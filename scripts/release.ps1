# Publish only validated artifacts from the current main-branch workflow run.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REF -ne 'refs/heads/main') { throw 'Releases must run through the main-branch GitHub workflow.' }
$repo = $env:GITHUB_REPOSITORY
$commit = $env:GITHUB_SHA
if ($repo -ne 'SquishywareOfficial/SquishyDisk' -or $commit -notmatch '^[0-9a-f]{40}$') { throw 'Unexpected repository or commit.' }
$root = Split-Path $PSScriptRoot -Parent
$metadata = Get-Content (Join-Path $root 'artifacts/release.json') -Raw | ConvertFrom-Json
$version = $metadata.Version
if ($version -notmatch '^\d+\.\d+\.\d+\.\d+$' -or $metadata.BuildNumber -ne [int]$env:GITHUB_RUN_NUMBER -or $metadata.Commit -ne $commit) { throw 'Build metadata does not match this workflow run.' }
$tag = "v$version"
$files = @('artifacts/portable/SquishyDisk.exe', 'artifacts/SquishyDisk.sha256', 'artifacts/smartctl-7.5-source.zip', 'artifacts/smartctl-7.5-source.sha256') | ForEach-Object { Join-Path $root $_ }
foreach ($file in $files) { if (!(Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing release asset: $file" } }
foreach ($pair in @(@($files[0], $files[1]), @($files[2], $files[3]))) {
    $expected = ((Get-Content $pair[1] -Raw).Trim() -split '\s+')[0]
    if ((Get-FileHash $pair[0] -Algorithm SHA256).Hash -ne $expected) { throw "Release checksum mismatch: $($pair[0])" }
}
function Invoke-Gh([string[]]$Arguments) {
    $result = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub command failed: $($Arguments[0])" }
    return $result
}

$existingJson = & gh release view $tag --repo $repo --json isDraft,assets,targetCommitish 2>$null
$exists = $LASTEXITCODE -eq 0
if ($exists) {
    $existing = $existingJson | ConvertFrom-Json
    if ($existing.isDraft) {
        if ($existing.targetCommitish -ne $commit) { throw 'Existing draft targets a different commit.' }
    } else {
        $releasedCommit = Invoke-Gh @('api', "repos/$repo/commits/$tag", '--jq', '.sha')
        if ($releasedCommit -ne $commit) { throw 'Existing release targets a different commit.' }
        foreach ($file in $files) {
            if (!($existing.assets | Where-Object name -eq ([IO.Path]::GetFileName($file)))) { throw 'Published release is missing assets; inspect it before retrying.' }
        }
        Write-Host "Release $tag is already complete; leaving it unchanged."
        exit 0
    }
}

# A retry must never move an existing tag to a different commit.
$tagCommit = & git rev-parse -q --verify "refs/tags/$tag^{commit}" 2>$null
if ($LASTEXITCODE -eq 0 -and $tagCommit -ne $commit) { throw 'Existing tag targets a different commit.' }
if (!$exists) {
    $notes = @"
SquishyDisk $version

Portable Windows x64 build from commit $commit.
Download SquishyDisk.exe to run the app; no installer is required.
The matching smartctl source package and SHA-256 checksums are included.

Build: https://github.com/$repo/actions/runs/$env:GITHUB_RUN_ID
"@
    $notesPath = Join-Path $root 'artifacts/release-notes.md'
    [IO.File]::WriteAllText($notesPath, $notes, [Text.UTF8Encoding]::new($false))
    Invoke-Gh @('release', 'create', $tag, '--repo', $repo, '--target', $commit, '--title', "SquishyDisk $version", '--draft', '--notes-file', $notesPath)
}
Invoke-Gh (@('release', 'upload', $tag, '--repo', $repo, '--clobber') + $files)
# Publishing only happens after all four uploads succeed; reruns can resume a draft.
$head = Invoke-Gh @('api', "repos/$repo/git/ref/heads/main", '--jq', '.object.sha')
$latest = if ($head -eq $commit) { '--latest=true' } else { '--latest=false' }
Invoke-Gh @('release', 'edit', $tag, '--repo', $repo, '--draft=false', '--prerelease=false', $latest)
"Published [$tag](https://github.com/$repo/releases/tag/$tag)." | Add-Content $env:GITHUB_STEP_SUMMARY
