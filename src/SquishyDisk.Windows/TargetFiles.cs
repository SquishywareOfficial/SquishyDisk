using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SquishyDisk.Core;

namespace SquishyDisk.Windows;

public sealed record TargetLease(string Id, string DirectoryPath, string JournalPath)
{
    public string FilePath => Path.Combine(DirectoryPath, "test.dat");
}

public static class TargetFiles
{
    public static string JournalRoot => Path.Combine(EngineStore.CacheRoot, "runs");

    public static void Preflight(BenchmarkPlan plan)
    {
        PlanValidation.Validate(plan);
        if (!Directory.Exists(plan.TargetFolder)) throw new IOException("The selected folder is unavailable. Reconnect the drive or choose another folder.");
        if (GetDiskFreeSpaceEx(plan.TargetFolder, out var free, out _, out _) && free < (ulong)plan.Settings.FileSize + 64 * 1048576UL)
            throw new IOException("There is not enough free space for the test file plus 64 MiB of spare space.");
        var root = Path.GetPathRoot(plan.TargetFolder)!;
        try
        {
            var drive = new DriveInfo(root);
            if (drive.DriveFormat.Equals("FAT32", StringComparison.OrdinalIgnoreCase) && plan.Settings.FileSize >= 4294967296L)
                throw new IOException("FAT32 requires a test file smaller than 4 GiB.");
        }
        catch (ArgumentException) { /* UNC volume: file creation is the authority. */ }
        if (GetDiskFreeSpace(root, out _, out var sectorSize, out _, out _) &&
            plan.Tests.Any(t => t.Workload.BlockSize % sectorSize != 0))
            throw new IOException("The selected block size is not aligned to this volume's sector size.");
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        var required = plan.Tests.Max(t => (long)t.Workload.BlockSize * t.Workload.QueueDepth * t.Workload.Threads) + 16 * 1048576L;
        if (GlobalMemoryStatusEx(ref memory) && (ulong)required > memory.AvailablePhysical / 2)
            throw new IOException("This workload requires too much available memory. Reduce block size, queue depth, or threads.");
    }

    public static TargetLease Create(string target)
    {
        Directory.CreateDirectory(JournalRoot);
        RejectReparseAncestors(JournalRoot);
        var id = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetFullPath(target), ".squishydisk-" + id);
        Directory.CreateDirectory(dir);
        var lease = new TargetLease(id, dir, Path.Combine(JournalRoot, id + ".json"));
        try
        {
            // A marker is required in addition to a journal. Cleanup never recursively deletes a target directory.
            File.WriteAllText(Path.Combine(dir, "owner.txt"), id);
            File.WriteAllText(lease.JournalPath, JsonSerializer.Serialize(lease));
            return lease;
        }
        catch { try { Cleanup(lease); } catch { /* Preserve the original error. */ } throw; }
    }

    public static async Task PrepareAsync(TargetLease lease, BenchmarkSettings settings, Action<double> progress, CancellationToken token)
    {
        var buffer = new byte[1048576];
        await using var stream = new FileStream(lease.FilePath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            1048576, FileOptions.Asynchronous | FileOptions.SequentialScan);
        for (long written = 0; written < settings.FileSize;)
        {
            token.ThrowIfCancellationRequested();
            if (settings.Data == DataPattern.Random) RandomNumberGenerator.Fill(buffer);
            int length = (int)Math.Min(buffer.Length, settings.FileSize - written);
            await stream.WriteAsync(buffer.AsMemory(0, length), token);
            written += length;
            progress((double)written / settings.FileSize);
        }
        token.ThrowIfCancellationRequested();
        stream.Flush(true);
    }

    public static void Cleanup(TargetLease lease)
    {
        if (!Guid.TryParseExact(lease.Id, "N", out _)) throw new IOException("Invalid cleanup record.");
        var full = Path.GetFullPath(lease.DirectoryPath);
        if (Path.GetFileName(full) != ".squishydisk-" + lease.Id || full != lease.DirectoryPath ||
            !Path.GetFullPath(lease.JournalPath).Equals(Path.Combine(Path.GetFullPath(JournalRoot), lease.Id + ".json"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Cleanup record does not identify an owned test directory.");
        if (Directory.Exists(full))
        {
            RejectReparseAncestors(full);
            var owner = Path.Combine(full, "owner.txt");
            if (!File.Exists(owner) || (File.GetAttributes(owner) & FileAttributes.ReparsePoint) != 0 || File.ReadAllText(owner) != lease.Id)
                throw new IOException("The test file ownership marker has changed; automatic cleanup was skipped.");
            // Each deletion is an exact file, within the validated owned directory.
            if (File.Exists(lease.FilePath))
            {
                if ((File.GetAttributes(lease.FilePath) & FileAttributes.ReparsePoint) != 0) throw new IOException("The test file became a link; cleanup was skipped.");
                File.Delete(lease.FilePath);
            }
            if (Directory.EnumerateFileSystemEntries(full).Any(p => !p.Equals(owner, StringComparison.OrdinalIgnoreCase)))
                throw new IOException("Unexpected files are present in the test directory; they were left untouched.");
            File.Delete(owner);
            Directory.Delete(full, false);
        }
        if (File.Exists(lease.JournalPath)) File.Delete(lease.JournalPath);
    }

    public static List<string> Recover()
    {
        var warnings = new List<string>();
        if (!Directory.Exists(JournalRoot)) return warnings;
        try { RejectReparseAncestors(JournalRoot); }
        catch (IOException ex) { return [ex.Message]; }
        foreach (var journal in Directory.EnumerateFiles(JournalRoot, "*.json"))
        {
            try
            {
                if (new FileInfo(journal).Length > 16384) throw new IOException("Oversized recovery record.");
                var lease = JsonSerializer.Deserialize<TargetLease>(File.ReadAllText(journal)) ?? throw new IOException("Invalid recovery record.");
                if (!lease.JournalPath.Equals(journal, StringComparison.OrdinalIgnoreCase)) throw new IOException("Mismatched recovery record.");
                // If an external drive vanished, leave its journal until it is reconnected.
                var parent = Path.GetDirectoryName(lease.DirectoryPath);
                if (!Directory.Exists(parent)) { warnings.Add($"Reconnect the drive to clean up {lease.DirectoryPath}"); continue; }
                Cleanup(lease);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { warnings.Add($"Recovery: {journal}: {ex.Message}"); }
        }
        return warnings;
    }

    public static void RejectReparseAncestors(string path)
    {
        for (var dir = new DirectoryInfo(Path.GetFullPath(path)); dir != null; dir = dir.Parent)
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Choose a folder without directory links or junctions for this operation.");
    }

    public static string Describe(string folder)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            return $"{drive.VolumeLabel} · {drive.DriveFormat} · {Sizes.Format(drive.TotalSize)} total · {Sizes.Format(drive.AvailableFreeSpace)} free";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        { return "Selected folder · " + folder; }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDiskFreeSpaceEx(string directory, out ulong free, out ulong total, out ulong totalFree);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetDiskFreeSpace(string root, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint clusters);
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended; }
    [DllImport("kernel32.dll")][return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}
