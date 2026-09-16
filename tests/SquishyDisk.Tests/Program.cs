using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using SquishyDisk.Core;
using SquishyDisk.Windows;

namespace SquishyDisk.Tests;

internal static partial class Program
{
    private static int passed, failed;
    private static readonly string Work = Path.Combine(Path.GetTempPath(), "SquishyDisk-tests-" + Guid.NewGuid().ToString("N"));
    public static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--crash-worker") return await CrashWorker(args[1]);
        Directory.CreateDirectory(Work);
        await Check("Product version matches assemblies and exported reports", () => {
            var version = typeof(Program).Assembly.GetName().Version!.ToString();
            Equal(version, ProductInfo.Version); Equal(version, typeof(PreferenceStore).Assembly.GetName().Version!.ToString());
            Equal("SquishyDisk", ProductInfo.Name); Equal(version, Session().AppVersion);
            True(Reports.Text(Session()).StartsWith(ProductInfo.DisplayName, StringComparison.Ordinal));
            var drive = SmartParser.Parse(SmartDevice, AtaFixture, 0);
            True(DriveReports.Text(drive).StartsWith(ProductInfo.DisplayName, StringComparison.Ordinal));
            True(DriveReports.Json(drive).Contains(version)); True(DriveReports.Csv(drive).Contains(version));
        });
        await Check("Renamed helper accepts its generated pipe name", () => {
            True(DriveAccessBroker.ValidPipeName(DriveAccessBroker.PipePrefix + Guid.NewGuid().ToString("N")));
            True(!DriveAccessBroker.ValidPipeName(DriveAccessBroker.PipePrefix + "invalid"));
            True(!DriveAccessBroker.ValidPipeName("Other-" + Guid.NewGuid().ToString("N")));
            True(!DriveAccessBroker.ValidPipeName(""));
        });
        await Check("Settings migrate once and preserve the original file", () => {
            var legacy = Path.Combine(Work, "DiskSpdUI.settings.json");
            var current = Path.Combine(Work, "SquishyDisk.settings.json");
            True(PreferenceStore.Save(legacy, new() { Theme = "Dark", Settings = new() { Passes = 7 } }));
            var prefs = PreferenceStore.LoadWithLegacyFallback(current, out var warning);
            Equal("Dark", prefs.Theme); Equal(7, prefs.Settings.Passes); True(warning == null);
            True(File.Exists(current) && File.Exists(legacy));
            True(PreferenceStore.Save(current, prefs with { Theme = "Light" }));
            Equal("Light", PreferenceStore.LoadWithLegacyFallback(current, out warning).Theme);
            File.WriteAllText(current, "broken");
            Equal(3, PreferenceStore.LoadWithLegacyFallback(current, out warning).Settings.Passes); True(warning != null);
            File.Delete(current); File.WriteAllText(legacy, "broken");
            PreferenceStore.LoadWithLegacyFallback(current, out warning); True(warning != null && !File.Exists(current));
            File.Delete(legacy);
        });
        await SmartChecks(args);
        await Check("Standard and NVMe presets", () => {
            Equal(4, Presets.Standard().Count); Equal(16, Presets.Nvme()[2].Threads); Equal(131072, Presets.Nvme()[1].BlockSize);
            Equal(512, Presets.Nvme()[2].Threads * Presets.Nvme()[2].QueueDepth);
        });
        await Check("Arguments map direction, access, cache, threads and data", () => {
            var args = DiskSpdCommand.Build(new(2, Presets.Nvme()[2], TestDirection.Write), new(), Path.Combine(Work, "space folder", "file.dat"), "test");
            foreach (var flag in new[] { "-b4096", "-o32", "-t16", "-w100", "-r", "-Su", "-Z16M", "-L", "-Rxml", "-ap", "-bsp" }) True(args.Contains(flag), flag);
            var zero = DiskSpdCommand.Build(new(0, Presets.Standard()[0] with { Threads = 2 }, TestDirection.Write), new() { Data = DataPattern.Zero, Cache = CacheMode.WriteThrough }, "target", "event");
            True(zero.Contains("-Z") && zero.Contains("-Suw") && zero.Contains("-si"));
            True(!args.Contains("-c"));
        });
        await Check("Aggregate measured counters across threads, not profile", () => {
            var p = Parse(Sample()); Equal(12288L, p.Bytes); Equal(3L, p.Operations); Equal(2.0, p.Seconds);
            Equal(0.006144, p.Value(ResultUnit.MBs)); Equal(1.5, p.Iops); Equal(42.0, p.Value(ResultUnit.Microseconds));
        });
        await Check("Parsing is independent of Windows number format", () => {
            var old = CultureInfo.CurrentCulture;
            try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); Equal(0.042, Parse(Sample()).AverageLatencyMilliseconds); }
            finally { CultureInfo.CurrentCulture = old; }
        });
        await Check("Invalid and malicious XML is rejected", () => {
            Throws(() => Parse("<Results/>")); Throws(() => Parse(Sample().Replace("<TestTimeSeconds>2", "<TestTimeSeconds>0")));
            Throws(() => Parse(Sample().Replace("<AverageReadMilliseconds>0.042</AverageReadMilliseconds>", "")));
            Throws(() => Parse(Sample().Replace("<ReadBytes>8192", "<ReadBytes>-8192")));
            Throws(() => Parse("<!DOCTYPE Results [<!ENTITY x SYSTEM 'file:///c:/windows/win.ini'>]><Results>&x;</Results>"));
        });
        await Check("Best result retains latency from the fastest pass", () => {
            var s = Session(); var first = Parse(Sample()); var fast = first with { Pass = 2, Bytes = 24576, AverageLatencyMilliseconds = 0.8 };
            s.Results.Add(first); s.Results.Add(fast); Equal(fast, s.Best(0, TestDirection.Read)); Equal(800.0, s.Best(0, TestDirection.Read)!.Value(ResultUnit.Microseconds));
        });
        await Check("Validation rejects devices, bad timing and excessive workloads", () => {
            PlanValidation.Validate(Plan());
            Throws(() => PlanValidation.Validate(Plan() with { TargetFolder = @"\\.\PhysicalDrive0" }));
            Throws(() => PlanValidation.Validate(Plan() with { TargetFolder = "C:" }));
            Throws(() => PlanValidation.Validate(Plan() with { Settings = new() { Passes = 0 } }));
            Throws(() => PlanValidation.Validate(Plan() with { Tests = [new(0, Presets.Standard()[0] with { Threads = 100 }, TestDirection.Read)] }));
        });
        await Check("Exports preserve metadata, all passes and incomplete state", () => {
            var s = Session(); s.Status = SessionStatus.Cancelled; s.Results.Add(Parse(Sample()));
            var json = Reports.Json(s); using var document = JsonDocument.Parse(json);
            Equal("Cancelled", document.RootElement.GetProperty("Status").GetString()); Equal(1, document.RootElement.GetProperty("Results").GetArrayLength());
            True(Reports.Text(s).Contains("Cancelled")); True(Reports.Csv(s).Contains("average_microseconds"));
            s.Results.Clear(); True(Reports.Csv(s).Contains("Cancelled"));
        });
        await Check("Settings round-trip and corrupt fallback", () => {
            var path = Path.Combine(Work, "settings.json");
            True(PreferenceStore.Save(path, new() { Theme = "Dark", Settings = new() { Passes = 7 } }));
            var prefs = PreferenceStore.Load(path, out var error); Equal("Dark", prefs.Theme); Equal(7, prefs.Settings.Passes); True(error == null);
            File.WriteAllText(path, "not json"); Equal(3, PreferenceStore.Load(path, out error).Settings.Passes); True(error != null);
            File.Delete(path);
            True(!PreferenceStore.Save(Path.Combine(Work, "missing", "settings.json"), prefs));
        });
        await CheckAsync("Owned test file preparation and cleanup", async () => {
            var lease = TargetFiles.Create(Work);
            await TargetFiles.PrepareAsync(lease, Small() with { Data = DataPattern.Zero }, _ => { }, CancellationToken.None);
            Equal(16777216L, new FileInfo(lease.FilePath).Length);
            using (var f = File.OpenRead(lease.FilePath)) { var b = new byte[4096]; f.ReadExactly(b); True(b.All(x => x == 0)); }
            TargetFiles.Cleanup(lease); True(!Directory.Exists(lease.DirectoryPath) && !File.Exists(lease.JournalPath));
        });
        await Check("Cleanup refuses unknown ownership and unexpected files", () => {
            var lease = TargetFiles.Create(Work); File.WriteAllText(lease.FilePath, "owned");
            var extra = Path.Combine(lease.DirectoryPath, "keep.txt"); File.WriteAllText(extra, "user data");
            Throws(() => TargetFiles.Cleanup(lease)); True(File.Exists(extra));
            File.Delete(extra); TargetFiles.Cleanup(lease);
            var forged = TargetFiles.Create(Work); File.WriteAllText(Path.Combine(forged.DirectoryPath, "owner.txt"), "wrong");
            Throws(() => TargetFiles.Cleanup(forged)); True(Directory.Exists(forged.DirectoryPath));
            File.WriteAllText(Path.Combine(forged.DirectoryPath, "owner.txt"), forged.Id); TargetFiles.Cleanup(forged);
            Throws(() => TargetFiles.Cleanup(new(Guid.NewGuid().ToString("N"), Work, Path.Combine(Work, "journal.json"))));
        });
        if (args.Contains("--integration"))
        {
            await Check("Embedded engine checksum and dependencies", () => {
                var path = EngineStore.Extract(); True(EngineStore.Verify(path)); True(EngineStore.License.Contains("MIT"));
            });
            await CheckAsync("Every preset command validates with DiskSpd without I/O", async () => {
                foreach (var w in Presets.Standard().Concat(Presets.Nvme()))
                foreach (var direction in Enum.GetValues<TestDirection>())
                {
                    var flags = DiskSpdCommand.Build(new(0, w, direction), Small(), Path.Combine(Work, "preview.dat"), Guid.NewGuid().ToString("N"))
                        .Select(a => a == "-Rxml" ? "-Rpxml" : a).ToArray();
                    var (code, stdout, stderr) = await Engine(flags); Equal(0, code, stderr); True(stdout.Contains("<Profile>"));
                }
                True(!File.Exists(Path.Combine(Work, "preview.dat")));
            });
            await CheckAsync("Real read/write measurements parse and clean up", async () => {
                var actual = await new BenchmarkRunner().RunAsync(Plan() with { Settings = Small(), Tests = [
                    new(0, Presets.Standard()[0], TestDirection.Read), new(3, Presets.Standard()[3], TestDirection.Write)] },
                    new SyncProgress(_ => { }), _ => { }, CancellationToken.None);
                Equal(SessionStatus.Completed, actual.Status, actual.Error); Equal(2, actual.Results.Count); Equal(0, actual.CleanupWarnings.Count);
                True(actual.Results.All(p => p.Bytes > 0 && p.Iops > 0 && p.AverageLatencyMilliseconds >= 0));
                True(!Directory.EnumerateDirectories(Work, ".squishydisk-*").Any());
                var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "test-results"); Directory.CreateDirectory(output);
                File.WriteAllText(Path.Combine(output, "engine-integration.json"), Reports.Json(actual));
            });
            await CheckAsync("Complete Standard and NVMe suites with repeat aggregation", async () => {
                foreach (var (name, workloads) in new[] { ("Standard", Presets.Standard()), ("NVMe", Presets.Nvme()) })
                {
                    var tests = workloads.SelectMany((w, i) => new[] { new TestCase(i, w, TestDirection.Read), new TestCase(i, w, TestDirection.Write) }).ToList();
                    var actual = await new BenchmarkRunner().RunAsync(new(Work, name, Small(), tests), new SyncProgress(_ => { }), _ => { }, CancellationToken.None);
                    Equal(SessionStatus.Completed, actual.Status, actual.Error); Equal(8, actual.Results.Count); Equal(0, actual.CleanupWarnings.Count);
                    var output = Path.Combine(Environment.CurrentDirectory, "artifacts", "test-results"); Directory.CreateDirectory(output);
                    File.WriteAllText(Path.Combine(output, name.ToLowerInvariant() + "-suite.json"), Reports.Json(actual));
                }
                var repeated = await new BenchmarkRunner().RunAsync(Plan() with { Settings = Small() with { Passes = 2, IntervalSeconds = 1 } }, new SyncProgress(_ => { }), _ => { }, CancellationToken.None);
                Equal(SessionStatus.Completed, repeated.Status, repeated.Error); Equal(2, repeated.Results.Count);
                Equal(repeated.Results.Max(p => p.BytesPerSecond), repeated.Best(0, TestDirection.Read)!.BytesPerSecond);
            });
            await CheckAsync("Cancellation during preparation, warm-up and measurement", async () => {
                foreach (var phase in new[] { "Preparing", "Warm-up", "Measuring" })
                {
                    using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var result = await new BenchmarkRunner().RunAsync(Plan() with { Settings = Small() with { DurationSeconds = 15, WarmupSeconds = 3 } },
                        new SyncProgress(p => { if (p.Phase == phase) cancel.Cancel(); }), _ => { }, cancel.Token);
                    Equal(SessionStatus.Cancelled, result.Status, phase + ": " + result.Error); Equal(0, result.Results.Count); Equal(0, result.CleanupWarnings.Count);
                    True(!Directory.EnumerateDirectories(Work, ".squishydisk-*").Any());
                }
            });
            await CheckAsync("Unicode and ampersand target folders", async () => {
                var folder = Path.Combine(Work, "Tést & 測試"); Directory.CreateDirectory(folder);
                var actual = await new BenchmarkRunner().RunAsync(Plan() with { TargetFolder = folder, Settings = Small() },
                    new SyncProgress(_ => { }), _ => { }, CancellationToken.None);
                Equal(SessionStatus.Completed, actual.Status, actual.Error); Equal(0, actual.CleanupWarnings.Count);
                Directory.Delete(folder, false);
            });
            await CheckAsync("Forced parent exit terminates DiskSpd and recovery removes its file", ForcedExitTest);
        }
        if (Directory.Exists(Work) && !Directory.EnumerateFileSystemEntries(Work).Any()) Directory.Delete(Work, false);
        Console.WriteLine($"\n{passed} passed; {failed} failed.");
        return failed == 0 ? 0 : 1;
    }
    private static async Task<int> CrashWorker(string folder)
    {
        bool announced = false;
        await new BenchmarkRunner().RunAsync(Plan() with { TargetFolder = folder, Settings = Small() with { DurationSeconds = 60 } }, new SyncProgress(p => {
            if (p.Phase == "Measuring" && !announced)
            {
                announced = true;
                var engines = Process.GetProcessesByName("diskspd");
                var child = engines.Single(p => p.MainModule?.FileName == EngineStore.Extract());
                Console.WriteLine("READY " + child.Id); Console.Out.Flush();
                foreach (var proc in engines) proc.Dispose();
            }
        }), _ => { }, CancellationToken.None);
        return 0;
    }
    private static async Task ForcedExitTest()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(Environment.ProcessPath), "dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add("--crash-worker"); info.ArgumentList.Add(Work);
        using var worker = Process.Start(info)!;
        try
        {
            var ready = await worker.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            True(ready?.StartsWith("READY ", StringComparison.Ordinal) == true, ready ?? "Worker did not start");
            int id = int.Parse(ready![6..], CultureInfo.InvariantCulture);
            using var engine = Process.GetProcessById(id);
            worker.Kill(false); await worker.WaitForExitAsync();
            await engine.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            True(Directory.EnumerateDirectories(Work, ".squishydisk-*").Any());
            var errors = TargetFiles.Recover(); True(!errors.Any(e => e.Contains(Work)));
            True(!Directory.EnumerateDirectories(Work, ".squishydisk-*").Any());
        }
        finally { if (!worker.HasExited) { worker.Kill(true); await worker.WaitForExitAsync(); } }
    }
    private static async Task<(int, string, string)> Engine(IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(EngineStore.Extract()) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); return (process.ExitCode, await output, await error);
    }
    private static BenchmarkSettings Small() => new() { FileSize = 16777216, Passes = 1, DurationSeconds = 1, WarmupSeconds = 0, IntervalSeconds = 0 };
    private static BenchmarkPlan Plan() => new(Work, "Standard", new(), [new(0, Presets.Standard()[0], TestDirection.Read)]);
    private static SessionResult Session() => new() { Plan = Plan() };
    private static PassResult Parse(string xml) => ResultParser.Parse(xml, Plan().Tests[0], 1, "diskspd test");
    private static string Sample() => """
        <Results><Profile><TimeSpans><TimeSpan><Duration>9999</Duration></TimeSpan></TimeSpans></Profile>
        <TimeSpan><TestTimeSeconds>2</TestTimeSeconds><Latency><AverageReadMilliseconds>0.042</AverageReadMilliseconds></Latency>
        <Thread><Target><ReadBytes>8192</ReadBytes><ReadCount>2</ReadCount><WriteBytes>0</WriteBytes><WriteCount>0</WriteCount></Target></Thread>
        <Thread><Target><ReadBytes>4096</ReadBytes><ReadCount>1</ReadCount><WriteBytes>0</WriteBytes><WriteCount>0</WriteCount></Target></Thread>
        </TimeSpan></Results>
        """;
    private static Task Check(string name, Action test) => CheckAsync(name, () => { test(); return Task.CompletedTask; });
    private static async Task CheckAsync(string name, Func<Task> test)
    {
        try { await test(); Console.WriteLine("PASS " + name); passed++; }
        catch (Exception ex) { Console.WriteLine("FAIL " + name + "\n" + ex); failed++; }
    }
    private static void True(bool condition, string? detail = null) { if (!condition) throw new Exception("Assertion failed. " + detail); }
    private static void Equal<T>(T expected, T actual, string? detail = null) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}. {detail}"); }
    private static void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected an exception."); }
    private sealed class SyncProgress(Action<BenchmarkProgress> report) : IProgress<BenchmarkProgress> { public void Report(BenchmarkProgress value) => report(value); }
}
