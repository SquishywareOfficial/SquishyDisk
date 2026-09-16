using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SquishyDisk.Core;

namespace SquishyDisk.App;

public partial class MainWindow
{
    // Developer-only smoke mode renders the actual WPF controls and performs a bounded real-engine run.
    public async Task VerifyUiAsync(string output)
    {
        verification = true;
        Directory.CreateDirectory(output);
        var original = preferences;
        preferences = preferences with { Theme = "Light" }; ApplyTheme();
        initialized = false; ThemePicker.SelectedItem = "Light"; initialized = true;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        RenderPng(Page, Path.Combine(output, "ui-light-empty.png"));
        var workloads = Presets.Standard();
        var tests = workloads.SelectMany((w, i) => new[] { new TestCase(i, w, TestDirection.Read), new TestCase(i, w, TestDirection.Write) }).ToList();
        session = new SessionResult { Plan = new(target, "Standard", Settings, tests), Status = SessionStatus.Completed, TargetDescription = "Sample drive · 1 TiB total · 620 GiB free" };
        for (int i = 0; i < 4; i++) foreach (var direction in Enum.GetValues<TestDirection>())
        {
            double mb = new[] { 7452.32, 3228.19, 812.57, 83.42 }[i] * (direction == TestDirection.Write ? 0.87 : 1);
            session.Results.Add(new(i, direction, 1, 5, (long)(mb * 1e6 * 5), (long)(mb * 1e6 * 5 / workloads[i].BlockSize), 0.042, "Preview data", "<Results/>"));
        }
        RefreshResults(); StatusLabel.Text = "Preview results · sample data"; RunProgress.Value = 1;
        UpdateLayout(); RenderPng(Page, Path.Combine(output, "ui-light-results.png"));
        preferences = preferences with { Theme = "Dark" }; ApplyTheme();
        initialized = false; ThemePicker.SelectedItem = "Dark"; initialized = true;
        UpdateLayout();
        RenderPng(Page, Path.Combine(output, "ui-dark-results.png"));
        RenderPng(Page, Path.Combine(output, "ui-dark-200.png"), 2);
        SaveReportImage(Path.Combine(output, "sample-report.png"));
        await VerifyDriveUiAsync(output);
        var settingsWindow = new SettingsWindow(Settings, preferences.Workloads) { Owner = this };
        settingsWindow.Show(); settingsWindow.UpdateLayout();
        RenderPng((FrameworkElement)settingsWindow.Content, Path.Combine(output, "ui-settings.png")); settingsWindow.Close();
        Width = 780; Height = 680; UpdateLayout(); RenderPng(Page, Path.Combine(output, "ui-compact.png"));
        preferences = preferences with { Settings = new() { FileSize = 16777216, Passes = 1, DurationSeconds = 1, WarmupSeconds = 0, IntervalSeconds = 0 } };
        initialized = false;
        SizePicker.SelectedItem = SizePicker.Items.Cast<Choice<long>>().First(x => x.Value == 16777216);
        PassPicker.SelectedItem = 1;
        initialized = true;
        ClearResults();
        await BeginRunAsync([new(3, rows[3].Workload, TestDirection.Read), new(3, rows[3].Workload, TestDirection.Write)]);
        if (session?.Status != SessionStatus.Completed || session.Results.Count != 2) throw new InvalidOperationException("UI benchmark failed: " + session?.Error);
        if (!RunButton.IsEnabled || !CopyButton.IsEnabled || !ExportButton.IsEnabled || !Configuration.IsEnabled) throw new InvalidOperationException("UI controls did not return to ready state.");
        File.WriteAllText(Path.Combine(output, "ui-benchmark.json"), Reports.Json(session));
        UpdateLayout(); RenderPng(Page, Path.Combine(output, "ui-real-results.png"));
        // Exercise cancellation through the same token used by Stop and the Closing handler.
        preferences = preferences with { Settings = preferences.Settings with { DurationSeconds = 30, WarmupSeconds = 0 } };
        var cancelRun = BeginRunAsync([new(3, rows[3].Workload, TestDirection.Write)]);
        await Task.Delay(600);
        cancellation?.Cancel();
        await cancelRun;
        if (session?.Status != SessionStatus.Cancelled || session.CleanupWarnings.Count != 0) throw new InvalidOperationException("UI cancellation failed.");
        File.WriteAllText(Path.Combine(output, "ui-verification.json"), JsonSerializer.Serialize(new { Passed = true, RealPasses = 2, Cancellation = true, DriveInfo = true, SimulatedSelfTestStartAbort = true, CloseChoices = new[] { "Cancel", "Leave running", "Abort" }, SelfTestBlocksBenchmark = true, Themes = new[] { "Light", "Dark" }, RenderScales = new[] { 1, 2 } }));
        preferences = original;
    }
}
