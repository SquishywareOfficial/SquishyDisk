using System.Diagnostics;
using SquishyDisk.Core;

namespace SquishyDisk.Windows;

public sealed class BenchmarkRunner : IBenchmarkRunner
{
    private int running;
    public async Task<SessionResult> RunAsync(BenchmarkPlan plan, IProgress<BenchmarkProgress> progress,
        Action<PassResult> passCompleted, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref running, 1) != 0) throw new InvalidOperationException("A benchmark is already running.");
        try { return await Task.Run(() => RunCoreAsync(plan, progress, passCompleted, cancellationToken), CancellationToken.None); }
        finally { Volatile.Write(ref running, 0); }
    }

    private static async Task<SessionResult> RunCoreAsync(BenchmarkPlan plan, IProgress<BenchmarkProgress> progress,
        Action<PassResult> passCompleted, CancellationToken token)
    {
        var session = new SessionResult { Plan = plan, TargetDescription = TargetFiles.Describe(plan.TargetFolder) };
        TargetLease? lease = null;
        int done = 0, total = plan.Tests.Count * plan.Settings.Passes;
        try
        {
            token.ThrowIfCancellationRequested();
            TargetFiles.Preflight(plan);
            TargetFiles.RejectReparseAncestors(plan.TargetFolder);
            var exe = EngineStore.Extract();
            lease = TargetFiles.Create(plan.TargetFolder);
            var throttle = Stopwatch.StartNew();
            progress.Report(new("Preparing", $"Preparing {Sizes.Format(plan.Settings.FileSize)} of {plan.Settings.Data.ToString().ToLowerInvariant()} test data…", 0, 0, total));
            await TargetFiles.PrepareAsync(lease, plan.Settings, fraction => {
                if (throttle.ElapsedMilliseconds < 100 && fraction < 1) return;
                throttle.Restart();
                progress.Report(new("Preparing", $"Preparing test file · {fraction:P0}", 0.05 * fraction, 0, total));
            }, token);
            foreach (var test in plan.Tests.OrderBy(t => t.Direction).ThenBy(t => t.Row))
            {
                for (int pass = 1; pass <= plan.Settings.Passes; pass++)
                {
                    token.ThrowIfCancellationRequested();
                    var result = await RunPassAsync(exe, lease.FilePath, test, pass, plan.Settings, done, total, progress, token);
                    session.Results.Add(result);
                    passCompleted(result);
                    done++;
                    if (done < total && plan.Settings.IntervalSeconds > 0)
                    {
                        var until = Stopwatch.StartNew();
                        while (until.Elapsed.TotalSeconds < plan.Settings.IntervalSeconds)
                        {
                            progress.Report(new("Interval", "Pausing between passes…", 0.05 + 0.95 * done / total, done, total,
                                RemainingSeconds: (total - done) * (plan.Settings.WarmupSeconds + plan.Settings.DurationSeconds + plan.Settings.IntervalSeconds)));
                            await Task.Delay(100, token);
                        }
                    }
                }
            }
            session.Status = SessionStatus.Completed;
        }
        catch (OperationCanceledException) { session.Status = SessionStatus.Cancelled; }
        catch (Exception ex) { session.Status = SessionStatus.Failed; session.Error = ex.Message; }
        finally
        {
            if (lease != null)
            {
                progress.Report(new("Cleanup", "Removing the temporary test file…", 0.05 + 0.95 * done / Math.Max(1, total), done, total));
                try { TargetFiles.Cleanup(lease); }
                catch (Exception ex) { session.CleanupWarnings.Add($"{lease.DirectoryPath}: {ex.Message}"); }
            }
            session.FinishedAt = DateTimeOffset.Now;
        }
        return session;
    }

    private static async Task<PassResult> RunPassAsync(string exe, string file, TestCase test, int pass,
        BenchmarkSettings settings, int done, int total, IProgress<BenchmarkProgress> progress, CancellationToken token)
    {
        var prefix = @"Local\SquishyDisk-" + Guid.NewGuid().ToString("N");
        using var started = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-start");
        using var finished = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-finish");
        using var stop = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + "-stop");
        var args = DiskSpdCommand.Build(test, settings, file, prefix);
        var info = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Path.GetDirectoryName(exe)!,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8 };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        var command = DiskSpdCommand.Display(exe, args);
        using var job = new ProcessJob();
        using var process = new Process { StartInfo = info };
        token.ThrowIfCancellationRequested();
        if (!process.Start()) throw new IOException("DiskSpd could not start.");
        try { job.Assign(process); }
        catch { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); throw; }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        var exit = process.WaitForExitAsync();
        var elapsed = Stopwatch.StartNew();
        Stopwatch? measurement = null;
        var duration = settings.DurationSeconds + settings.WarmupSeconds;
        using var cancellation = token.Register(() => stop.Set());
        try
        {
            while (!exit.IsCompleted)
            {
                token.ThrowIfCancellationRequested();
                if (elapsed.Elapsed.TotalSeconds > duration + 120) throw new TimeoutException("DiskSpd did not finish in time. Check the drive connection.");
                if (measurement == null && started.WaitOne(0)) measurement = Stopwatch.StartNew();
                bool ending = finished.WaitOne(0);
                string phase = ending ? "Finishing" : measurement == null ? "Warm-up" : "Measuring";
                double step = measurement == null ? Math.Min(elapsed.Elapsed.TotalSeconds, settings.WarmupSeconds) :
                    settings.WarmupSeconds + Math.Min(measurement.Elapsed.TotalSeconds, settings.DurationSeconds);
                double fraction = Math.Clamp(step / Math.Max(1, duration), 0, 0.99);
                progress.Report(new(phase, $"{test.Workload.Label} · {test.Direction} · pass {pass}/{settings.Passes} · {phase.ToLowerInvariant()}",
                    0.05 + 0.95 * (done + fraction) / total, done, total, test.Row, test.Direction, pass,
                    Math.Max(0, (total - done - fraction) * (duration + settings.IntervalSeconds))));
                await Task.WhenAny(exit, Task.Delay(100, token));
            }
            token.ThrowIfCancellationRequested();
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode != 0) throw new IOException($"DiskSpd exited with code {process.ExitCode}. {Limit(error + " " + output)}");
            try { return ResultParser.Parse(output, test, pass, command); }
            catch (Exception ex) when (ex is System.Xml.XmlException or FormatException or OverflowException)
            { throw new IOException($"DiskSpd returned invalid results: {ex.Message} {Limit(error)}", ex); }
        }
        finally
        {
            if (!process.HasExited)
            {
                stop.Set();
                if (await Task.WhenAny(exit, Task.Delay(5000)) != exit) process.Kill(true);
                await exit.WaitAsync(TimeSpan.FromSeconds(15));
            }
            await Task.WhenAll(stdout, stderr);
        }
    }
    private static string Limit(string value) => value.Length > 4000 ? value[..4000] : value.Trim();
}
