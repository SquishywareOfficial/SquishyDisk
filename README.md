# SquishyDisk

A portable Windows storage benchmark and drive information utility from Squishyware, powered by Microsoft DiskSpd 2.3 and smartctl 7.5.

## Run

Download **SquishyDisk.exe** from the [latest release](https://github.com/SquishywareOfficial/SquishyDisk/releases/latest), or build it locally at `artifacts/portable/SquishyDisk.exe`. The EXE includes .NET, DiskSpd and smartctl; no installer or internet connection is required. Benchmarks run as a normal user; some Drive Info operations need administrator access. Target: Windows 10 22H2 and Windows 11, Intel/AMD x64.

1. Choose a drive or use **Folder…** to select a writable folder, including a network share.
2. Choose **Standard** or **NVMe**, file size, and pass count.
3. Click **Run all tests**, a workload row, or an individual read/write cell.
4. Switch between MB/s, GB/s, IOPS, and average latency. Copy results or export text, CSV, JSON, or PNG.

The test file is created and fully initialized on the selected volume, then removed. A read benchmark also needs write permission to prepare that temporary file. The system-drive shortcut uses your temporary folder so normal-user operation works; the full target folder is always shown.

## Behavior

### Drive Info

Open **Drive Info** to discover physical drives, including unmounted devices supported by smartctl. The app reads identity, reported health, temperature, lifetime counters, ATA attributes, NVMe/SCSI data, error logs and self-test history. Missing values remain **Unavailable**. **Refresh** reads the selected drive; **Rescan** discovers connected devices. Data is read on demand except while a known self-test is active.

**Enable drive access** opens a UAC prompt for a separate helper. The main interface remains at its original privilege level. The helper uses an authenticated local pipe and exposes only discovery, reading, short/extended self-tests and abort. The pipe permits the initiating account and administrators, and both ends verify the other process ID and session nonce.

**Short test** and **Extended test** start a drive's background diagnostic where supported. Status is checked every 10 seconds; actual progress and drive time estimates are shown when available. Only one app-started test can run at a time. Benchmarks are blocked while a known self-test is active or its outcome is unresolved; SMART operations pause during benchmarks. This does not detect tests started by another tool after the last reading until the next refresh. Unsupported bridge, RAID or Windows driver commands remain explicit limitations.

Closing during a known test offers **Leave running and exit**, **Abort and exit**, or **Cancel**. A drive test can continue after the app closes. The app rechecks identity before sending start/abort, and reads status after a command timeout rather than retrying the command. There are no captive tests, repair/erase commands, firmware updates or automatic drive-setting changes.

Drive reports are separate from benchmark results: copy text or export text, JSON (including original smartctl JSON), or CSV. Reports include the reading timestamp, identity, helper version and limitations. Serial numbers are included. A stale reading is labelled in the UI and exported reports.

smartctl 7.5 and its pinned drive database are embedded in the EXE. A normal helper extracts into a private temporary directory; an elevated helper uses a fresh directory under Common Program Files with an administrators/SYSTEM-only ACL. Extracted files are hash checked and held against modification while used, then removed on normal exit. No service is installed. A forced process termination can leave a helper directory behind.

### Benchmark

- Defaults: 1 GiB, 3 passes, 5 seconds measured, 1 second warm-up, 1 second between passes; random data; Windows caching disabled, normal device write caching retained.
- **Settings** edits four workloads: sequential/random, 4 KiB–1 MiB blocks, Q1–64 per thread, T1–16, measurement/warm-up/interval, data pattern, and optional write-through.
- The fastest completed pass supplies all metrics in a cell. MB/s and GB/s are decimal; file/block sizes are binary MiB/GiB and KiB. Latency is DiskSpd's measured aggregate mean, converted to µs; the engine reports it with 1 µs precision.
- Individual reruns replace the selected cells and retain other cells with the same configuration. Changing the target, preset, or test settings clears the grid. JSON and CSV retain every completed pass.
- **Stop** cancels preparation, warm-up, measurement, or intervals. Closing the app cancels and waits for cleanup. A Windows Job Object stops DiskSpd if the UI process crashes. Next launch attempts recovery of owned files; reconnect an unavailable target to allow recovery.
- Cleanup requires an exact session folder and ownership marker. Unknown files, links/junctions, and raw devices are never benchmark targets for this app. Cleanup errors include the path for inspection.
- Preferences are stored as `SquishyDisk.settings.json` beside the EXE. On first launch, existing `DiskSpdUI.settings.json` preferences are imported if the new file does not exist; the original file is retained. Read-only launch folders use session-only preferences. Runtime/engine caches and recovery records live under the user's temporary directory. No telemetry or auto-update service is included.

The engine is a source build with small event-notification and XML-path fixes. See [engine provenance](vendor/diskspd/README.md). Workloads resemble CrystalDiskMark, but results are not guaranteed to match its older DiskSpd engine or test preparation. This is an independent app with its own UI and artwork.

## Build and publish

Run in PowerShell 7:

```powershell
./scripts/publish.ps1
```

The script uses .NET SDK 10.0.401, downloading a checksum-verified local SDK into `.tools/dotnet` if needed. It builds, runs the deterministic/file lifecycle checks, publishes one self-contained EXE, and writes `artifacts/SquishyDisk.sha256`.

It also verifies the pinned smartctl files and creates `artifacts/smartctl-7.5-source.zip`. Provide that matching source package alongside any published EXE. Only the EXE is needed to run the app. See [smartctl provenance](vendor/smartctl/README.md).

### Versioning and automatic releases

`BaseVersion` in `Directory.Build.props` is the single place to change the base version, currently `0.1.0`. Executable metadata, About and reports use `BaseVersion.BuildNumber`. Local builds default to `0.1.0.0`; an explicit local build number can be supplied with `./scripts/publish.ps1 -BuildNumber 12`.

The [release workflow](.github/workflows/release.yml) builds the latest commit of each push to `main` on Windows x64 with the pinned SDK. It also supports manual runs on `main`. GitHub's workflow run number starts at 1, increases for each new run, and stays the same on retries. Failed runs can leave gaps in released build numbers. Changing the base version does not reset the counter.

Each successful build publishes a regular release tagged `v0.1.0.BUILDNUMBER` at the exact built commit, with `SquishyDisk.exe`, its SHA-256 checksum, the matching smartctl source ZIP and its checksum. Dependency verification and fast tests must pass first. Asset uploads finish in a draft before publication; retries resume that draft or leave an already completed release unchanged. New pushes do not cancel older builds.

Run real-engine integration tests (small, temporary 16 MiB files):

```powershell
./.tools/dotnet/dotnet.exe tests/SquishyDisk.Tests/bin/Release/net10.0-windows/SquishyDisk.Tests.dll --integration
```

Developer UI verification renders actual WPF controls in light/dark themes and at 1×/2× resolution, runs a short read/write test, and checks cancellation:

```powershell
./artifacts/portable/SquishyDisk.exe --verify-ui ./artifacts/ui-check
```

Close any other SquishyDisk instance first. Verification mode does not save preferences. Normal runs never start a benchmark automatically.

Add `--drive-integration` to the test executable for read-only SMART hardware checks. Default tests use simulated ATA/NVMe/SCSI data and simulated self-test commands; the UI harness also tests start/abort and benchmark exclusion without starting a firmware test.

## Layout

- `src/SquishyDisk.Core`: workload definitions, validation, commands, XML parsing, and reports.
- `src/SquishyDisk.Windows`: engine extraction, process/job management, temporary files, recovery, and preferences.
- `src/SquishyDisk.App`: WPF interface and visual verification.
- `tests/SquishyDisk.Tests`: dependency-free test executable with optional real-engine coverage.

See [validation notes](VALIDATION.md) for tested environments and remaining hardware checks. An installer, saved session comparisons, mixed workloads, and arbitrary DiskSpd XML profile import are deferred.

## License

Our original code, documentation, and artwork use **WTFPL v2**, with a separate **NO WARRANTY** notice. See [LICENSE](LICENSE) and [COPYING.WTFPL](COPYING.WTFPL).

Microsoft DiskSpd, .NET, and WPF retain their **MIT** notices. The runtime's additional third-party notices are included too. All license texts are embedded in the portable EXE and available under **About**. See [third-party components](THIRD-PARTY-NOTICES.md).

The separate, unmodified smartctl executable is distributed under **GPL v2 or later**. Its notices and matching source package accompany every release.
