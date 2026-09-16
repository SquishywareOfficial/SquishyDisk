# Third-party components

Original SquishyDisk work is licensed under **WTFPL v2 with a separate NO WARRANTY notice**. See [LICENSE](LICENSE) and [COPYING.WTFPL](COPYING.WTFPL).

Bundled components retain their original licenses:

| Component | License | Included notice |
|---|---|---|
| Microsoft DiskSpd 2.3, built from source with the documented integration patch | MIT; Copyright (c) 2014 Microsoft | `vendor/diskspd/LICENSE.txt` |
| smartctl 7.5 and its drive database, unmodified from smartmontools | GPL-2.0-or-later; smartmontools authors | `vendor/smartctl/COPYING.txt`, `vendor/smartctl/AUTHORS.txt` |
| Microsoft .NET runtime 10.0.12 | MIT; .NET Foundation and contributors | `vendor/dotnet/LICENSE.txt` |
| Microsoft Windows Desktop / WPF runtime 10.0.12 | MIT; .NET Foundation and contributors | `vendor/dotnet/WPF-LICENSE.txt` |
| Third-party code included by the .NET runtime | Licenses identified in the supplied notices | `vendor/dotnet/THIRD-PARTY-NOTICES.txt` |

The full texts above, the app's WTFPL, and its warranty notice are embedded in the EXE and readable through **About**. Retain the relevant component notices when redistributing those components.

Sources:

- DiskSpd: https://github.com/microsoft/diskspd/tree/v2.3
- .NET runtime: https://github.com/dotnet/runtime
- WPF: https://github.com/dotnet/wpf

The embedded engine is a build from MIT-licensed source. It is not Microsoft's separately licensed prebuilt ZIP binary. See `vendor/diskspd/README.md` for the source revision, patch, build instructions, and checksum.

smartctl runs as an independent executable through ordinary command-line JSON communication. Its full GPL and author notices are available in About. See `vendor/smartctl/README.md` for the exact version, hashes and build provenance. Each release must provide the generated `smartctl-7.5-source.zip` alongside the portable EXE; it contains matching source and build scripts. The source package is not a runtime dependency.
