using System.Reflection;
using System.Security.Cryptography;

namespace SquishyDisk.Windows;

public static class EngineStore
{
    public static string CacheRoot => Path.Combine(Path.GetTempPath(), "SquishyDisk");
    public static string License => ReadText("DiskSpd.License.txt");
    public static string Hash => ReadText("DiskSpd.Engine.sha256").Trim();
    private static Stream Resource(string name) => typeof(EngineStore).Assembly.GetManifestResourceStream(name)
        ?? throw new InvalidOperationException("The bundled DiskSpd engine is incomplete.");
    private static string ReadText(string name) { using var r = new StreamReader(Resource(name)); return r.ReadToEnd(); }
    public static string Extract()
    {
        var folder = Path.Combine(CacheRoot, "engine", Hash);
        Directory.CreateDirectory(folder);
        TargetFiles.RejectReparseAncestors(folder);
        var path = Path.Combine(folder, "diskspd.exe");
        if (File.Exists(path) && Verify(path)) return path;
        var temp = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = Resource("DiskSpd.Engine.exe"))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
            if (!Verify(temp)) throw new InvalidOperationException("The bundled DiskSpd checksum does not match.");
            File.Move(temp, path, true);
            return path;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static bool Verify(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        using var file = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(file)).Equals(Hash, StringComparison.OrdinalIgnoreCase);
    }
}
