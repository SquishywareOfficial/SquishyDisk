using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
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
        UnitPicker.SelectedItem = UnitPicker.Items.Cast<Choice<ResultUnit>>().First(u => u.Value == ResultUnit.MBs);
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        RenderPng(Page, Path.Combine(output, "ui-light-empty.png"));
        var workloads = Presets.Standard();
        var tests = workloads.SelectMany((w, i) => new[] { new TestCase(i, w, TestDirection.Read), new TestCase(i, w, TestDirection.Write) }).ToList();
        session = new SessionResult { Plan = new(target, "Standard", Settings, tests), Status = SessionStatus.Completed, TargetDescription = "Sample drive · 1 TiB total · 620 GiB free" };
        for (int i = 0; i < 4; i++) foreach (var direction in Enum.GetValues<TestDirection>()) for (int pass = 1; pass <= 3; pass++)
        {
            double mb = new[] { 7452.32, 3228.19, 812.57, 83.42 }[i] * (direction == TestDirection.Write ? 0.87 : 1);
            session.Results.Add(new(i, direction, pass, 5, (long)(mb * 1e6 * 5), (long)(mb * 1e6 * 5 / workloads[i].BlockSize), 0.042, "Preview data", "<Results/>"));
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
        foreach (var unit in Enum.GetValues<ResultUnit>())
        {
            UnitPicker.SelectedItem = UnitPicker.Items.Cast<Choice<ResultUnit>>().First(u => u.Value == unit);
            UpdateLayout(); RenderPng(Page, Path.Combine(output, $"ui-compact-{unit}.png"));
        }
        UnitPicker.SelectedItem = UnitPicker.Items.Cast<Choice<ResultUnit>>().First(u => u.Value == ResultUnit.MBs);
        preferences = preferences with { Settings = new() { FileSize = 16777216, Passes = 1, DurationSeconds = 1, WarmupSeconds = 0, IntervalSeconds = 0 } };
        initialized = false;
        SizePicker.SelectedItem = SizePicker.Items.Cast<Choice<long>>().First(x => x.Value == 16777216);
        PassPicker.SelectedItem = 1;
        initialized = true;
        ClearResults();
        var benchmark = BeginRunAsync([new(3, rows[3].Workload, TestDirection.Read), new(3, rows[3].Workload, TestDirection.Write)]);
        if (SettingsButton.IsEnabled) throw new InvalidOperationException("Settings remained enabled during a benchmark.");
        UpdateLayout(); RenderPng(Page, Path.Combine(output, "ui-running.png"));
        await benchmark;
        if (session?.Status != SessionStatus.Completed || session.Results.Count != 2) throw new InvalidOperationException("UI benchmark failed: " + session?.Error);
        if (!RunButton.IsEnabled || !CopyButton.IsEnabled || !ExportButton.IsEnabled || !Configuration.IsEnabled || !SettingsButton.IsEnabled) throw new InvalidOperationException("UI controls did not return to ready state.");
        File.WriteAllText(Path.Combine(output, "ui-benchmark.json"), Reports.Json(session));
        UpdateLayout(); RenderPng(Page, Path.Combine(output, "ui-real-results.png"));
        // Exercise cancellation through the same token used by Stop and the Closing handler.
        preferences = preferences with { Settings = preferences.Settings with { DurationSeconds = 30, WarmupSeconds = 0 } };
        var cancelRun = BeginRunAsync([new(3, rows[3].Workload, TestDirection.Write)]);
        await Task.Delay(600);
        cancellation?.Cancel();
        await cancelRun;
        if (session?.Status != SessionStatus.Cancelled || session.CleanupWarnings.Count != 0 || !SettingsButton.IsEnabled) throw new InvalidOperationException("UI cancellation failed.");
        preferences = original;
        // Exercise WPF's actual Closing event with the system-menu/taskbar close command.
        // The fake drive service completes cleanup synchronously, exposing reentrant Close calls.
        MainTabs.SelectedIndex = 1;
        await DrivePanel.Pending;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Closed += (_, _) => closed.TrySetResult();
        SendMessage(new WindowInteropHelper(this).Handle, 0x0112, new IntPtr(0xF060), IntPtr.Zero); // WM_SYSCOMMAND / SC_CLOSE
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        File.WriteAllText(Path.Combine(output, "ui-verification.json"), JsonSerializer.Serialize(new { Passed = true, RealPasses = 2, Cancellation = true, DriveInfo = true, SimulatedSelfTestStartAbort = true, CloseChoices = new[] { "Cancel", "Leave running", "Abort" }, TaskbarClose = true, SelfTestBlocksBenchmark = true, Themes = new[] { "Light", "Dark" }, RenderScales = new[] { 1, 2 } }));
    }
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
