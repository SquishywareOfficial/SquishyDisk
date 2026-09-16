param([string]$Toolchain = (Join-Path $PSScriptRoot '..\.tools\msvc'))
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$source = Join-Path $repo '.tools\diskspd-source'
$revision = '5e7025bfc9d1364f185d4c30963d7ac79195435e'
if (!(Test-Path $source)) {
    git clone --depth 1 --branch v2.3 https://github.com/microsoft/diskspd.git $source
    if ($LASTEXITCODE -ne 0) { throw 'DiskSpd source download failed.' }
}
if ((git -C $source rev-parse HEAD).Trim() -ne $revision) { throw 'Unexpected DiskSpd source revision.' }
$patch = Join-Path $repo 'vendor\diskspd\integration.patch'
$diff = (git -C $source diff --binary) -join "`n"
if ($diff.Length -eq 0) {
    git -C $source apply --ignore-space-change $patch
    if ($LASTEXITCODE -ne 0) { throw 'DiskSpd integration patch failed.' }
} elseif ($diff.TrimEnd() -ne (Get-Content $patch -Raw).Replace("`r", '').TrimEnd()) {
    throw 'The DiskSpd source has changes beyond the documented integration patch.'
}
$toolchainPath = [IO.Path]::GetFullPath($Toolchain)
$compiler = Get-ChildItem (Join-Path $toolchainPath 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdk = Get-ChildItem (Join-Path $toolchainPath 'Windows Kits\10\Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$compilerBin = Join-Path $compiler.FullName 'bin\Hostx64\x64'
$sdkBin = Join-Path $toolchainPath "Windows Kits\10\bin\$($sdk.Name)\x64"
$savedPath = $env:PATH
$savedInclude = $env:INCLUDE
$savedLib = $env:LIB
$build = Join-Path $repo '.tools\engine-build'
New-Item -ItemType Directory -Force $build | Out-Null
try {
    $env:PATH = "$compilerBin;$sdkBin;$savedPath"
    $env:INCLUDE = @((Join-Path $compiler.FullName 'include'), (Join-Path $compiler.FullName 'atlmfc\include'), (Join-Path $sdk.FullName 'ucrt'), (Join-Path $sdk.FullName 'shared'), (Join-Path $sdk.FullName 'um'), (Join-Path $sdk.FullName 'winrt')) -join ';'
    $env:LIB = @((Join-Path $compiler.FullName 'lib\x64'), (Join-Path $compiler.FullName 'atlmfc\lib\x64'), (Join-Path $toolchainPath "Windows Kits\10\Lib\$($sdk.Name)\ucrt\x64"), (Join-Path $toolchainPath "Windows Kits\10\Lib\$($sdk.Name)\um\x64")) -join ';'
    $folders = 'CmdLineParser','Common','IORequestGenerator','ResultParser','XmlResultParser','CmdRequestCreator','XmlProfileParser'
    foreach ($folder in $folders) {
        foreach ($file in Get-ChildItem (Join-Path $source $folder) -Filter '*.cpp') {
            $object = Join-Path $build ($file.BaseName + '.obj')
            $arguments = @('/nologo','/c','/std:c++17','/O2','/MT','/EHsc','/GR-','/Gy','/Gw','/GF','/Brepro','/DWIN32','/DNDEBUG','/D_CONSOLE','/D_SILENCE_ALL_CXX17_DEPRECATION_WARNINGS',"/I$source\Common", "/Fo$object", $file.FullName)
            if ($folder -eq 'XmlProfileParser') { $arguments += '/DUNICODE','/D_UNICODE' }
            & (Join-Path $compilerBin 'cl.exe') @arguments
            if ($LASTEXITCODE -ne 0) { throw "Native compile failed: $file" }
        }
    }
    Push-Location (Join-Path $source 'CmdRequestCreator')
    try {
        & (Join-Path $sdkBin 'rc.exe') /nologo "/I$source\Common" "/fo$build\diskspd.res" 'diskspd.rc'
        if ($LASTEXITCODE -ne 0) { throw 'Native resource compilation failed.' }
    } finally { Pop-Location }
    $objects = Get-ChildItem $build -Filter '*.obj' | Select-Object -ExpandProperty FullName
    & (Join-Path $compilerBin 'link.exe') /nologo /SUBSYSTEM:CONSOLE /MACHINE:X64 /OPT:REF /OPT:ICF /Brepro "/OUT:$build\diskspd.exe" @objects "$build\diskspd.res" powrprof.lib onecore.lib msxml6.lib advapi32.lib ole32.lib oleaut32.lib uuid.lib
    if ($LASTEXITCODE -ne 0) { throw 'Native linking failed.' }
    & (Join-Path $sdkBin 'mt.exe') -nologo -manifest (Join-Path $repo 'vendor\diskspd\engine.manifest') "-outputresource:$build\diskspd.exe;#1"
    if ($LASTEXITCODE -ne 0) { throw 'Embedding the engine UTF-8 manifest failed.' }
    $vendor = Join-Path $repo 'vendor\diskspd'
    Copy-Item (Join-Path $build 'diskspd.exe') (Join-Path $vendor 'diskspd.exe') -Force
    Copy-Item (Join-Path $source 'LICENSE') (Join-Path $vendor 'LICENSE.txt') -Force
    (Get-FileHash (Join-Path $vendor 'diskspd.exe') -Algorithm SHA256).Hash.ToLowerInvariant() | Set-Content (Join-Path $vendor 'engine.sha256')
    Write-Host 'Built DiskSpd 2.3 from MIT source with notification/XML fixes, a UTF-8 manifest, and the static C++ runtime.'
} finally {
    $env:PATH = $savedPath; $env:INCLUDE = $savedInclude; $env:LIB = $savedLib
}
