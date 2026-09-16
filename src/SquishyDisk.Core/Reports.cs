using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SquishyDisk.Core;

public static class Reports
{
    public static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true,
        Converters = { new JsonStringEnumConverter() } };
    public static string Json(SessionResult session) => JsonSerializer.Serialize(session, JsonOptions);
    public static string Text(SessionResult session)
    {
        var s = session.Plan.Settings;
        var text = new StringBuilder();
        text.AppendLine($"SquishyDisk {session.AppVersion} | {session.EngineVersion}");
        text.AppendLine($"{session.StartedAt:yyyy-MM-dd HH:mm:ss zzz} | {session.Status}");
        text.AppendLine($"Target: {session.Plan.TargetFolder} | {session.TargetDescription}");
        text.AppendLine($"{session.Plan.Preset} | {Sizes.Format(s.FileSize)} | {s.Passes} passes | {s.DurationSeconds}s measured | {s.WarmupSeconds}s warm-up | {s.IntervalSeconds}s interval");
        text.AppendLine($"Data: {s.Data} | Cache: {s.Cache} | {session.OperatingSystem}");
        text.AppendLine(session.Aggregation);
        text.AppendLine();
        text.AppendLine("Workload                 Direction     MB/s       IOPS       Avg µs   Pass");
        foreach (var t in session.Plan.Tests)
        {
            var p = session.Best(t.Row, t.Direction);
            var label = $"{t.Workload.Label} {t.Workload.Detail}";
            text.AppendLine(p == null ? $"{label,-25} {t.Direction,-8}  —" :
                FormattableString.Invariant($"{label,-25} {t.Direction,-8} {p.Value(ResultUnit.MBs),10:0.00} {p.Iops,10:0.00} {p.Value(ResultUnit.Microseconds),10:0.00} {p.Pass,4}"));
        }
        if (session.Error != null) text.AppendLine("Error: " + session.Error);
        foreach (var warning in session.CleanupWarnings) text.AppendLine("Cleanup: " + warning);
        text.AppendLine();
        text.AppendLine(session.EngineSettings);
        return text.ToString();
    }
    public static string Csv(SessionResult session)
    {
        var text = new StringBuilder("schema_version,app_version,engine_version,started_at,status,target,preset,file_bytes,configured_passes,measured_seconds,warmup_seconds,interval_seconds,data,cache,row,workload,block_bytes,queue_per_thread,threads,direction,pass,is_best,actual_seconds,bytes,operations,MB_per_second,IOPS,average_microseconds,command,error\r\n");
        var s = session.Plan.Settings;
        if (session.Results.Count == 0)
        {
            object?[] empty = [session.SchemaVersion, session.AppVersion, session.EngineVersion, session.StartedAt.ToString("O"), session.Status,
                session.Plan.TargetFolder, session.Plan.Preset, s.FileSize, s.Passes, s.DurationSeconds, s.WarmupSeconds, s.IntervalSeconds,
                s.Data, s.Cache, null, null, null, null, null, null, null, null, null, null, null, null, null, null, null, session.Error];
            text.AppendLine(string.Join(",", empty.Select(CsvCell)));
        }
        foreach (var p in session.Results)
        {
            var t = session.Plan.Tests.First(t => t.Row == p.Row && t.Direction == p.Direction);
            object?[] values = [session.SchemaVersion, session.AppVersion, session.EngineVersion, session.StartedAt.ToString("O"), session.Status,
                session.Plan.TargetFolder, session.Plan.Preset, s.FileSize, s.Passes, s.DurationSeconds, s.WarmupSeconds, s.IntervalSeconds,
                s.Data, s.Cache, p.Row + 1, t.Workload.Pattern, t.Workload.BlockSize, t.Workload.QueueDepth, t.Workload.Threads,
                p.Direction, p.Pass, session.Best(p.Row, p.Direction) == p, p.Seconds, p.Bytes, p.Operations,
                p.Value(ResultUnit.MBs), p.Iops, p.Value(ResultUnit.Microseconds), p.Command, session.Error];
            text.AppendLine(string.Join(",", values.Select(CsvCell)));
        }
        return text.ToString();
    }
    private static string CsvCell(object? value)
    {
        var s = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
        // Keep exported text from being interpreted as a spreadsheet formula.
        if (value is string && s.Length > 0 && "=+-@\t\r".Contains(s[0])) s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
