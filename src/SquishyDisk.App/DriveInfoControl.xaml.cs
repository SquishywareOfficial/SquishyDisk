using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SquishyDisk.Core;
using SquishyDisk.Windows;
using Microsoft.Win32;

namespace SquishyDisk.App;

public partial class DriveInfoControl : UserControl, IAsyncDisposable
{
    private IDriveInfoService service = new SmartctlService();
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(10) };
    private readonly CancellationTokenSource lifetime = new();
    private List<DriveSnapshot> devices = [];
    private bool discovered, selecting, elevated, disposed;
    private string benchmarkFolder = "";
    public DriveActivity Activity { get; } = new();
    public Task Pending { get; private set; } = Task.CompletedTask;
    internal Func<int>? VerificationCloseChoice { get; set; }
    private DriveSnapshot? Selected => DevicePicker.SelectedItem as DriveSnapshot;
    public DriveInfoControl()
    {
        InitializeComponent(); UpdateButtons();
        poll.Tick += async (_, _) => { if (!Activity.Busy && !Activity.Benchmark && Activity.Active.Count > 0) await Run(PollAsync); };
        poll.Start();
    }
    public async Task OpenAsync(string folder)
    {
        benchmarkFolder = folder;
        if (!discovered) await Run(ScanAsync);
        else UpdateMapping(false);
    }
    public void BenchmarkChanged()
    {
        UpdateButtons();
        MessageLabel.Text = Activity.Benchmark ? "Drive queries are paused while the benchmark runs." : discovered ? "Drive queries are available. Refresh to read the selected drive." : "Click Rescan to discover drives.";
    }
    private Task Run(Func<Task> action)
    {
        if (disposed || !Activity.TryDrive()) return Task.CompletedTask;
        Pending = Execute(action); return Pending;
    }
    private async Task Execute(Func<Task> action)
    {
        UpdateButtons();
        try { await action(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or Win32Exception or FormatException or System.Text.Json.JsonException or OperationCanceledException)
        {
            if (service is DriveAccessBroker broker && !broker.IsConnected) elevated = false;
            string message = ex is Win32Exception { NativeErrorCode: 1223 } ? "Administrator access was cancelled. Benchmarking is still available." : "Drive access failed: " + ex.Message;
            MessageLabel.Text = message;
            if (Selected is { } selected) Accept(new(Snapshot: selected with { Stale = true, Limitations = [.. selected.Limitations, message] }, Message: message));
        }
        finally { Activity.EndDrive(); UpdateButtons(); }
    }
    private async Task ScanAsync()
    {
        MessageLabel.Text = "Reading drives…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); timeout.CancelAfter(TimeSpan.FromMinutes(3));
        string? previous = Selected?.Identity;
        var reply = await service.SendAsync(new(DriveOperation.Discover), timeout.Token);
        devices = reply.Devices ?? [];
        foreach (var snapshot in devices) Activity.Observe(snapshot);
        selecting = true; DevicePicker.ItemsSource = devices;
        DevicePicker.SelectedItem = devices.FirstOrDefault(s => !string.IsNullOrEmpty(previous) && s.Identity == previous) ?? devices.FirstOrDefault();
        selecting = false; discovered = true; UpdateMapping(previous == null);
        if (Selected is { } selected) Show(selected);
        else { HealthLabel.Text = "Unavailable"; SummaryFields.ItemsSource = null; AttributeTable.ItemsSource = null; LogsBox.Text = ""; ReadLabel.Text = "No drive reading"; }
        MessageLabel.Text = reply.Message ?? (devices.Count == 0 ? "No drives found. Try Enable drive access, then Rescan." : $"{devices.Count} drive(s) found. Refresh reads the selected drive.");
    }
    private void UpdateMapping(bool preselect)
    {
        string root = PhysicalDrives.VolumeRoot(benchmarkFolder);
        if (root.StartsWith(@"\\", StringComparison.Ordinal) || (root.Length > 0 && new DriveInfo(root).DriveType == DriveType.Network))
        { MappingLabel.Text = "Benchmark target is a network share. Remote drive health is unavailable."; return; }
        var matches = devices.Where(d => (d.Device.Volumes ?? []).Contains(root, StringComparer.OrdinalIgnoreCase)).ToList();
        bool shared = matches.Any(d => (d.Device.SharedVolumes ?? []).Contains(root, StringComparer.OrdinalIgnoreCase));
        if (matches.Count == 1 && !shared)
        {
            MappingLabel.Text = $"Benchmark target {root} is on {matches[0].Device.Location}.";
            if (preselect) { selecting = true; DevicePicker.SelectedItem = matches[0]; selecting = false; }
        }
        else MappingLabel.Text = matches.Count > 1 || shared ? $"Benchmark target {root} has multiple backing disks. Discovered: " + string.Join(", ", matches.Select(d => d.Device.Location)) : "Benchmark target could not be matched to one physical disk. Select a drive above.";
    }
    private async Task ReadAsync(DriveSnapshot selected, DriveOperation operation = DriveOperation.Read)
    {
        var reply = await service.SendAsync(new(operation, selected.Device.Token), lifetime.Token);
        Accept(reply);
    }
    private void Accept(DriveReply reply)
    {
        if (reply.Snapshot is not { } updated) return;
        Activity.Observe(updated, reply.Uncertain);
        int index = devices.FindIndex(s => s.Device.Name == updated.Device.Name && s.Device.Type == updated.Device.Type);
        bool selected = Selected?.Device.Name == updated.Device.Name && Selected?.Device.Type == updated.Device.Type;
        if (index >= 0) devices[index] = updated;
        selecting = true; string? token = Selected?.Device.Token;
        DevicePicker.ItemsSource = null; DevicePicker.ItemsSource = devices;
        DevicePicker.SelectedItem = devices.FirstOrDefault(s => s.Device.Token == (selected ? updated.Device.Token : token)); selecting = false;
        if (selected) Show(updated, reply.Uncertain ? "Self-test state is not confirmed. Benchmarks remain blocked until a status read succeeds." : null);
        if (reply.Message != null) MessageLabel.Text = reply.Message;
    }
    private void Show(DriveSnapshot s, string? extra = null)
    {
        HealthLabel.Text = s.Health;
        ReadLabel.Text = $"Read {s.ReadAt:yyyy-MM-dd HH:mm:ss zzz} · smartctl {s.ToolVersion}" + (s.Stale ? " · Stale reading" : s.Partial ? " · Partial reading" : "") + (extra == null ? "" : "\n" + extra);
        SummaryFields.ItemsSource = s.Fields; AttributeTable.ItemsSource = s.Attributes;
        LogsBox.Text = s.Logs.Count == 0 ? "No logs were returned by this drive." : string.Join("\n", s.Logs.Select(f => f.Name + ": " + f.Value));
        TestLabel.Text = s.SelfTest.Status + (s.SelfTest.Active ? (s.SelfTest.Percent is int p ? $" · {p}% complete" : " · Progress unavailable") : "");
        TestProgress.Visibility = s.SelfTest.Active ? Visibility.Visible : Visibility.Collapsed;
        TestProgress.IsIndeterminate = s.SelfTest.Active && !s.SelfTest.Percent.HasValue;
        TestProgress.Value = Math.Clamp(s.SelfTest.Percent ?? 0, 0, 100);
        EstimateLabel.Text = !s.SelfTest.Supported ? "Self-tests are unsupported or their capabilities could not be read." :
            $"Drive estimates: short {Estimate(s.SelfTest.ShortMinutes)}, extended {Estimate(s.SelfTest.ExtendedMinutes)}. Active tests are checked every 10 seconds.";
        MessageLabel.Text = string.Join("\n", s.Limitations);
        if (s.Identity.Length == 0) MessageLabel.Text += "\nA stable drive identity is unavailable. Self-test actions are disabled.";
        UpdateButtons();
    }
    private static string Estimate(int? minutes) => minutes.HasValue ? $"{minutes} min" : "unavailable";
    private void UpdateButtons()
    {
        bool ready = !Activity.Busy && !Activity.Benchmark && !disposed;
        RefreshButton.IsEnabled = ready && Selected != null;
        RescanButton.IsEnabled = DevicePicker.IsEnabled = ready;
        AccessButton.IsEnabled = ready && !elevated && !SmartctlPayload.Elevated;
        AccessButton.Content = elevated || SmartctlPayload.Elevated ? "Drive access enabled" : "Enable drive access";
        ShortButton.IsEnabled = ExtendedButton.IsEnabled = ready && Selected is { Stale: false, Identity.Length: > 0, SelfTest: { Supported: true, StatusKnown: true, Active: false } } && Activity.Active.Count == 0;
        AbortButton.IsEnabled = ready && Selected is { Identity.Length: > 0, SelfTest.Active: true };
        CopyDriveButton.IsEnabled = ExportDriveButton.IsEnabled = ready && Selected != null;
    }
    private async Task PollAsync()
    {
        var errors = new List<string>();
        foreach (var active in Activity.Active.ToList())
        {
            try { await ReadAsync(active, DriveOperation.Status); }
            catch (Exception ex) when (ex is IOException or TimeoutException or System.Text.Json.JsonException or FormatException) { errors.Add(active.Model + ": " + ex.Message); }
        }
        if (errors.Count > 0) throw new IOException(string.Join("\n", errors));
    }
    private async Task ActionAsync(DriveOperation operation, DriveSnapshot selected)
    {
        try { Accept(await service.SendAsync(new(operation, selected.Device.Token, selected.Identity), lifetime.Token)); }
        catch { Activity.Observe(selected, true); throw; }
    }
    private async void Device_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (selecting || Selected is not { } selected) return;
        Show(selected); await Run(() => ReadAsync(selected));
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) await Run(() => ReadAsync(s)); }
    private async void Rescan_Click(object sender, RoutedEventArgs e) => await Run(ScanAsync);
    private async void Access_Click(object sender, RoutedEventArgs e) => await Run(async () => {
        MessageLabel.Text = "Waiting for administrator access…";
        var broker = await DriveAccessBroker.StartAsync(lifetime.Token);
        await service.DisposeAsync(); service = broker; elevated = true;
        await ScanAsync();
    });
    private async void Short_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) await Run(() => ActionAsync(DriveOperation.StartShort, s)); }
    private async void Extended_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) await Run(() => ActionAsync(DriveOperation.StartExtended, s)); }
    private async void Abort_Click(object sender, RoutedEventArgs e) { if (Selected is { } s) await Run(() => ActionAsync(DriveOperation.Abort, s)); }
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        try { Clipboard.SetText(DriveReports.Text(s)); MessageLabel.Text = "Drive report copied."; }
        catch (ExternalException) { MessageLabel.Text = "The clipboard is busy. Try again."; }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } s) return;
        var dialog = new SaveFileDialog { FileName = $"SquishyDisk-DriveInfo-{DateTime.Now:yyyyMMdd-HHmmss}", Filter = "Text report (*.txt)|*.txt|JSON with raw SMART data (*.json)|*.json|SMART table (*.csv)|*.csv", AddExtension = true };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        try { File.WriteAllText(dialog.FileName, dialog.FilterIndex switch { 2 => DriveReports.Json(s), 3 => DriveReports.Csv(s), _ => DriveReports.Text(s) }, new UTF8Encoding(dialog.FilterIndex == 3)); MessageLabel.Text = "Drive report exported."; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageLabel.Text = "Export failed: " + ex.Message; }
    }
    public async Task<bool> CanCloseAsync()
    {
        poll.Stop(); await Pending;
        while (Activity.Active.Count > 0)
        {
            int choice = CloseChoice();
            if (choice == 0) { poll.Start(); return false; }
            if (choice == 1) return true;
            await Run(async () => {
                foreach (var s in Activity.Active.ToList()) await ActionAsync(DriveOperation.Abort, s);
            });
            if (Activity.Active.Count > 0) MessageBox.Show(Window.GetWindow(this), "The self-test could not be confirmed stopped.\n\n" + MessageLabel.Text, "Self-test status", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return true;
    }
    private int CloseChoice()
    {
        if (VerificationCloseChoice != null) return VerificationCloseChoice();
        int choice = 0;
        var panel = new StackPanel { Margin = new Thickness(22) };
        panel.Children.Add(new TextBlock { Text = "A drive self-test is running or its status is unresolved.\nLeaving it running allows the drive to continue after this app closes.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) });
        var window = new Window { Title = "Close during self-test", Owner = Window.GetWindow(this), Width = 570, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        var buttons = new WrapPanel();
        foreach (var item in new[] { ("Leave running and exit", 1), ("Abort and exit", 2), ("Cancel", 0) })
        {
            var b = new Button { Content = item.Item1, Margin = new Thickness(0, 0, 8, 0), IsCancel = item.Item2 == 0 };
            b.Click += (_, _) => { choice = item.Item2; window.Close(); }; buttons.Children.Add(b);
        }
        panel.Children.Add(buttons); window.ShowDialog(); return choice;
    }
    internal async Task SetVerificationService(IDriveInfoService fake)
    { await service.DisposeAsync(); service = fake; discovered = false; }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true; poll.Stop(); lifetime.Cancel(); await Pending; await service.DisposeAsync(); lifetime.Dispose();
    }
}
