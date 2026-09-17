using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using SquishyDisk.Core;

namespace SquishyDisk.App;

public partial class MainWindow
{
    private async Task VerifyDriveUiAsync(string output)
    {
        await DrivePanel.SetVerificationService(new PreviewDriveService());
        MainTabs.SelectedIndex = 1;
        await DrivePanel.Pending;
        if (DrivePanel.DevicePicker.Items.Count != 2) throw new InvalidOperationException("Drive picker did not populate.");
        preferences = preferences with { Theme = "Light" }; ApplyTheme();
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
        RenderPng(MainTabs, Path.Combine(output, "drive-light.png"));
        RenderFullDrive(Path.Combine(output, "drive-light-full.png"));
        preferences = preferences with { Theme = "Dark" }; ApplyTheme();
        UpdateLayout(); RenderPng(MainTabs, Path.Combine(output, "drive-dark.png"));
        RenderPng(MainTabs, Path.Combine(output, "drive-dark-200.png"), 2);
        DrivePanel.ShortButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        await DrivePanel.Pending;
        if (DrivePanel.Activity.Active.Count != 1 || DrivePanel.ShortButton.IsEnabled || !DrivePanel.AbortButton.IsEnabled) throw new InvalidOperationException("Self-test controls did not enter running state.");
        var before = session;
        await BeginRunAsync([new(0, rows[0].Workload, TestDirection.Read)]);
        if (!ReferenceEquals(before, session)) throw new InvalidOperationException("Benchmark started during a self-test.");
        RenderFullDrive(Path.Combine(output, "drive-active-test.png"));
        DrivePanel.VerificationCloseChoice = () => 0;
        if (await DrivePanel.CanCloseAsync() || DrivePanel.Activity.Active.Count != 1) throw new InvalidOperationException("Cancel close did not preserve the self-test.");
        DrivePanel.VerificationCloseChoice = () => 1;
        if (!await DrivePanel.CanCloseAsync() || DrivePanel.Activity.Active.Count != 1) throw new InvalidOperationException("Leave-running close aborted the self-test.");
        DrivePanel.VerificationCloseChoice = () => 2;
        if (!await DrivePanel.CanCloseAsync()) throw new InvalidOperationException("Abort-and-exit failed.");
        DrivePanel.VerificationCloseChoice = null;
        if (DrivePanel.Activity.Active.Count != 0 || !DrivePanel.ShortButton.IsEnabled) throw new InvalidOperationException("Self-test controls did not return to ready state.");
        DrivePanel.DevicePicker.SelectedIndex = 1; await DrivePanel.Pending;
        foreach (var expander in DrivePanel.DrivePage.Children.OfType<System.Windows.Controls.Expander>()) expander.IsExpanded = true;
        await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ApplicationIdle);
        File.WriteAllText(Path.Combine(output, "smart-table-layout.json"), JsonSerializer.Serialize(DrivePanel.AttributeTable.Columns.Select(c => new { c.Header, Width = c.Width.Value, c.ActualWidth, c.MinWidth })));
        RenderFullDrive(Path.Combine(output, "drive-ata.png"));
        var selected = (DriveSnapshot)DrivePanel.DevicePicker.SelectedItem;
        File.WriteAllText(Path.Combine(output, "drive-report.txt"), DriveReports.Text(selected));
        File.WriteAllText(Path.Combine(output, "drive-report.json"), DriveReports.Json(selected));
        File.WriteAllText(Path.Combine(output, "drive-report.csv"), DriveReports.Csv(selected));
        Width = 780; Height = 680; UpdateLayout(); RenderPng(MainTabs, Path.Combine(output, "drive-compact.png"));
        Width = 960; Height = 880; MainTabs.SelectedIndex = 0; UpdateLayout();
    }
    private void RenderFullDrive(string path)
    {
        var panel = DrivePanel.DrivePage;
        panel.Measure(new Size(840, double.PositiveInfinity)); panel.Arrange(new Rect(panel.DesiredSize)); panel.UpdateLayout();
        RenderPng(panel, path); UpdateLayout();
    }
    private sealed class PreviewDriveService : IDriveInfoService
    {
        private bool active;
        private static readonly DriveDevice Nvme = new("preview-nvme", "/dev/sda", "nvme", "NVMe", 0, [@"C:\"]);
        private static readonly DriveDevice Ata = new("preview-ata", "/dev/sdb", "sat", "ATA", 1, [@"D:\"]);
        public Task<DriveReply> SendAsync(DriveRequest request, CancellationToken cancellation = default)
        {
            if (request.Operation == DriveOperation.Discover) return Task.FromResult(new DriveReply(Devices: [Snapshot(Nvme), Snapshot(Ata)]));
            if (request.Operation is DriveOperation.StartShort or DriveOperation.StartExtended) active = true;
            if (request.Operation == DriveOperation.Abort) active = false;
            return Task.FromResult(new DriveReply(Snapshot: Snapshot(request.Token == Ata.Token ? Ata : Nvme)));
        }
        private DriveSnapshot Snapshot(DriveDevice device)
        {
            string json = device == Nvme ? JsonSerializer.Serialize(new {
                smartctl = new { version = new[] { 7, 5 }, exit_status = 0 }, model_name = "Example NVMe SSD (sample data)", serial_number = "SAMPLE-0001", firmware_version = "1.0",
                user_capacity = new { bytes = 1000204886016L }, smart_status = new { passed = true }, temperature = new { current = 37 }, power_on_time = new { hours = 2150 }, power_cycle_count = 142,
                nvme_optional_admin_commands = new { self_test = true }, nvme_version = new { @string = "NVMe 1.4" },
                nvme_smart_health_information_log = new { critical_warning = 0, percentage_used = 3, available_spare = 100, available_spare_threshold = 10, data_units_read = 11000000, data_units_written = 7000000, unsafe_shutdowns = 2, media_errors = 0 },
                nvme_self_test_log = new { current_self_test_operation = new { value = active ? 1 : 0, @string = active ? "Short self-test in progress" : "No self-test in progress" }, current_self_test_completion_percent = active ? 40 : 0,
                    table = new[] { new { self_test_code = new { value = 1, @string = "Short" }, self_test_result = new { value = 0, @string = "Completed without error" }, power_on_hours = 2149 } } }
            }) : JsonSerializer.Serialize(new {
                smartctl = new { version = new[] { 7, 5 }, exit_status = 64 }, model_name = "Example SATA HDD (sample data)", serial_number = "SAMPLE-0002", firmware_version = "2.1",
                user_capacity = new { bytes = 4000787030016L }, smart_status = new { passed = true }, temperature = new { current = 29 }, power_on_time = new { hours = 12460 }, power_cycle_count = 397,
                ata_smart_data = new { capabilities = new { self_tests_supported = true }, self_test = new { status = new { value = 0, @string = "Completed without error" }, polling_minutes = new { @short = 2, extended = 480 } } },
                ata_smart_attributes = new { table = new[] {
                    new { id = 5, name = "Reallocated_Sector_Ct", value = 100, worst = 100, thresh = 10, raw = new { value = 0, @string = "0" } },
                    new { id = 9, name = "Power_On_Hours", value = 98, worst = 98, thresh = 0, raw = new { value = 12460, @string = "12460" } },
                    new { id = 194, name = "Temperature_Celsius", value = 71, worst = 62, thresh = 0, raw = new { value = 29, @string = "29 (Min/Max 18/38)" } } } },
                ata_smart_error_log = new { summary = new { count = 1 } }
            });
            var snapshot = SmartParser.Parse(device, json, 0);
            return snapshot with { ReadAt = new DateTimeOffset(2026, 9, 16, 14, 30, 0, TimeSpan.FromHours(12)) };
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
