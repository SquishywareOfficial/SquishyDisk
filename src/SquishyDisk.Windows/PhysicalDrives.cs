using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace SquishyDisk.Windows;

public static class PhysicalDrives
{
    public static int? DiskNumber(string name, string type)
    {
        // Only plain Windows disk devices map this way. RAID member addressing is deliberately excluded.
        if (type.Contains(',') || type.StartsWith("megaraid", StringComparison.Ordinal)) return null;
        var match = Regex.Match(name, "^/dev/sd([a-z]+)$", RegexOptions.CultureInvariant);
        int? number = null;
        if (match.Success) { int n = 0; foreach (char c in match.Groups[1].Value) n = n * 26 + c - 'a' + 1; number = n - 1; }
        else if (Regex.Match(name, "^/dev/pd([0-9]+)$") is { Success: true } pd && int.TryParse(pd.Groups[1].Value, out int n)) number = n;
        if (!number.HasValue) return null;
        using var handle = Open(@"\\.\PhysicalDrive" + number.Value);
        byte[] buffer = new byte[12];
        return !handle.IsInvalid && DeviceIoControl(handle, 0x2D1080, IntPtr.Zero, 0, buffer, buffer.Length, out int bytes, IntPtr.Zero) && bytes >= 12 && BitConverter.ToInt32(buffer, 4) == number.Value ? number : null;
    }
    public static Dictionary<string, int[]> VolumeMap()
    {
        var result = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
            using var h = Open(@"\\.\" + drive.Name.TrimEnd('\\'));
            byte[] buffer = new byte[8192];
            if (h.IsInvalid || !DeviceIoControl(h, 0x560000, IntPtr.Zero, 0, buffer, buffer.Length, out int bytes, IntPtr.Zero) || bytes < 8) continue;
            int count = BitConverter.ToInt32(buffer);
            if (count < 1 || count > (bytes - 8) / 24) continue;
            result[drive.Name] = Enumerable.Range(0, count).Select(i => BitConverter.ToInt32(buffer, 8 + i * 24)).Distinct().ToArray();
        }
        return result;
    }
    public static string VolumeRoot(string folder)
    {
        var buffer = new System.Text.StringBuilder(1024);
        return GetVolumePathName(folder, buffer, buffer.Capacity) ? buffer.ToString() : Path.GetPathRoot(folder) ?? "";
    }
    private static SafeFileHandle Open(string path) => CreateFile(path, 0, 7, IntPtr.Zero, 3, 0, IntPtr.Zero);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, IntPtr input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetVolumePathName(string path, System.Text.StringBuilder volume, int length);
}
