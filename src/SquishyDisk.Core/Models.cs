using System.Text.Json.Serialization;

namespace SquishyDisk.Core;

public enum AccessPattern { Sequential, Random }
public enum TestDirection { Read, Write }
public enum DataPattern { Random, Zero }
public enum CacheMode { Unbuffered, WriteThrough }
public enum SessionStatus { Running, Completed, Cancelled, Failed }
public enum ResultUnit { MBs, GBs, IOPS, Microseconds }

public sealed record Workload(string Name, AccessPattern Pattern, int BlockSize, int QueueDepth, int Threads)
{
    [JsonIgnore] public string Label => $"{(Pattern == AccessPattern.Sequential ? "SEQ" : "RND")} {Sizes.Format(BlockSize)}";
    [JsonIgnore] public string Detail => $"Q{QueueDepth} · T{Threads}";
}

public static class Presets
{
    public static List<Workload> Standard() => [
        new("Sequential 1", AccessPattern.Sequential, 1048576, 8, 1),
        new("Sequential 2", AccessPattern.Sequential, 1048576, 1, 1),
        new("Random 1", AccessPattern.Random, 4096, 32, 1),
        new("Random 2", AccessPattern.Random, 4096, 1, 1)];
    public static List<Workload> Nvme() => [
        new("Sequential 1", AccessPattern.Sequential, 1048576, 8, 1),
        new("Sequential 2", AccessPattern.Sequential, 131072, 32, 1),
        new("Random 1", AccessPattern.Random, 4096, 32, 16),
        new("Random 2", AccessPattern.Random, 4096, 1, 1)];
}

public sealed record BenchmarkSettings
{
    public long FileSize { get; init; } = 1073741824;
    public int Passes { get; init; } = 3;
    public int DurationSeconds { get; init; } = 5;
    public int WarmupSeconds { get; init; } = 1;
    public int IntervalSeconds { get; init; } = 1;
    public DataPattern Data { get; init; } = DataPattern.Random;
    public CacheMode Cache { get; init; } = CacheMode.Unbuffered;
}

public sealed record TestCase(int Row, Workload Workload, TestDirection Direction);
public sealed record BenchmarkPlan(string TargetFolder, string Preset, BenchmarkSettings Settings, List<TestCase> Tests);
public sealed record BenchmarkProgress(string Phase, string Message, double Fraction, int CompletedPasses, int TotalPasses,
    int? Row = null, TestDirection? Direction = null, int? Pass = null, double? RemainingSeconds = null);

public sealed record PassResult(int Row, TestDirection Direction, int Pass, double Seconds, long Bytes, long Operations,
    double AverageLatencyMilliseconds, string Command, string RawXml)
{
    [JsonIgnore] public double BytesPerSecond => Bytes / Seconds;
    [JsonIgnore] public double Iops => Operations / Seconds;
    public double Value(ResultUnit unit) => unit switch {
        ResultUnit.MBs => BytesPerSecond / 1_000_000,
        ResultUnit.GBs => BytesPerSecond / 1_000_000_000,
        ResultUnit.IOPS => Iops,
        _ => AverageLatencyMilliseconds * 1000
    };
}

public sealed class SessionResult
{
    public int SchemaVersion { get; init; } = 1;
    public string AppVersion { get; init; } = ProductInfo.Version;
    public string EngineVersion { get; init; } = "DiskSpd 2.3 + UI integration fixes (MIT source build)";
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public required BenchmarkPlan Plan { get; init; }
    public SessionStatus Status { get; set; } = SessionStatus.Running;
    public string OperatingSystem { get; init; } = Environment.OSVersion.VersionString;
    public string TargetDescription { get; set; } = "";
    public string Aggregation { get; init; } = "Highest throughput completed pass per cell; all units use that pass.";
    public string EngineSettings { get; init; } = "IOCP; P-cores first (-ap); PDE buffer separation (-bsp); normal I/O priority; latency enabled; random source buffer 16 MiB; per-pass seed 0.";
    public List<PassResult> Results { get; } = [];
    public string? Error { get; set; }
    public List<string> CleanupWarnings { get; } = [];
    public PassResult? Best(int row, TestDirection direction) => Results.Where(x => x.Row == row && x.Direction == direction)
        .OrderByDescending(x => x.BytesPerSecond).FirstOrDefault();
}

public interface IBenchmarkRunner
{
    Task<SessionResult> RunAsync(BenchmarkPlan plan, IProgress<BenchmarkProgress> progress,
        Action<PassResult> passCompleted, CancellationToken cancellationToken);
}

public static class Sizes
{
    public static readonly long[] FileSizes = Enumerable.Range(4, 13).Select(n => (1L << n) * 1048576).ToArray();
    public static readonly int[] BlockSizes = [4096, 8192, 16384, 32768, 65536, 131072, 262144, 524288, 1048576];
    public static string Format(long bytes) => bytes >= 1073741824 ? $"{bytes / 1073741824d:0.##} GiB" :
        bytes >= 1048576 ? $"{bytes / 1048576d:0.##} MiB" : $"{bytes / 1024d:0.##} KiB";
}

public static class PlanValidation
{
    public static void Validate(BenchmarkPlan plan)
    {
        var s = plan.Settings;
        if (string.IsNullOrWhiteSpace(plan.TargetFolder) || !Path.IsPathFullyQualified(plan.TargetFolder)
            || plan.TargetFolder.StartsWith(@"\\.\", StringComparison.Ordinal) || plan.TargetFolder.StartsWith(@"\\?\", StringComparison.Ordinal))
            throw new ArgumentException("Choose a normal drive or folder for the temporary test file.");
        if (!Sizes.FileSizes.Contains(s.FileSize)) throw new ArgumentException("Choose a test file size from 16 MiB to 64 GiB.");
        if (s.Passes is < 1 or > 9 || s.DurationSeconds is < 1 or > 60 || s.WarmupSeconds is < 0 or > 60 || s.IntervalSeconds is < 0 or > 60)
            throw new ArgumentException("Use 1–9 passes, 1–60 measured seconds, and 0–60 seconds for warm-up and intervals.");
        if (!Enum.IsDefined(s.Data) || !Enum.IsDefined(s.Cache)) throw new ArgumentException("Invalid data or cache setting.");
        if (plan.Tests.Count is < 1 or > 8 || plan.Tests.DistinctBy(x => (x.Row, x.Direction)).Count() != plan.Tests.Count)
            throw new ArgumentException("Choose one or more unique test cells.");
        foreach (var test in plan.Tests)
        {
            var w = test.Workload;
            if (test.Row is < 0 or > 3 || !Enum.IsDefined(test.Direction) || !Enum.IsDefined(w.Pattern)
                || !Sizes.BlockSizes.Contains(w.BlockSize) || w.QueueDepth is < 1 or > 64 || w.Threads is < 1 or > 16)
                throw new ArgumentException("Workloads require 4 KiB–1 MiB blocks, queue depth 1–64, and 1–16 threads.");
        }
    }
}
