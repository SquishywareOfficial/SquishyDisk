param([switch]$SkipTests, [ValidateRange(0, 65534)][int]$BuildNumber = 0)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$sdkVersion = '10.0.401'
$dotnet = Join-Path $repo '.tools\dotnet\dotnet.exe'
if (!(Test-Path $dotnet)) {
    $installed = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($installed -and ((& $installed.Source --list-sdks) -match '^10\.0\.401 ')) { $dotnet = $installed.Source }
    else {
        $tools = Join-Path $repo '.tools'
        New-Item -ItemType Directory -Force $tools | Out-Null
        $zip = Join-Path $tools 'dotnet-sdk.zip'
        $sha = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'
        if (!(Test-Path $zip) -or (Get-FileHash $zip -Algorithm SHA512).Hash -ne $sha) {
            Invoke-WebRequest "https://builds.dotnet.microsoft.com/dotnet/Sdk/$sdkVersion/dotnet-sdk-$sdkVersion-win-x64.zip" -OutFile $zip
        }
        if ((Get-FileHash $zip -Algorithm SHA512).Hash -ne $sha) { throw 'The .NET SDK checksum does not match.' }
        Expand-Archive -LiteralPath $zip -DestinationPath (Join-Path $tools 'dotnet') -Force
    }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
Push-Location $repo
try {
    $engine = Join-Path $repo 'vendor\diskspd\diskspd.exe'
    if ((Get-FileHash $engine -Algorithm SHA256).Hash -ne (Get-Content 'vendor\diskspd\engine.sha256' -Raw).Trim()) { throw 'The vendored engine checksum does not match.' }
    foreach ($entry in Get-Content 'vendor\smartctl\SHA256SUMS') {
        $parts = $entry -split '  ', 2
        if ($parts.Count -ne 2 -or (Get-FileHash (Join-Path 'vendor\smartctl' $parts[1]) -Algorithm SHA256).Hash -ne $parts[0]) { throw "The vendored smartctl checksum does not match: $entry" }
    }
    & $dotnet build SquishyDisk.slnx -c Release --nologo "-p:BuildNumber=$BuildNumber"
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
    if (!$SkipTests) {
        & $dotnet 'tests\SquishyDisk.Tests\bin\Release\net10.0-windows\SquishyDisk.Tests.dll'
        if ($LASTEXITCODE -ne 0) { throw 'Verification failed.' }
    }
    $output = Join-Path $repo 'artifacts\portable'
    & $dotnet publish src/SquishyDisk.App -c Release -r win-x64 --self-contained true -o $output --nologo "-p:BuildNumber=$BuildNumber"
    if ($LASTEXITCODE -ne 0) { throw 'Publishing failed.' }
    $files = @(Get-ChildItem $output -File)
    if ($files.Count -ne 1 -or $files[0].Name -ne 'SquishyDisk.exe') { throw 'The publish folder must contain only SquishyDisk.exe. Move any saved settings or exports out before publishing.' }
    $baseVersion = ([xml](Get-Content (Join-Path $repo 'Directory.Build.props') -Raw)).Project.PropertyGroup.BaseVersion
    $version = "$baseVersion.$BuildNumber"
    if ($files[0].VersionInfo.FileVersion -ne $version -or $files[0].VersionInfo.ProductVersion -ne $version) { throw 'Published executable version does not match the requested build.' }
    @{ Version = $version; BuildNumber = $BuildNumber; Commit = $env:GITHUB_SHA } | ConvertTo-Json | Set-Content (Join-Path $repo 'artifacts\release.json') -Encoding utf8
    $hash = (Get-FileHash $files[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  SquishyDisk.exe" | Set-Content (Join-Path $repo 'artifacts\SquishyDisk.sha256')
    $sourceFiles = @(Get-ChildItem (Join-Path $repo 'vendor\smartctl') -File | Where-Object Name -ne 'smartctl.exe' | Select-Object -ExpandProperty FullName)
    $sourceZip = Join-Path $repo 'artifacts\smartctl-7.5-source.zip'
    Compress-Archive -LiteralPath $sourceFiles -DestinationPath $sourceZip -Force
    $sourceHash = (Get-FileHash $sourceZip -Algorithm SHA256).Hash.ToLowerInvariant()
    "$sourceHash  smartctl-7.5-source.zip" | Set-Content (Join-Path $repo 'artifacts\smartctl-7.5-source.sha256')
    Write-Host "SquishyDisk $version - Portable EXE: $($files[0].FullName) ($([Math]::Round($files[0].Length / 1MB, 1)) MiB)"
    Write-Host "Matching smartctl source: $sourceZip (publish alongside the EXE)"
} finally { Pop-Location }
