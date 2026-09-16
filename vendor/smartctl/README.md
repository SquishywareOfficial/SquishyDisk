# smartctl 7.5 provenance and redistribution

SquishyDisk invokes this unmodified x64 executable as a separate process. It does not link smartmontools code into the app. The database is supplied directly to smartctl. The app never downloads updates at runtime and does not include smartd.

## Upstream files

- Release: https://github.com/smartmontools/smartmontools/releases/tag/RELEASE_7_5
- Signed Windows distribution: https://github.com/smartmontools/smartmontools/releases/download/RELEASE_7_5/smartmontools-7.5.win32-setup.exe
- Release source: https://github.com/smartmontools/smartmontools/releases/download/RELEASE_7_5/smartmontools-7.5.tar.gz
- Exact Git source revision reported by the executable: `22539e09791ca3bc4a6947952d876cee79d454e4` (SVN r5714).
- Git source archive: https://codeload.github.com/smartmontools/smartmontools/tar.gz/22539e09791ca3bc4a6947952d876cee79d454e4
- Original Windows build recipe: https://github.com/smartmontools/smartmontools/blob/22539e09791ca3bc4a6947952d876cee79d454e4/.appveyor.yml

The installer was extracted as an archive with 7-Zip; it was not run or installed. `bin/smartctl.exe`, `bin/drivedb.h`, `doc/COPYING.txt`, `doc/AUTHORS.txt`, and `doc/checksums64.txt` were copied without modification. Windows verified the installer's Authenticode signature as valid. Its SHA-256 is `896337fcc253220614cf8cdbd5cf2321c5aa326a37a04160a672a281e6104c70`.

The executable hash matches upstream's included `checksums64.txt`. `SHA256SUMS` pins our bundled files and source archives. The release source tarball matches upstream's MD5 (`38c38b0b82db7fc4906cdd50d15a7931`). The upstream detached source signature is included; it was not independently verified here.

The database matches the source release's entries; the Windows package leaves its SVN `$Id$` keyword unexpanded. Its exact source text is included separately in the source bundle.

## Build provenance

The executable reports smartctl 7.5, 2025-04-30 r5714, x86_64-w64-mingw32, AppVeyor, GCC 10-win32 20220113, MinGW-w64 8.0.0, and SOURCE_DATE_EPOCH=1745999024. The original `appveyor.yml` and exact Git snapshot are supplied, including autogen/configure inputs, makefiles, Windows resources, and installer scripts. The release tarball also contains generated configure/build files and INSTALL instructions. No source or binary patches were applied by this project.

For a rebuild, use the toolchain versions above and the pinned AppVeyor recipe (Ubuntu 22.04, MinGW-w64 cross compilation). The x64 build runs `autogen.sh`, then configures with `host_alias=x86_64-w64-mingw32` and the pinned SOURCE_DATE_EPOCH, and runs make with BUILD_INFO set to `(AppVeyor)`. The original recipe also builds x86 and an NSIS installer; neither is needed by this app. A bit-for-bit rebuild was not performed here.

## License and release files

smartctl and drivedb.h are GPL-2.0-or-later. Retain COPYING, AUTHORS and the source notices. The app's own WTFPL + NO WARRANTY terms do not replace their license.

`scripts/publish.ps1` produces `artifacts/smartctl-7.5-source.zip` with the matching source archives, exact database source, build recipe, provenance and notices. When publishing or redistributing this EXE, supply that source package alongside it with equivalent access. A link to upstream's latest version is not a substitute for the matching source package. The source ZIP is not needed to run the app; `artifacts/portable` still contains one executable.
