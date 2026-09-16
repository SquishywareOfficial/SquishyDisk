using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using SquishyDisk.Core;
using SquishyDisk.Windows;

namespace SquishyDisk.App;

public partial class MainWindow : Window
{
    private Preferences preferences;
    private List<RowViewModel> rows = [];
    private SessionResult? session;
    private CancellationTokenSource? cancellation;
    private Task? activeRun;
    private readonly BenchmarkRunner runner = new();
    private readonly Stopwatch clock = new();
    private bool initialized, closing, closeRequested, verification;
    private string target = Path.GetTempPath();
    private string? loadWarning;
    private ResultUnit Unit => (UnitPicker.SelectedItem as Choice<ResultUnit>)?.Value ?? ResultUnit.MBs;
    private BenchmarkSettings Settings => preferences.Settings with {
        FileSize = (SizePicker.SelectedItem as Choice<long>)?.Value ?? preferences.Settings.FileSize,
        Passes = PassPicker.SelectedItem is int count ? count : preferences.Settings.Passes };

    public MainWindow()
    {
        preferences = PreferenceStore.LoadWithLegacyFallback(PreferenceStore.DefaultPath, out loadWarning);
        InitializeComponent();
        ThemePicker.ItemsSource = new[] { "System", "Light", "Dark" };
        ThemePicker.SelectedItem = preferences.Theme;
        PresetPicker.ItemsSource = new[] { "Standard", "NVMe", "Custom" };
        PresetPicker.SelectedItem = preferences.Preset;
        SizePicker.ItemsSource = Sizes.FileSizes.Select(s => new Choice<long>(Sizes.Format(s), s)).ToList();
        SizePicker.SelectedItem = SizePicker.Items.Cast<Choice<long>>().First(s => s.Value == preferences.Settings.FileSize);
        PassPicker.ItemsSource = Enumerable.Range(1, 9);
        PassPicker.SelectedItem = preferences.Settings.Passes;
        UnitPicker.ItemsSource = Enum.GetValues<ResultUnit>().Select(u => new Choice<ResultUnit>(CellViewModel.UnitLabel(u), u)).ToList();
        UnitPicker.SelectedItem = UnitPicker.Items.Cast<Choice<ResultUnit>>().First(u => u.Value == preferences.Unit);
        LoadDrives();
        if (!string.IsNullOrEmpty(preferences.TargetFolder) && Directory.Exists(preferences.TargetFolder)) target = preferences.TargetFolder;
        RebuildRows();
        initialized = true;
        RefreshTarget(); RefreshSummary(); ApplyTheme();
        if (loadWarning != null) ShowNotice(loadWarning);
        SystemEvents.UserPreferenceChanged += SystemPreferenceChanged;
        Closed += (_, _) => SystemEvents.UserPreferenceChanged -= SystemPreferenceChanged;
    }

