using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SquishyDisk.Core;

public enum DriveOperation { Discover, Read, StartShort, StartExtended, Abort, Status }
public record DriveRequest(DriveOperation Operation, string? Token = null, string? Identity = null);
public record DriveDevice(string Token, string Name, string Type, string Protocol, int? DiskNumber = null, string[]? Volumes = null, string[]? SharedVolumes = null)
{
    public string Location => DiskNumber.HasValue ? $"Disk {DiskNumber} · {string.Join(", ", Volumes ?? [])}".TrimEnd(' ', '·') : Name;
}
public record DriveField(string Name, string Value);
public record SmartAttribute(string Id, string Name, string Current, string Worst, string Threshold, string Raw, string WhenFailed);
public record DriveSelfTest(bool Supported, bool StatusKnown, bool Active, string Status, int? Percent = null, int? ShortMinutes = null, int? ExtendedMinutes = null, string? LastResult = null);
public record DriveSnapshot
{
    public string AppVersion { get; init; } = ProductInfo.Version;
    public required DriveDevice Device { get; init; }
    public DateTimeOffset ReadAt { get; init; } = DateTimeOffset.Now;
    public string Model { get; init; } = "Unavailable";
    public string Serial { get; init; } = "Unavailable";
    public string Identity { get; init; } = "";
    public string Capacity { get; init; } = "Unavailable";
    public string Health { get; init; } = "Unavailable";
    public string ToolVersion { get; init; } = "7.5";
    public int ExitStatus { get; init; }
    public bool Partial { get; init; }
    public bool Stale { get; init; }
    public List<string> Limitations { get; init; } = [];
    public List<DriveField> Fields { get; init; } = [];
    public List<SmartAttribute> Attributes { get; init; } = [];
    public List<DriveField> Logs { get; init; } = [];
    public DriveSelfTest SelfTest { get; init; } = new(false, false, false, "Unavailable");
    public string RawJson { get; init; } = "{}";
    public string DisplayName => $"{Device.Location} · {Model} · {Capacity.Split(" (")[0]} · {Device.Protocol}";
    public override string ToString() => DisplayName;
}
public record DriveReply(List<DriveSnapshot>? Devices = null, DriveSnapshot? Snapshot = null, string? Message = null, bool Uncertain = false);
public interface IDriveInfoService : IAsyncDisposable
{
    Task<DriveReply> SendAsync(DriveRequest request, CancellationToken cancellation = default);
}

// A shared gate for UI actions, refreshes and benchmarks. Unknown outcomes stay blocked until reconciled.
public sealed class DriveActivity
{
    public bool Busy { get; private set; }
    public bool Benchmark { get; private set; }
    private readonly Dictionary<string, DriveSnapshot> active = [];
    public IReadOnlyCollection<DriveSnapshot> Active => active.Values;
    public bool TryDrive() { if (Busy || Benchmark) return false; Busy = true; return true; }
    public void EndDrive() => Busy = false;
    public bool TryBenchmark() { if (Busy || Benchmark || active.Count != 0) return false; Benchmark = true; return true; }
    public void EndBenchmark() => Benchmark = false;
    public void Observe(DriveSnapshot snapshot, bool uncertain = false)
    {
        string key = snapshot.Device.Name + "|" + snapshot.Device.Type;
        if (active.TryGetValue(key, out var previous))
        {
            if (previous.Identity.Length > 0 && snapshot.Identity.Length > 0 && previous.Identity != snapshot.Identity) return;
            if (!snapshot.SelfTest.StatusKnown) active[key] = previous with { Device = snapshot.Device };
        }
        if (snapshot.SelfTest.Active || uncertain) active[key] = snapshot;
        else if (snapshot.SelfTest.StatusKnown && !snapshot.Stale) active.Remove(key);
    }
}

public static class SmartParser
{
    public static JsonElement At(JsonElement node, params string[] path)
    {
        foreach (var part in path)
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out node)) return default;
        return node;
    }
    public static string? Value(JsonElement node) => node.ValueKind switch {
        JsonValueKind.String => node.GetString(), JsonValueKind.Number => node.GetRawText(),
        JsonValueKind.True => "Yes", JsonValueKind.False => "No", _ => null };
    public static string? Get(JsonElement node, params string[] path) => Value(At(node, path));
    public static int? Number(JsonElement node, params string[] path) => int.TryParse(Get(node, path), out int n) ? n : null;
    public static bool Yes(JsonElement node, params string[] path) => At(node, path).ValueKind == JsonValueKind.True;
    private static string Scalar(JsonElement node, string key) => Get(node, key + "_s") ?? Get(node, key) ?? "Unavailable";
    private static string Bytes(string? value) => BigInteger.TryParse(value, out var n) && n >= 0 ? $"{(double)n / 1e9:N2} GB ({n.ToString(CultureInfo.InvariantCulture)} bytes)" : "Unavailable";

    public static DriveSnapshot Parse(DriveDevice device, string json, int exitStatus)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || At(root, "smartctl").ValueKind != JsonValueKind.Object)
            throw new FormatException("The drive helper returned an unexpected response.");
        int code = exitStatus | (Number(root, "smartctl", "exit_status") ?? 0);
        string model = Get(root, "model_name") ?? Get(root, "scsi_model_name") ?? Get(root, "product") ?? "Unavailable";
        string serial = Get(root, "serial_number") ?? "Unavailable";
        string? capacity = Get(root, "user_capacity", "bytes_s") ?? Get(root, "user_capacity", "bytes") ?? Get(root, "nvme_total_capacity");
        string identity = serial != "Unavailable" && !string.IsNullOrWhiteSpace(serial) && model != "Unavailable" ? model + "|" + serial + "|" + capacity : "";
        var limitations = new List<string>();
        if ((code & 1) != 0) limitations.Add("The helper rejected the command.");
        if ((code & 2) != 0) limitations.Add("The device could not be opened or identified. Administrator access may be required.");
        if ((code & 4) != 0) limitations.Add("Some drive commands failed or returned incomplete data.");
        if ((code & 0xE0) != 0) limitations.Add("The drive records previous threshold, error-log or self-test events. Review the logs.");
        var messages = At(root, "smartctl", "messages");
        if (messages.ValueKind == JsonValueKind.Array)
            limitations.AddRange(messages.EnumerateArray().Select(m => Get(m, "string")).OfType<string>());
        var nvme = At(root, "nvme_smart_health_information_log");
        bool? passed = At(root, "smart_status", "passed").ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
        string health = passed == false || (code & 8) != 0 ? "Failing" : (Number(nvme, "critical_warning") is > 0 || (code & 16) != 0) ? "Warning" : passed == true ? "Passed" : "Unavailable";
        var fields = new List<DriveField>();
        void Add(string name, string? value) => fields.Add(new(name, value ?? "Unavailable"));
        Add("Model", model); Add("Serial number", serial); Add("Firmware", Get(root, "firmware_version") ?? Get(root, "revision"));
        Add("Capacity", Bytes(capacity)); Add("Protocol", Get(root, "device", "protocol") ?? device.Protocol);
        Add("Interface / link", Get(root, "sata_version", "string") ?? Get(root, "nvme_version", "string") ?? Get(root, "scsi_transport_protocol", "name"));
        bool isNvme = nvme.ValueKind == JsonValueKind.Object || string.Equals(Get(root, "device", "protocol") ?? device.Protocol, "NVMe", StringComparison.OrdinalIgnoreCase);
        Add("Current link", Get(root, "interface_speed", "current", "string") ?? (isNvme ? "Not reported by smartctl for NVMe" : null));
        Add("Temperature (°C)", Get(root, "temperature", "current") ?? Get(nvme, "temperature"));
        Add("Power-on hours", Get(root, "power_on_time", "hours") ?? Get(nvme, "power_on_hours_s") ?? Get(nvme, "power_on_hours"));
        Add("Power cycles", Get(root, "power_cycle_count") ?? Get(nvme, "power_cycles_s") ?? Get(nvme, "power_cycles"));
        if (nvme.ValueKind == JsonValueKind.Object || At(root, "endurance_used").ValueKind == JsonValueKind.Object || At(root, "spare_available").ValueKind == JsonValueKind.Object)
        {
            Add("Endurance used (%)", Get(root, "endurance_used", "current_percent") ?? Get(nvme, "percentage_used"));
            Add("Available spare (%)", Get(root, "spare_available", "current_percent") ?? Get(nvme, "available_spare"));
            Add("Spare threshold (%)", Get(root, "spare_available", "threshold_percent") ?? Get(nvme, "available_spare_threshold"));
        }
        if (nvme.ValueKind == JsonValueKind.Object) Add("NVMe critical warning", Get(nvme, "critical_warning"));
        Add("Host data read", HostBytes(root, nvme, true)); Add("Host data written", HostBytes(root, nvme, false));
        var attributes = new List<SmartAttribute>();
        var table = At(root, "ata_smart_attributes", "table");
        if (table.ValueKind == JsonValueKind.Array)
            foreach (var a in table.EnumerateArray()) attributes.Add(new(Get(a, "id") ?? "", Get(a, "name") ?? "", Get(a, "value") ?? "—", Get(a, "worst") ?? "—", Get(a, "thresh") ?? "—", Get(a, "raw", "string") ?? Get(a, "raw", "value_s") ?? Get(a, "raw", "value") ?? "—", Get(a, "when_failed") ?? ""));
        // Named protocol fields and logs are flattened without converting numbers to floating point.
        var logs = new List<DriveField>();
        foreach (var p in root.EnumerateObject())
        {
            if (p.Name.Contains("log", StringComparison.Ordinal) || p.Name.StartsWith("scsi_self_test_", StringComparison.Ordinal) || p.Name.Contains("error_counter", StringComparison.Ordinal) || p.Name == "ata_device_statistics") Flatten(p.Value, p.Name, logs);
            else if (p.Name.StartsWith("scsi_", StringComparison.Ordinal)) Flatten(p.Value, p.Name, fields);
        }
        string version = "7.5";
        var v = At(root, "smartctl", "version");
        if (v.ValueKind == JsonValueKind.Array) version = string.Join(".", v.EnumerateArray().Select(Value));
        return new() { Device = device, Model = model, Serial = serial, Identity = identity, Capacity = Bytes(capacity), Health = health,
            ToolVersion = version, ExitStatus = code, Partial = (code & 7) != 0, Limitations = limitations.Distinct().ToList(),
            Fields = fields, Attributes = attributes, Logs = logs, SelfTest = ParseSelfTest(root), RawJson = json };
    }
    private static string? HostBytes(JsonElement root, JsonElement nvme, bool read)
    {
        if (BigInteger.TryParse(Scalar(nvme, read ? "data_units_read" : "data_units_written"), out var units) && units >= 0)
            return Bytes((units * 512000).ToString(CultureInfo.InvariantCulture));
        var pages = At(root, "ata_device_statistics", "pages");
        if (pages.ValueKind == JsonValueKind.Array && Number(root, "logical_block_size") is > 0 and var blockSize)
            foreach (var page in pages.EnumerateArray())
            {
                var table = At(page, "table");
                if (Number(page, "number") != 1 || table.ValueKind != JsonValueKind.Array) continue;
                foreach (var entry in table.EnumerateArray())
                    if (Number(entry, "offset") == (read ? 40 : 24) && Yes(entry, "flags", "valid") && !Yes(entry, "flags", "normalized") && BigInteger.TryParse(Scalar(entry, "value"), out var sectors) && sectors >= 0)
                        return Bytes((sectors * blockSize).ToString(CultureInfo.InvariantCulture));
            }
        string? scsi = Get(root, "scsi_error_counter_log", read ? "read" : "write", "gigabytes_processed");
        return scsi == null ? null : scsi + " GB (drive-reported)";
    }
    public static DriveSelfTest ParseSelfTest(JsonElement root)
    {
        var ata = At(root, "ata_smart_data", "self_test");
        int? value = Number(ata, "status", "value");
        if (value.HasValue || At(root, "ata_smart_data").ValueKind == JsonValueKind.Object)
        {
            bool active = value is >= 240 and <= 255;
            int? remaining = Number(ata, "status", "remaining_percent");
            return new(Yes(root, "ata_smart_data", "capabilities", "self_tests_supported"), value.HasValue, active,
                Get(ata, "status", "string") ?? "Status unavailable", active && remaining is >= 0 and <= 100 ? 100 - remaining : null,
                Number(ata, "polling_minutes", "short"), Number(ata, "polling_minutes", "extended"));
        }
        var nvme = At(root, "nvme_self_test_log");
        if (nvme.ValueKind == JsonValueKind.Object || At(root, "nvme_optional_admin_commands").ValueKind == JsonValueKind.Object)
        {
            value = Number(nvme, "current_self_test_operation", "value");
            // NVMe reports the current operation separately from completed test results.
            var table = At(nvme, "table");
            var last = table.ValueKind == JsonValueKind.Array ? table.EnumerateArray().FirstOrDefault() : default;
            string? lastResult = Get(last, "self_test_result", "string");
            if (lastResult != null)
            {
                lastResult = (Get(last, "self_test_code", "string") ?? "Self-test") + ": " + lastResult;
                if (Get(last, "power_on_hours") is string hours) lastResult += $" (power-on hour {hours})";
            }
            return new(Yes(root, "nvme_optional_admin_commands", "self_test"), value.HasValue, value is > 0,
                Get(nvme, "current_self_test_operation", "string") ?? "Status unavailable", Number(nvme, "current_self_test_completion_percent"), LastResult: lastResult);
        }
        var scsiTests = root.EnumerateObject().Where(p => p.Name.StartsWith("scsi_self_test_", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.Object).ToList();
        bool scsiActive = scsiTests.Any(p => Yes(p.Value, "self_test_in_progress") || Number(p.Value, "result", "value") == 15);
        bool scsiKnown = scsiTests.Count > 0;
        int? seconds = Number(root, "scsi_extended_self_test_seconds");
        int? percent = null;
        // smartctl 7.5 only emits SCSI sense progress and an empty self-test log as text.
        // --json=ov retains those original lines inside the JSON response.
        var output = At(root, "smartctl", "output");
        if (output.ValueKind == JsonValueKind.Array)
            foreach (var line in output.EnumerateArray().Select(Value).OfType<string>())
            {
                if (line.Trim() == "No Self-tests have been logged") scsiKnown = true;
                var progress = Regex.Match(line, @"^Self-test execution status:\s*(\d+)% of test remaining$");
                if (progress.Success && int.TryParse(progress.Groups[1].Value, out int remaining) && remaining is >= 0 and <= 100)
                { scsiActive = scsiKnown = true; percent = 100 - remaining; }
            }
        return new(scsiKnown || seconds.HasValue, scsiKnown, scsiActive, scsiActive ? "Self-test in progress" : scsiKnown ? "No self-test running" : "Status unavailable", percent, null, seconds.HasValue ? (seconds + 59) / 60 : null);
    }
    public static void Flatten(JsonElement node, string path, List<DriveField> output)
    {
        if (node.ValueKind == JsonValueKind.Object) foreach (var p in node.EnumerateObject()) Flatten(p.Value, path + "." + p.Name, output);
        else if (node.ValueKind == JsonValueKind.Array) { int i = 0; foreach (var item in node.EnumerateArray()) Flatten(item, $"{path}[{i++}]", output); }
        else if (Value(node) is string value) output.Add(new(path, value));
    }
}

