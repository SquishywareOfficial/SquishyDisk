# Validation

## SquishyDisk rebrand — 0.1.0.0

Verified on the same Windows 11 Pro x64 host, September 16, 2026:

- The renamed solution builds and publishes one portable `SquishyDisk.exe` with zero warnings/errors. File and product versions are `0.1.0.0`; company metadata is Squishyware.
- All 28 fast checks pass, including version reporting across assemblies and exports, renamed helper pipe validation, and legacy settings import without overwriting current preferences.
- The published EXE passes the WPF smoke harness: light/dark layouts, Drive Info simulations, a small real read/write benchmark and cancellation. The rendered interface shows SquishyDisk branding.

Evidence: `artifacts/ui-rebrand-final`, `artifacts/release.json`, and `artifacts/SquishyDisk.sha256`. The hardware coverage below predates the rebrand; it was not repeated for this change.

## Earlier Drive Info validation — 0.1.0

Verified on this Windows 11 Pro x64 host, September 16, 2026. The published file is `artifacts/portable/SquishyDisk.exe` (about 62.9 MiB with Drive Info). Its current SHA-256 is recorded in `artifacts/SquishyDisk.sha256`.

### Drive Info update

- Release builds completed with zero warnings/errors. All 25 default checks passed on the published build's source, plus the read-only hardware discovery check during this change. The seven additional real DiskSpd integration checks also passed: complete preset runs, cancellation, Unicode paths, forced exit and cleanup.
- Tests cover ATA/NVMe/SCSI parsing, bitmask exit codes, partial/denied/malformed responses, exact 128-bit counters, ATA sector units, missing capabilities, reports/CSV formula protection, registered device tokens, identity changes, start/status/abort, existing tests, action timeouts, removal, benchmark exclusion and retention of unresolved states.
- Private pipe tests cover asynchronous transport, exclusive server creation, packet bounds and truncated responses. Both production endpoints check peer process IDs and a session nonce. The native pipe rejects remote clients.
- Read-only hardware check on this host: NVMe SMART health/status succeeded as a normal user. The second ATA device returned access denied and remained an explicit partial reading. No real firmware self-test was started or aborted.
- WPF verification exercises the new tab in light/dark themes, 1×/2× rendering and the minimum window size; fake-drive tests verify start/abort, blocking benchmarks and all three close choices (cancel, leave running, abort). Text/CSV/JSON drive reports were generated. Expanded SMART columns and logs were visually checked.
- The final single-file EXE passed the WPF harness with runtime lookup redirected to nonexistent directories, multilevel lookup disabled, and a fresh bundle extraction directory. That run also completed real temporary-file read/write benchmarks and cancellation. Its publish folder contains only the EXE.
- The Windows smartctl installer signature verified successfully. The extracted executable matches upstream's included SHA-256; the release source archive matches upstream's MD5. Runtime extraction verifies embedded executable/database SHA-256 values. Publishing checks the pinned files and generates the matching source ZIP. No system package was installed.

Current evidence: `artifacts/test-results/drive-final.txt`, `artifacts/ui-drive-portable`, and `artifacts/smartctl-7.5-source.zip`. Older benchmark-only evidence below predates this addition.

### Drive Info checks still requiring manual hardware coverage

- UAC accept/cancel, helper termination and elevation using another administrator account. The transport is tested locally, but the complete interactive UAC flow has not been exercised on this host.
- Real short/extended self-tests and abort on ATA, NVMe and SCSI devices; controller/USB bridge compatibility; active-device removal/replacement. Simulations cover the control flow, not firmware behaviour.
- Multi-disk volumes, RAID members, unmounted/mount-point volumes, sleep/wake behaviour and a clean Windows 10 installation. Disk-to-volume associations are shown only for verified Windows device numbers; unsupported mappings remain explicit.

## Passed

- Release build: zero warnings and zero errors.
- **18 automated checks**: preset/argument mapping, counter aggregation, locale-independent XML parsing, invalid XML rejection, coherent best-pass selection, target/settings validation, incomplete-session exports, preference persistence and corrupt-file fallback, ownership checks, and cleanup.
- Official-engine profile validation of every read/write workload in both presets.
- Complete **Standard and NVMe** suites using temporary 16 MiB files and one-second measurements, plus repeated-pass aggregation. These are functional checks, not representative performance scores.
- Real read/write XML, throughput/IOPS/latency values, cancellation during preparation/warm-up/measurement, Unicode and ampersand paths, forced parent-process termination, and next-launch recovery.
- Published EXE: actual WPF rendering in light/dark themes, compact layout and 1×/2× rendering, real read/write runs, cancellation, and return of controls to the ready state.
- Portable launch from a folder containing **only the EXE**, with .NET runtime lookup redirected to nonexistent directories and multilevel lookup disabled.
- The same published EXE launched successfully from a directory with an explicit write-deny ACL. The test confirmed writes were denied; the application completed its small benchmark and exited successfully. The ACL was restored afterward.
- No remaining DiskSpd/UI test processes or outstanding recovery journals after the checks.
- License notices are included as embedded resources: original app WTFPL v2 plus separate NO WARRANTY, DiskSpd MIT, .NET/WPF MIT, and the runtime's additional third-party notices.

Evidence is under `artifacts/test-results`, `artifacts/ui-check`, and `artifacts/portability-result.json`. UI preview images with sample numbers explicitly say “sample data”; `ui-real-results.png` comes from the actual engine test.

## Still to verify on separate environments

- A clean Windows 10 22H2 machine and a clean Windows 11 machine. The isolated-runtime launch is not a substitute for testing another OS installation.
- Physical drive unplug/reconnect, network shares, FAT32 file-size limits, and disk-full behavior on suitable test media. Related validation/error-handling code exists; those hardware conditions were not created here.
- Physical monitor transitions at different DPI settings, keyboard/screen-reader review, and extended thermal/performance runs.
- Public code signing and installer distribution. This portable development build is unsigned.

## Host restart observed during development

Windows System event 1074 recorded planned restarts initiated by `MoUsoCoreWorker.exe` at 02:29 and `TrustedInstaller.exe` at 02:30. The reasons were operating-system servicing/upgrade. No restart command was issued by the app build workflow. .NET was unpacked into `.tools/dotnet`; C++ build tools were extracted into `.tools/msvc`.
