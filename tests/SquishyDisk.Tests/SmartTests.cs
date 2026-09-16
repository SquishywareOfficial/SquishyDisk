using System.Text.Json;
using SquishyDisk.Core;
using SquishyDisk.Windows;

namespace SquishyDisk.Tests;

internal static partial class Program
{
    private static readonly DriveDevice SmartDevice = new("test-token", "/dev/sda", "sat", "ATA");
    private const string AtaFixture = """
        {"json_format_version":[1,0],"smartctl":{"version":[7,5],"exit_status":0},
        "model_name":"Example SATA SSD","serial_number":"TEST-001","firmware_version":"1.2",
        "user_capacity":{"bytes":1000204886016},"smart_status":{"passed":true},"temperature":{"current":34},
        "power_on_time":{"hours":1250},"power_cycle_count":85,
        "ata_smart_data":{"capabilities":{"self_tests_supported":true},"self_test":{"status":{"value":0,"string":"Completed without error"},"polling_minutes":{"short":2,"extended":90}}},
        "ata_smart_attributes":{"table":[{"id":5,"name":"Reallocated_Sector_Ct","value":100,"worst":100,"thresh":10,"when_failed":"","raw":{"value":0,"string":"0"}},
        {"id":9,"name":"Power_On_Hours","value":99,"worst":99,"thresh":0,"raw":{"value":1250,"string":"1250"}}]},
        "ata_smart_self_test_log":{"standard":{"table":[{"num":1,"type":{"value":1,"string":"Short offline"},"status":{"value":0,"string":"Completed without error"},"lifetime_hours":1240}]}}}
        """;
    private const string NvmeFixture = """
        {"smartctl":{"version":[7,5],"exit_status":72},"model_name":"Example NVMe","serial_number":"NVME-001","user_capacity":{"bytes":2000409260032},
        "smart_status":{"passed":false},"nvme_optional_admin_commands":{"self_test":true},
        "nvme_smart_health_information_log":{"critical_warning":4,"percentage_used":103,"available_spare":98,"data_units_read":18446744073709551615,"data_units_read_s":"340282366920938463463374607431768211455","data_units_written":12000,"media_errors":4},
        "nvme_self_test_log":{"current_self_test_operation":{"value":2,"string":"Extended self-test in progress"},"current_self_test_completion_percent":43,"table":[{"self_test_result":{"value":0,"string":"Completed without error"}}]}}
        """;
    private const string ScsiFixture = """
        {"smartctl":{"version":[7,5],"exit_status":0,"output":["Self-test execution status:\t\t72% of test remaining"]},
        "vendor":"EXAMPLE","product":"SAS disk","serial_number":"SAS-001","smart_status":{"passed":true},"scsi_extended_self_test_seconds":18001,
        "scsi_self_test_0":{"code":{"value":2,"string":"Background long"},"result":{"value":15,"string":"Self test in progress ..."},"self_test_in_progress":true},
        "scsi_error_counter_log":{"read":{"errors_corrected_by_eccfast":9007199254740993,"total_uncorrected_errors":0}}}
        """;
    private static async Task SmartChecks(string[] args)
    {
        await Check("SMART ATA identity, attributes, capabilities and logs", () => {
            var s = SmartParser.Parse(SmartDevice, AtaFixture, 0);
            Equal("Passed", s.Health); True(s.Identity.Contains("TEST-001")); Equal(2, s.Attributes.Count);
            True(s.SelfTest.Supported && s.SelfTest.StatusKnown && !s.SelfTest.Active); Equal(90, s.SelfTest.ExtendedMinutes);
            True(s.Logs.Any(l => l.Value == "Short offline"));
        });
        await Check("SMART NVMe fault bits, active tests and exact 128-bit counters", () => {
            var s = SmartParser.Parse(SmartDevice, NvmeFixture, 72);
            Equal("Failing", s.Health); True(!s.Partial); True(s.SelfTest.Active); Equal(43, s.SelfTest.Percent);
            True(s.Fields.Single(f => f.Name == "Host data read").Value.Contains("174224571863520493293247799005065324264960000"));
            True(DriveReports.Json(s).Contains("340282366920938463463374607431768211455"));
        });
        await Check("SMART SCSI progress and exact counters", () => {
            var s = SmartParser.Parse(SmartDevice, ScsiFixture, 0);
            Equal("SAS disk", s.Model); True(s.SelfTest.Active); Equal(28, s.SelfTest.Percent); Equal(301, s.SelfTest.ExtendedMinutes);
            True(s.Logs.Any(l => l.Value == "9007199254740993"));
            var empty = SmartParser.Parse(SmartDevice, """{"smartctl":{"output":["No Self-tests have been logged"]}}""", 0);
            True(empty.SelfTest.Supported && empty.SelfTest.StatusKnown && !empty.SelfTest.Active);
        });
        await Check("SMART partial readings retain valid data and unavailable stays unknown", () => {
            var s = SmartParser.Parse(SmartDevice, AtaFixture, 4); True(s.Partial); Equal(2, s.Attributes.Count); Equal("Passed", s.Health);
            var denied = SmartParser.Parse(SmartDevice, """{"smartctl":{"exit_status":2,"messages":[{"string":"Access denied","severity":"error"}]}}""", 2);
            Equal("Unavailable", denied.Health); True(!denied.SelfTest.StatusKnown); True(denied.Limitations.Contains("Access denied"));
            Throws(() => SmartParser.Parse(SmartDevice, "broken", 0)); Throws(() => SmartParser.Parse(SmartDevice, "{}", 0));
        });
        await Check("ATA standardized sector totals use the reported logical sector size", () => {
            var root = System.Text.Json.Nodes.JsonNode.Parse(AtaFixture)!;
            root["logical_block_size"] = 4096;
            root["endurance_used"] = new System.Text.Json.Nodes.JsonObject { ["current_percent"] = 12 };
            root["ata_device_statistics"] = System.Text.Json.Nodes.JsonNode.Parse("""{"pages":[{"number":1,"table":[{"offset":40,"value":123456789,"flags":{"valid":true,"normalized":false}},{"offset":24,"value":999,"flags":{"valid":false,"normalized":false}}]}]}""");
            var s = SmartParser.Parse(SmartDevice, root.ToJsonString(), 0);
            True(s.Fields.Single(f => f.Name == "Host data read").Value.Contains("505679007744 bytes"));
            Equal("Unavailable", s.Fields.Single(f => f.Name == "Host data written").Value);
            Equal("12", s.Fields.Single(f => f.Name == "Endurance used (%)").Value);
        });
        await Check("SMART reports include identity and protect spreadsheet formulas", () => {
            var s = SmartParser.Parse(SmartDevice, AtaFixture, 0) with { Model = "=malicious()", Limitations = ["Partial reading"] };
            string csv = DriveReports.Csv(s); True(csv.Contains("\"'=malicious()\"")); True(csv.Contains("Partial reading"));
            True(DriveReports.Text(s).Contains(s.ReadAt.ToString("O"))); True(DriveReports.Json(s).Contains("RawJson"));
        });
        await Check("Drive activity excludes benchmarks and retains unknown self-tests", () => {
            var gate = new DriveActivity(); True(gate.TryDrive()); True(!gate.TryBenchmark()); gate.EndDrive(); True(gate.TryBenchmark()); True(!gate.TryDrive()); gate.EndBenchmark();
            var active = SmartParser.Parse(SmartDevice, NvmeFixture, 72); gate.Observe(active); True(!gate.TryBenchmark());
            gate.Observe(active with { SelfTest = new(false, false, false, "Unavailable") }); Equal(1, gate.Active.Count);
            gate.Observe(active with { SelfTest = new(true, true, false, "Stopped") }); True(gate.TryBenchmark()); gate.EndBenchmark();
            gate.Observe(active, true); True(!gate.TryBenchmark());
            gate.Observe(active with { Stale = true, SelfTest = new(true, true, false, "Old reading") }); True(!gate.TryBenchmark());
        });
        await CheckAsync("Self-tests require registered tokens and matching identity", async () => {
            var runner = new FakeSmartRunner(); await using var service = new SmartctlService(runner);
            await ThrowsAsync(() => service.SendAsync(new(DriveOperation.StartShort, "arbitrary /dev/path", "id")));
            var s = (await service.SendAsync(new(DriveOperation.Discover))).Devices!.Single();
            await ThrowsAsync(() => service.SendAsync(new(DriveOperation.StartShort, s.Device.Token, "wrong")));
            runner.Serial = "REPLACED";
            await ThrowsAsync(() => service.SendAsync(new(DriveOperation.Abort, s.Device.Token, s.Identity)));
            Equal(0, runner.Actions);
        });
        await CheckAsync("Self-test start, existing-test rejection, progress and abort", async () => {
            var runner = new FakeSmartRunner(); await using var service = new SmartctlService(runner);
            var s = (await service.SendAsync(new(DriveOperation.Discover))).Devices!.Single();
            var started = await service.SendAsync(new(DriveOperation.StartShort, s.Device.Token, s.Identity));
            True(started.Snapshot!.SelfTest.Active); True(!started.Uncertain); Equal(1, runner.Actions);
            await ThrowsAsync(() => service.SendAsync(new(DriveOperation.StartExtended, s.Device.Token, s.Identity))); Equal(1, runner.Actions);
            var stopped = await service.SendAsync(new(DriveOperation.Abort, s.Device.Token, s.Identity)); True(!stopped.Snapshot!.SelfTest.Active); True(!stopped.Uncertain); Equal(2, runner.Actions);
        });
        await CheckAsync("Timed-out self-test command is reconciled and never retried", async () => {
            var runner = new FakeSmartRunner { TimeoutAction = true }; await using var service = new SmartctlService(runner);
            var s = (await service.SendAsync(new(DriveOperation.Discover))).Devices!.Single();
            var result = await service.SendAsync(new(DriveOperation.StartExtended, s.Device.Token, s.Identity));
            Equal(1, runner.Actions); True(result.Snapshot!.SelfTest.Active); True(!result.Uncertain);
            runner.FailReadsAfterAction = true;
            var abort = await service.SendAsync(new(DriveOperation.Abort, s.Device.Token, s.Identity));
            True(abort.Uncertain); Equal(2, runner.Actions);
        });
        await CheckAsync("Drive removal and unsupported operations do not send mutations", async () => {
            var runner = new FakeSmartRunner(); await using var service = new SmartctlService(runner);
            var s = (await service.SendAsync(new(DriveOperation.Discover))).Devices!.Single();
            runner.FailReads = true;
            await ThrowsAsync(() => service.SendAsync(new(DriveOperation.StartShort, s.Device.Token, s.Identity)));
            await ThrowsAsync(() => service.SendAsync(new((DriveOperation)999, s.Device.Token, s.Identity)));
            Equal(0, runner.Actions);
        });
        await CheckAsync("Bundled helper hash, JSON mode and offline database", async () => {
            using var runner = new SmartctlRunner();
            var result = await runner.RunAsync(["--version"], CancellationToken.None);
            using var doc = JsonDocument.Parse(result.Json);
            Equal("7", SmartParser.At(doc.RootElement, "smartctl", "version")[0].GetRawText());
            Equal(0, result.ExitCode);
        });
        await CheckAsync("Drive IPC framing rejects malformed and oversized packets", async () => {
            using var packet = new MemoryStream();
            await DriveAccessBroker.WritePacket(packet, new DriveRequest(DriveOperation.Read, "token"), CancellationToken.None);
            packet.Position = 0;
            var request = await DriveAccessBroker.ReadPacket<DriveRequest>(packet, CancellationToken.None);
            Equal("token", request.Token); Equal(DriveOperation.Read, request.Operation);
            using var oversized = new MemoryStream(BitConverter.GetBytes(int.MaxValue));
            await ThrowsAsync(() => DriveAccessBroker.ReadPacket<DriveRequest>(oversized, CancellationToken.None));
            using var truncated = new MemoryStream([10, 0, 0, 0, 123]);
            await ThrowsAsync(() => DriveAccessBroker.ReadPacket<DriveRequest>(truncated, CancellationToken.None));
        });
        await CheckAsync("Private local pipe supports async transport and prevents a second server", async () => {
            string name = "SquishyDisk-test-" + Guid.NewGuid().ToString("N");
            using var server = DriveAccessBroker.CreateLocalPipe(name);
            Throws(() => { using var duplicate = DriveAccessBroker.CreateLocalPipe(name); });
            using var client = new System.IO.Pipes.NamedPipeClientStream(".", name, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var connection = server.WaitForConnectionAsync(timeout.Token); await client.ConnectAsync(timeout.Token); await connection;
            await DriveAccessBroker.WritePacket(client, "session-nonce", timeout.Token);
            Equal("session-nonce", await DriveAccessBroker.ReadPacket<string>(server, timeout.Token));
        });
        if (args.Contains("--drive-integration"))
            await CheckAsync("Read-only drive discovery and SMART hardware smoke test", async () => {
                await using var service = new SmartctlService();
                var reply = await service.SendAsync(new(DriveOperation.Discover));
                True(reply.Devices is { Count: > 0 });
                foreach (var s in reply.Devices!) Console.WriteLine($"  {s.Device.Name}: {s.Device.Protocol}, health {s.Health}, partial {s.Partial}, status known {s.SelfTest.StatusKnown}, mapped {s.Device.DiskNumber.HasValue}");
                True(reply.Devices!.Any(s => s.Identity.Length > 0 && s.Health != "Unavailable"));
            });
    }
    private static async Task ThrowsAsync(Func<Task> action) { try { await action(); } catch { return; } throw new Exception("Expected an exception."); }
    private sealed class FakeSmartRunner : ISmartctlRunner
    {
        public string Serial = "TEST-001";
        public bool Active, TimeoutAction, FailReads, FailReadsAfterAction;
        public int Actions;
        public Task<SmartctlOutput> RunAsync(IReadOnlyList<string> args, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            if (args.Contains("--scan")) return Task.FromResult(new SmartctlOutput(0, """{"smartctl":{"exit_status":0},"devices":[{"name":"/dev/sda","type":"sat","protocol":"ATA"}]}""", ""));
            if (args.Contains("-t") || args.Contains("-X"))
            {
                Actions++; Active = args.Contains("-t"); if (FailReadsAfterAction) FailReads = true;
                if (TimeoutAction) throw new TimeoutException("Simulated timeout after command accepted");
                return Task.FromResult(new SmartctlOutput(0, """{"smartctl":{"exit_status":0}}""", ""));
            }
            if (FailReads) throw new IOException("Simulated removal");
            string json = AtaFixture.Replace("TEST-001", Serial, StringComparison.Ordinal);
            if (Active) json = json.Replace("\"status\":{\"value\":0,\"string\":\"Completed without error\"}", "\"status\":{\"value\":249,\"remaining_percent\":90,\"string\":\"Self-test routine in progress\"}", StringComparison.Ordinal);
            return Task.FromResult(new SmartctlOutput(0, json, ""));
        }
        public void Dispose() { }
    }
}