public static class DriveReports
{
    public static string Text(DriveSnapshot s) => $"SquishyDisk {s.AppVersion} · Drive Info\nRead: {s.ReadAt:O}\nReading: {(s.Stale ? "Stale" : s.Partial ? "Partial" : "Complete")}\nDevice: {s.Device.Location} ({s.Device.Name}, {s.Device.Type})\nsmartctl {s.ToolVersion} · exit bits: {s.ExitStatus}\nHealth: {s.Health}\nSelf-test: {s.SelfTest.Status}\n" +
        (s.SelfTest.LastResult == null ? "" : $"Last self-test: {s.SelfTest.LastResult}\n") +
        string.Join("\n", s.Limitations.Select(x => "Note: " + x)) + "\n\n" + string.Join("\n", s.Fields.Select(x => $"{x.Name}: {x.Value}")) + "\n\nSMART attributes\n" +
        string.Join("\n", s.Attributes.Select(a => $"{a.Id} {a.Name} · current {a.Current} · worst {a.Worst} · threshold {a.Threshold} · raw {a.Raw} · {a.WhenFailed}")) +
        "\n\nLogs and protocol data\n" + string.Join("\n", s.Logs.Select(x => $"{x.Name}: {x.Value}"));
    public static string Json(DriveSnapshot s) => JsonSerializer.Serialize(s, Reports.JsonOptions);
    public static string Csv(DriveSnapshot s)
    {
        static string C(string v) => "\"" + ((v.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' or '\t' or '\r') ? "'" : "") + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var b = new StringBuilder("AppVersion,ReadAt,Device,Model,Serial,Smartctl,Section,ID,Name,Current,Worst,Threshold,Raw,WhenFailed\r\n");
        void Row(params string[] cells) => b.AppendLine(string.Join(',', new[] { s.AppVersion, s.ReadAt.ToString("O"), s.Device.Name, s.Model, s.Serial, s.ToolVersion }.Concat(cells).Select(C)));
        Row("Summary", "", "Health", "", "", "", s.Health, "");
        Row("Summary", "", "Reading", "", "", "", s.Stale ? "Stale" : s.Partial ? "Partial" : "Complete", "");
        Row("Summary", "", "Self-test", "", "", "", s.SelfTest.Status, "");
        if (s.SelfTest.LastResult != null) Row("Summary", "", "Last self-test", "", "", "", s.SelfTest.LastResult, "");
        foreach (var note in s.Limitations) Row("Limitations", "", "Note", "", "", "", note, "");
        foreach (var f in s.Fields) Row("Summary", "", f.Name, "", "", "", f.Value, "");
        foreach (var a in s.Attributes) Row("ATA", a.Id, a.Name, a.Current, a.Worst, a.Threshold, a.Raw, a.WhenFailed);
        foreach (var f in s.Logs) Row("Logs", "", f.Name, "", "", "", f.Value, "");
        return b.ToString();
    }
}
