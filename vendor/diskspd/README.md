# DiskSpd engine provenance

The embedded x64 engine is built from Microsoft's MIT-licensed DiskSpd source:

- Tag: `v2.3`
- Commit: `5e7025bfc9d1364f185d4c30963d7ac79195435e`
- Source: https://github.com/microsoft/diskspd/tree/v2.3
- License: [LICENSE.txt](LICENSE.txt)
- Binary integrity: [engine.sha256](engine.sha256)

This is our source build, **not** the binary from Microsoft's downloadable ZIP. That ZIP carries separate binary license terms.

## Small integration changes

`integration.patch` contains all source changes:

1. Expose the command parser's start/finish event handles and connect them to the existing notification callbacks. Upstream v2.3 creates those handles but never assigns the application's notification globals, so `-ys` and `-yf` do not signal.
2. Escape XML text in file paths. A folder containing `&` otherwise produces invalid XML results.

`engine.manifest` enables UTF-8 for the engine's narrow Win32 filesystem calls and command-line arguments on Windows 10 1903 and newer. The published app targets Windows 10 22H2 and Windows 11 x64.

These changes leave the I/O loop and statistical calculations unchanged. Exports identify the patched source build.

## Rebuild

Run `scripts/build-engine.ps1 -Toolchain <portable-MSVC-folder>`. The folder must contain `VC/Tools/MSVC/<version>` (including ATL headers/libraries) and `Windows Kits/10`. This build used MSVC 14.51 and Windows SDK 10.0.26100.0. The script pins the source revision, verifies its patch, builds with `/O2 /MT`, adds the manifest, and updates the binary checksum. `/MT` includes the C++ runtime; the engine needs no separate Visual C++ redistributable installation.

Normal app builds use the checked-in engine and do not need C++ build tools.
