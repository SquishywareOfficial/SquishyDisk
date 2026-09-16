using System.Text.Json;
using SquishyDisk.Core;

namespace SquishyDisk.Windows;

public sealed record Preferences
{
    public int SchemaVersion { get; init; } = 1;
    public string Theme { get; init; } = "System";
    public string Preset { get; init; } = "Standard";
    public string TargetFolder { get; init; } = "";
    public ResultUnit Unit { get; init; } = ResultUnit.MBs;
    public BenchmarkSettings Settings { get; init; } = new();
    public List<Workload> Workloads { get; init; } = Presets.Standard();
}

public static class PreferenceStore
{
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "SquishyDisk.settings.json");
    public static Preferences LoadWithLegacyFallback(string path, out string? warning)
    {
        if (File.Exists(path)) return Load(path, out warning);
        var legacy = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, "DiskSpdUI.settings.json");
        var preferences = Load(legacy, out warning);
        if (File.Exists(legacy) && warning == null && !Save(path, preferences))
            warning = "Previous settings were loaded. This folder is read-only, so they will be kept for this session only.";
        return preferences;
    }
    public static Preferences Load(string path, out string? warning)
    {
        warning = null;
        try
        {
            if (!File.Exists(path)) return new();
            if (new FileInfo(path).Length > 65536) throw new FormatException("The settings file is too large.");
            var prefs = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path), Reports.JsonOptions)
                ?? throw new FormatException("Empty settings file.");
            if (prefs.SchemaVersion != 1 || prefs.Workloads == null || prefs.Workloads.Count != 4 || prefs.Settings == null ||
                prefs.Workloads.Any(w => w == null) || prefs.Theme is not ("System" or "Light" or "Dark") ||
                prefs.Preset is not ("Standard" or "NVMe" or "Custom") || !Enum.IsDefined(prefs.Unit))
                throw new FormatException("Unsupported settings.");
            PlanValidation.Validate(new(Path.GetTempPath(), prefs.Preset, prefs.Settings,
                prefs.Workloads.Select((w, i) => new TestCase(i, w, TestDirection.Read)).ToList()));
            return prefs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
        { warning = "Settings could not be loaded. Defaults are in use. " + ex.Message; return new(); }
    }
    public static bool Save(string path, Preferences preferences)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(preferences, Reports.JsonOptions)); File.Move(temp, path, true); return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
        finally { try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
