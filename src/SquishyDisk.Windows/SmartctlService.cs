using System.ComponentModel;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SquishyDisk.Core;

namespace SquishyDisk.Windows;

public sealed class SmartctlPayload : IDisposable
{
    public const string ExeHash = "b5db94e5082c042be44994b7a4fa8f7b5c8e713b2ab1c9a560d8f7a7995ea27d";
    public const string DatabaseHash = "dd39c6a520d38895da61923fe26fe7c9c5eb3f42325f2e4477a06ed7a61966d0";
    private readonly List<FileStream> locks = [];
    private readonly string folder;
    public string Executable => Path.Combine(folder, "smartctl.exe");
    public string Database => Path.Combine(folder, "drivedb.h");
    public static bool Elevated { get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); } }
    public static string Notice => Read("COPYING.txt") + "\n\n" + Read("AUTHORS.txt");
    private static string Read(string name) { using var r = new StreamReader(typeof(SmartctlPayload).Assembly.GetManifestResourceStream("Smartctl." + name)!); return r.ReadToEnd(); }
    public SmartctlPayload()
    {
        // A fresh ACL-protected directory avoids elevating an executable from a user-writable cache.
        var parent = Elevated ? Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles) : Path.GetTempPath();
        for (DirectoryInfo? p = new(parent); p != null; p = p.Parent)
            if ((p.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The helper directory cannot use a redirected path.");
        folder = Path.Combine(parent, "SquishyDisk-smartctl-" + Guid.NewGuid().ToString("N"));
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        using var identity = WindowsIdentity.GetCurrent();
        var owner = Elevated ? new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) : identity.User!;
        acl.SetOwner(owner);
        foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(folder).Create(acl);
        try { Extract("smartctl.exe", ExeHash); Extract("drivedb.h", DatabaseHash); }
        catch { Dispose(); throw; }
    }
    private void Extract(string name, string hash)
    {
        string path = Path.Combine(folder, name);
        using (var source = typeof(SmartctlPayload).Assembly.GetManifestResourceStream("Smartctl." + name)!)
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None)) source.CopyTo(file);
        var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        locks.Add(held);
        if (!Convert.ToHexString(SHA256.HashData(held)).Equals(hash, StringComparison.OrdinalIgnoreCase)) throw new IOException("The bundled drive helper checksum does not match.");
    }
    public void Dispose()
    {
        foreach (var held in locks) held.Dispose(); locks.Clear();
        try { File.Delete(Executable); File.Delete(Database); Directory.Delete(folder, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

public record SmartctlOutput(int ExitCode, string Json, string Error);
public interface ISmartctlRunner : IDisposable
{
    Task<SmartctlOutput> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellation);
}
public sealed class SmartctlRunner : ISmartctlRunner
{
    private SmartctlPayload? payload;
    public async Task<SmartctlOutput> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellation)
    {
        payload ??= new();
        var info = new ProcessStartInfo(payload.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = Path.GetDirectoryName(payload.Executable)! };
        info.ArgumentList.Add("-B"); info.ArgumentList.Add(payload.Database);
        info.ArgumentList.Add("--json=ov");
        foreach (var arg in arguments) info.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        using var job = new ProcessJob();
        using var process = Process.Start(info) ?? throw new IOException("The drive helper did not start.");
        try
        {
            job.Assign(process);
            Task<string> stdout = ReadLimited(process.StandardOutput, timeout.Token), stderr = ReadLimited(process.StandardError, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).ConfigureAwait(false);
            return new(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested) { throw new TimeoutException("The drive did not respond within 35 seconds."); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync().ConfigureAwait(false); } }
    }
    private static async Task<string> ReadLimited(StreamReader reader, CancellationToken cancellation)
    {
        var result = new StringBuilder(); var buffer = new char[8192]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation).ConfigureAwait(false)) != 0)
        {
            if (result.Length + count > 4 * 1024 * 1024) throw new IOException("The drive helper response exceeds the size limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
    public void Dispose() => payload?.Dispose();
}

public sealed class SmartctlService(ISmartctlRunner? runner = null) : IDriveInfoService
{
    private readonly ISmartctlRunner runner = runner ?? new SmartctlRunner();
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, DriveDevice> devices = [];
    private readonly Dictionary<string, DriveSnapshot> snapshots = [];
    private readonly HashSet<string> uncertain = [];
    public async Task<DriveReply> SendAsync(DriveRequest request, CancellationToken cancellation = default)
    {
        if (!Enum.IsDefined(request.Operation)) throw new ArgumentException("Unsupported drive operation.");
        await gate.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            if (request.Operation == DriveOperation.Discover) return await Discover(cancellation).ConfigureAwait(false);
            if (request.Token == null || !devices.TryGetValue(request.Token, out var device)) throw new IOException("The device selection is no longer valid. Rescan drives.");
            if (request.Operation is DriveOperation.Read or DriveOperation.Status) return new(Snapshot: await Read(device, cancellation).ConfigureAwait(false));
            var before = await Read(device, cancellation).ConfigureAwait(false);
            if (string.IsNullOrEmpty(request.Identity) || before.Identity != request.Identity) throw new IOException("Drive identity could not be verified or has changed. Refresh before trying again.");
            if (!before.SelfTest.StatusKnown) throw new IOException("The drive did not report its self-test status. No command was sent.");
            if (request.Operation == DriveOperation.Abort && !before.SelfTest.Active) return new(Snapshot: before, Message: "No self-test is running.");
            if (request.Operation != DriveOperation.Abort && (!before.SelfTest.Supported || before.SelfTest.Active || snapshots.Values.Any(s => s.SelfTest.Active) || uncertain.Count > 0))
                throw new IOException("A self-test is already running, its status is unresolved, or this drive does not support self-tests.");
            string[] command = request.Operation switch { DriveOperation.StartShort => ["-t", "short"], DriveOperation.StartExtended => ["-t", "long"], DriveOperation.Abort => ["-X"], _ => throw new ArgumentException("Unsupported action.") };
            string? message = null;
            bool ambiguous = false;
            try
            {
                var result = await runner.RunAsync([.. command, "-d", device.Type, device.Name], cancellation).ConfigureAwait(false);
                var action = SmartParser.Parse(device, result.Json, result.ExitCode);
                message = string.Join("\n", action.Limitations);
                ambiguous = (result.ExitCode & 7) == 0; // Accepted commands still need status confirmation.
                if ((result.ExitCode & 7) != 0 && message.Length == 0) message = result.Error;
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or JsonException or FormatException or OperationCanceledException)
            { message = "Command outcome unknown: " + ex.Message; ambiguous = true; }
            uncertain.Add(device.Token);
            if (cancellation.IsCancellationRequested) return new(Snapshot: before, Message: message ?? "Command outcome is unknown.", Uncertain: true);
            try
            {
                // A separate bounded read reconciles an action; the action itself is never retried.
                using var reconcile = new CancellationTokenSource(TimeSpan.FromSeconds(40));
                var after = await Read(device, reconcile.Token).ConfigureAwait(false);
                bool expected = request.Operation == DriveOperation.Abort ? !after.SelfTest.Active : after.SelfTest.Active;
                bool unresolved = !after.SelfTest.StatusKnown || (ambiguous && !expected);
                if (unresolved) uncertain.Add(device.Token);
                return new(Snapshot: after, Message: string.IsNullOrWhiteSpace(message) ? (expected ? (request.Operation == DriveOperation.Abort ? "Self-test stopped." : "Self-test started.") : "The requested state was not confirmed. Refresh status before continuing.") : message, Uncertain: unresolved);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or JsonException or FormatException)
            { return new(Snapshot: before, Message: (message ?? "Command sent.") + "\nStatus could not be confirmed: " + ex.Message, Uncertain: true); }
        }
        finally { gate.Release(); }
    }
    private async Task<DriveReply> Discover(CancellationToken cancellation)
    {
        var result = await runner.RunAsync(["--scan"], cancellation).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(result.Json);
        if ((result.ExitCode & 3) != 0) throw new IOException("Drive discovery failed. Try Enable drive access.");
        var array = SmartParser.At(doc.RootElement, "devices");
        if (array.ValueKind != JsonValueKind.Array) return new(Devices: [], Message: "No supported drives found. Try Enable drive access, then Rescan.");
        var found = new List<DriveSnapshot>();
        var volumes = PhysicalDrives.VolumeMap();
        foreach (var item in array.EnumerateArray().Take(64))
        {
            string? name = SmartParser.Get(item, "name"), type = SmartParser.Get(item, "type");
            if (name == null || type == null || name.Length > 256 || type.Length > 128) continue;
            var previous = devices.Values.FirstOrDefault(d => d.Name == name && d.Type == type);
            int? number = PhysicalDrives.DiskNumber(name, type);
            var device = new DriveDevice(previous?.Token ?? Guid.NewGuid().ToString("N"), name, type, SmartParser.Get(item, "protocol") ?? "Unknown", number,
                number.HasValue ? volumes.Where(p => p.Value.Contains(number.Value)).Select(p => p.Key).ToArray() : [],
                number.HasValue ? volumes.Where(p => p.Value.Contains(number.Value) && p.Value.Length > 1).Select(p => p.Key).ToArray() : []);
            devices[device.Token] = device;
            try { found.Add(await Read(device, cancellation).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is IOException or TimeoutException or JsonException or FormatException or Win32Exception)
            { found.Add(new() { Device = device, Partial = true, Limitations = [ex.Message] }); }
        }
        // Keep old tokens only for a previously active test whose device may have been unplugged.
        foreach (var token in devices.Keys.Except(found.Select(s => s.Device.Token)).ToList())
            if (!snapshots.TryGetValue(token, out var old) || (!old.SelfTest.Active && !uncertain.Contains(token))) devices.Remove(token);
        return new(Devices: found);
    }
    private async Task<DriveSnapshot> Read(DriveDevice device, CancellationToken cancellation)
    {
        var result = await runner.RunAsync(["-x", "-d", device.Type, device.Name], cancellation).ConfigureAwait(false);
        var snapshot = SmartParser.Parse(device, result.Json, result.ExitCode);
        if (snapshots.TryGetValue(device.Token, out var prior) && prior.Identity.Length > 0 && snapshot.Identity.Length > 0 && prior.Identity != snapshot.Identity && (prior.SelfTest.Active || uncertain.Contains(device.Token)))
            throw new IOException("A different drive now occupies this device address. The previous self-test status is unknown.");
        snapshots[device.Token] = snapshot;
        if (snapshot.SelfTest.StatusKnown) uncertain.Remove(device.Token);
        return snapshot;
    }
    public ValueTask DisposeAsync() { runner.Dispose(); gate.Dispose(); return ValueTask.CompletedTask; }
}