    private void SystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(ApplyTheme);
    protected override void OnSourceInitialized(EventArgs e) { base.OnSourceInitialized(e); ApplyTheme(); }
    private void LoadDrives()
    {
        var choices = new List<Choice<string>>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try {
                if (drive.IsReady && drive.DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Network)
                    choices.Add(new($"{drive.Name}  {drive.VolumeLabel}".Trim(), drive.Name));
            } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        choices.Add(new("Selected folder", ""));
        DrivePicker.ItemsSource = choices;
    }
    private void RebuildRows()
    {
        rows = preferences.Workloads.Select((w, i) => new RowViewModel(i, w)).ToList();
        RowsList.ItemsSource = rows;
    }
    private void RefreshTarget()
    {
        bool previous = initialized; initialized = false;
        var root = Path.GetPathRoot(target);
        DrivePicker.SelectedItem = DrivePicker.Items.Cast<Choice<string>>().FirstOrDefault(d => d.Value.Equals(root, StringComparison.OrdinalIgnoreCase))
            ?? DrivePicker.Items.Cast<Choice<string>>().Last();
        initialized = previous;
        DriveDescription.Text = TargetFiles.Describe(target);
        FolderDescription.Text = "Test folder: " + target;
        FolderDescription.ToolTip = target;
    }
    private void Drive_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized || DrivePicker.SelectedItem is not Choice<string> drive) return;
        if (drive.Value.Length == 0) { Browse_Click(sender, new()); return; }
        target = drive.Value.Equals(Path.GetPathRoot(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ? Path.GetTempPath() : drive.Value;
        ClearResults(); RefreshTarget(); SavePreferences();
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder for the temporary benchmark file", InitialDirectory = target };
        if (dialog.ShowDialog(this) == true) { target = dialog.FolderName; ClearResults(); SavePreferences(); }
        RefreshTarget();
    }
    private void Preset_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!initialized) return;
        var selected = (string)PresetPicker.SelectedItem;
        if (selected != "Custom") preferences = preferences with { Workloads = selected == "NVMe" ? Presets.Nvme() : Presets.Standard() };
        preferences = preferences with { Preset = selected };
        RebuildRows(); ClearResults(); SavePreferences(); RefreshSummary();
    }
    private void Config_Changed(object sender, SelectionChangedEventArgs e)
    { if (initialized) { ClearResults(); SavePreferences(); RefreshSummary(); } }
    private void Unit_Changed(object sender, SelectionChangedEventArgs e)
    { if (initialized) { RefreshResults(); SavePreferences(); } }
    private void Theme_Changed(object sender, SelectionChangedEventArgs e)
    { if (initialized) { preferences = preferences with { Theme = (string)ThemePicker.SelectedItem }; ApplyTheme(); SavePreferences(); } }

    private void ApplyTheme()
    {
        bool dark = preferences.Theme == "Dark" || (preferences.Theme == "System" &&
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0);
        string[] keys = ["PageBrush", "PanelBrush", "TextBrush", "MutedBrush", "LineBrush", "AccentBrush", "SoftBrush", "HoverBrush", "SuccessBrush"];
        string[] colors = dark ? ["#111824", "#1B2534", "#EBF1FC", "#A1B1C8", "#344158", "#6498FF", "#222F44", "#304561", "#73DAAD"] :
            ["#F3F6FB", "#FFFFFF", "#17263C", "#65758B", "#DFE6F0", "#2563EB", "#ECF3FF", "#E0EBFC", "#18794E"];
        for (int i = 0; i < keys.Length; i++) Application.Current.Resources[keys[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(colors[i]));
        if (SystemParameters.HighContrast)
        {
            Application.Current.Resources["PageBrush"] = SystemColors.WindowBrush;
            Application.Current.Resources["PanelBrush"] = SystemColors.WindowBrush;
            Application.Current.Resources["SoftBrush"] = SystemColors.WindowBrush;
            Application.Current.Resources["TextBrush"] = SystemColors.WindowTextBrush;
            Application.Current.Resources["MutedBrush"] = SystemColors.WindowTextBrush;
            Application.Current.Resources["LineBrush"] = SystemColors.WindowTextBrush;
        }
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) { int flag = dark ? 1 : 0; DwmSetWindowAttribute(handle, 20, ref flag, sizeof(int)); }
    }

    private void SavePreferences()
    {
        if (!initialized || verification) return;
        preferences = preferences with { Settings = Settings, Unit = Unit, TargetFolder = target };
        if (!PreferenceStore.Save(PreferenceStore.DefaultPath, preferences))
            ShowNotice("This folder is read-only. Preferences will be kept for this session only.");
    }
    private void RefreshSummary()
    {
        var s = Settings;
        SummaryLabel.Text = $"{s.DurationSeconds}s per pass · {s.Data.ToString().ToLowerInvariant()} data";
        DetailsBox.Text = $"DiskSpd 2.3 · static runtime · engine SHA-256: {EngineStore.Hash}\n\n{s.DurationSeconds}s measured, {s.WarmupSeconds}s warm-up, {s.IntervalSeconds}s interval\nCache: {s.Cache}\nQueue depth is per thread. Total outstanding requests = Q × T.\n\nCommands and raw XML appear here after a completed pass.";
    }
    private void ClearResults()
    {
        session = null;
        RefreshResults(); RunProgress.Value = 0; StatusLabel.Text = "Ready to benchmark"; TimeLabel.Text = "";
        CopyButton.IsEnabled = ExportButton.IsEnabled = false;
        ResultCaption.Text = "Click a row to run read and write tests, or a cell to run one.";
        RefreshSummary();
    }
    private void RefreshResults()
    {
        foreach (var row in rows)
        foreach (var cell in new[] { row.Read, row.Write })
        {
            var best = session?.Best(row.Index, cell.Direction);
            int completed = session?.Results.Count(p => p.Row == row.Index && p.Direction == cell.Direction) ?? 0;
            cell.Set(best, Unit, completed == 0 ? null : $"Best of {completed} · {CellViewModel.UnitLabel(Unit)}");
        }
    }
    private async void RunAll_Click(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) { cancellation.Cancel(); RunButton.IsEnabled = false; StatusLabel.Text = "Stopping and cleaning up…"; return; }
        await BeginRunAsync(rows.SelectMany(row => new[] { new TestCase(row.Index, row.Workload, TestDirection.Read), new TestCase(row.Index, row.Workload, TestDirection.Write) }).ToList());
    }
    private async void RunRow_Click(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        var row = rows[(int)((Button)sender).Tag];
        await BeginRunAsync([new(row.Index, row.Workload, TestDirection.Read), new(row.Index, row.Workload, TestDirection.Write)]);
    }
    private async void RunCell_Click(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        var cell = (CellViewModel)((Button)sender).Tag;
        await BeginRunAsync([new(cell.Row, rows[cell.Row].Workload, cell.Direction)]);
    }
    private async Task BeginRunAsync(List<TestCase> tests)
    {
        if (cancellation != null) return;
        if (!DrivePanel.Activity.TryBenchmark()) { ShowNotice("Wait for drive access to finish or stop the active self-test before benchmarking."); return; }
        DrivePanel.BenchmarkChanged();
        try { activeRun = ExecuteRunAsync(tests); await activeRun; }
        finally { DrivePanel.Activity.EndBenchmark(); DrivePanel.BenchmarkChanged(); }
    }
    private async void Tab_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (initialized && ReferenceEquals(e.Source, MainTabs) && MainTabs.SelectedIndex == 1)
            await DrivePanel.OpenAsync(target);
    }
    private async Task ExecuteRunAsync(List<TestCase> tests)
    {
        var plan = new BenchmarkPlan(target, preferences.Preset, Settings, tests);
        try { PlanValidation.Validate(plan); }
        catch (ArgumentException ex) { ShowNotice(ex.Message); return; }
        SavePreferences();
        cancellation = new();
        var previous = session;
        var retainedTests = previous?.Plan.Tests.Where(t => !tests.Any(n => n.Row == t.Row && n.Direction == t.Direction)).ToList() ?? [];
        session = new SessionResult { Plan = plan with { Tests = retainedTests.Concat(tests).OrderBy(t => t.Row).ThenBy(t => t.Direction).ToList() },
            StartedAt = previous?.StartedAt ?? DateTimeOffset.Now, TargetDescription = TargetFiles.Describe(target) };
        if (previous != null) session.Results.AddRange(previous.Results.Where(p => retainedTests.Any(t => t.Row == p.Row && t.Direction == p.Direction)));
        SetRunning(true); NoticeLabel.Visibility = Visibility.Collapsed; RefreshResults();
        clock.Restart();
        var progress = new Progress<BenchmarkProgress>(p => {
            RunProgress.Value = p.Fraction; StatusLabel.Text = p.Message;
            TimeLabel.Text = $"{clock.Elapsed:mm\\:ss} elapsed" + (p.RemainingSeconds.HasValue ? $" · ~{TimeSpan.FromSeconds(p.RemainingSeconds.Value):mm\\:ss} left" : "");
            if (p.Row is int index && p.Direction is TestDirection direction && p.Pass.HasValue)
            {
                var cell = direction == TestDirection.Read ? rows[index].Read : rows[index].Write;
                cell.Set(session?.Best(index, direction), Unit, $"{p.Phase} · pass {p.Pass}/{Settings.Passes}");
            }
        });
        try
        {
            var completed = await runner.RunAsync(plan, progress, result => Dispatcher.Invoke(() => {
                session.Results.Add(result); RefreshResults();
                DetailsBox.Text = result.Command + "\n\n" + result.RawXml;
            }), cancellation.Token);
            session.Status = completed.Status; session.FinishedAt = completed.FinishedAt;
            session.Error = completed.Error; session.CleanupWarnings.AddRange(completed.CleanupWarnings);
            RefreshResults();
            StatusLabel.Text = session.Status switch { SessionStatus.Completed => "Benchmark complete", SessionStatus.Cancelled => "Stopped · completed results kept", _ => "Test failed" };
            if (session.Status == SessionStatus.Completed) RunProgress.Value = 1;
            ResultCaption.Text = $"{session.Status} · best completed pass in each cell · {session.StartedAt:HH:mm}";
            var notices = session.CleanupWarnings.ToList();
            if (session.Error != null) notices.Insert(0, session.Error);
            if (notices.Count > 0) ShowNotice(string.Join(Environment.NewLine, notices));
            DetailsBox.Text = Reports.Text(session) + "\n\n" + string.Join("\n\n", session.Results.Select(p => p.Command + "\n" + p.RawXml));
        }
        finally
        {
            clock.Stop(); TimeLabel.Text = $"{clock.Elapsed:mm\\:ss} elapsed";
            cancellation.Dispose(); cancellation = null; SetRunning(false);
        }
    }
    private void SetRunning(bool running)
    {
        Configuration.IsEnabled = !running;
        foreach (var row in rows) row.Enable(!running);
        RunButton.Content = running ? "■  Stop" : "▶  Run all tests";
        RunButton.IsEnabled = true;
        CopyButton.IsEnabled = ExportButton.IsEnabled = !running && session != null;
    }
    public void ShowNotice(string text) { NoticeLabel.Text = text; NoticeLabel.Visibility = Visibility.Visible; }
    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) { ShowNotice("Stop the benchmark before changing workload settings."); return; }
        var dialog = new SettingsWindow(Settings, preferences.Workloads) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        bool workloadsChanged = !dialog.Workloads.SequenceEqual(preferences.Workloads);
        preferences = preferences with { Settings = dialog.Settings, Workloads = dialog.Workloads,
            Preset = workloadsChanged ? "Custom" : preferences.Preset };
        initialized = false; PresetPicker.SelectedItem = preferences.Preset; initialized = true;
        RebuildRows(); ClearResults(); SavePreferences();
    }
    private void About_Click(object sender, RoutedEventArgs e)
    {
        var text = ProductInfo.DisplayName + "\nSquishyware\nPortable storage benchmark and drive information for Windows\n\nPowered by Microsoft DiskSpd 2.3, built from MIT-licensed source.\nSource revision: 5e7025bfc9d1364f185d4c30963d7ac79195435e\nhttps://github.com/microsoft/diskspd\n\nIndependent application; not affiliated with Microsoft or Crystal Dew World.\n\n" + EngineStore.License;
        var assembly = typeof(MainWindow).Assembly;
        foreach (var resource in new[] { "AppLicense", "AppWtfpl", "AppThirdPartySummary" })
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            text += "\n\n" + reader.ReadToEnd();
        }
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("ThirdParty.", StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            text += "\n\n" + resource + "\n\n" + reader.ReadToEnd();
        }
        text = text.Replace("built from MIT-licensed source.", "built from MIT-licensed source with notification and XML-path fixes.", StringComparison.Ordinal);
        text += "\n\nDrive information: smartctl 7.5, an unmodified independent program from smartmontools.\nGPL-2.0-or-later. https://www.smartmontools.org\nMatching source package: smartmontools-7.5.tar.gz, supplied alongside this app's release.\n\n" + SmartctlPayload.Notice;
        var window = new Window { Title = "About SquishyDisk", Owner = this, Width = 680, Height = 550, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20) } };
        window.ShowDialog();
    }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (session == null) return;
        try { Clipboard.SetText(Reports.Text(session)); StatusLabel.Text = "Results copied to clipboard"; }
        catch (ExternalException) { ShowNotice("The clipboard is busy. Try again in a moment."); }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (session == null) return;
        var dialog = new SaveFileDialog { FileName = $"SquishyDisk-{DateTime.Now:yyyyMMdd-HHmmss}",
            Filter = "Text report (*.txt)|*.txt|CSV data (*.csv)|*.csv|JSON with raw results (*.json)|*.json|Result image (*.png)|*.png", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        try {
            if (dialog.FilterIndex == 4) SaveReportImage(dialog.FileName);
            else File.WriteAllText(dialog.FileName, dialog.FilterIndex switch { 2 => Reports.Csv(session), 3 => Reports.Json(session), _ => Reports.Text(session) }, new UTF8Encoding(dialog.FilterIndex == 2));
            StatusLabel.Text = "Results exported";
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ShowNotice("Export failed: " + ex.Message); }
    }
    private void SaveReportImage(string path)
    {
        if (session == null) return;
        var stack = new StackPanel { Margin = new Thickness(30) };
        stack.Children.Add(new TextBlock { Text = "SQUISHYDISK", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("AccentBrush"), Margin = new Thickness(0, 0, 0, 16) });
        stack.Children.Add(new TextBlock { Text = Reports.Text(session), Foreground = (Brush)FindResource("TextBrush"), FontFamily = new FontFamily("Consolas"), FontSize = 13, TextWrapping = TextWrapping.Wrap });
        var panel = new Border { Background = (Brush)FindResource("PanelBrush"), Width = 1000, Child = stack };
        panel.Measure(new Size(1000, double.PositiveInfinity)); panel.Arrange(new Rect(panel.DesiredSize)); panel.UpdateLayout();
        RenderPng(panel, path, 1.5);
    }
    internal static void RenderPng(FrameworkElement element, string path, double scale = 1)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
            drawing.DrawRectangle((Brush)Application.Current.FindResource("PageBrush"), null, bounds);
            drawing.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.Fill }, null, bounds);
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        if (closeRequested) return;
        closeRequested = true;
        DrivePanel.IsEnabled = false;
        try
        {
            cancellation?.Cancel();
            if (activeRun != null) await activeRun;
            if (!await DrivePanel.CanCloseAsync()) return;
            SavePreferences(); await DrivePanel.DisposeAsync();
            closing = true; Close();
        }
        finally { closeRequested = false; if (!closing) DrivePanel.IsEnabled = true; }
    }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
