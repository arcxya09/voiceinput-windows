param(
    [string]$Repository = $env:GH_REPO,
    [string]$Commit = $env:GITHUB_SHA,
    [string]$Version = $env:RELEASE_VERSION,
    [string]$ArtifactDirectory = 'artifacts',
    [string]$Notes,
    [switch]$FunctionsOnly
)
$ErrorActionPreference = 'Stop'

function Invoke-GitHubRead([string]$Endpoint, [switch]$AllowMissing) {
    # A failed request is not evidence that a release does not exist. Only HTTP 404
    # permits creation; transient failures are retried as reads, without mutations.
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        try {
            $response = Invoke-WebRequest -Uri "https://api.github.com/repos/$Repository/$Endpoint" -Headers @{
                Authorization = "Bearer $env:GH_TOKEN"
                Accept = 'application/vnd.github+json'
                'X-GitHub-Api-Version' = '2022-11-28'
            } -SkipHttpErrorCheck -TimeoutSec 30
            if ($response.StatusCode -eq 200) { return ($response.Content | ConvertFrom-Json) }
            if ($response.StatusCode -eq 404 -and $AllowMissing) { return $null }
            if ($response.StatusCode -in @(401, 403, 404, 422)) { throw "GitHub read rejected: HTTP $($response.StatusCode) ($Endpoint)" }
        }
        catch {
            if ($_.Exception.Message -like 'GitHub read rejected:*') { throw }
        }
        if ($attempt -lt 4) { Start-Sleep -Seconds 3 }
    }
    throw "Unable to establish GitHub state after repeated reads ($Endpoint); no further publication attempted."
}

function Invoke-GitHubCommand([string[]]$Arguments) {
    # stderr may describe a completed request whose response was lost. Callers
    # always read the resulting server state before deciding whether it failed.
    $PSNativeCommandUseErrorActionPreference = $false
    try {
        & gh @Arguments 2>&1 | ForEach-Object { Write-Host $_ }
        return $LASTEXITCODE
    }
    catch { Write-Warning 'GitHub command response was not established; checking server state.'; return 1 }
}

function Read-Release([string]$Tag, [switch]$AllowMissing) {
    # GitHub's by-tag REST endpoint returns published releases only. A draft may
    # already exist while both that endpoint and the git tag ref return 404.
    # Enumerate the drafts visible to this write-authorized token before deciding
    # that creation is safe, then read the matching release by its immutable ID.
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        $published = Invoke-GitHubRead "releases/tags/$Tag" -AllowMissing
        if ($null -ne $published) { return $published }
        $matchingReleases = @()
        $complete = $false
        for ($page = 1; $page -le 20; $page++) {
            $releases = @(Invoke-GitHubRead "releases?per_page=100&page=$page")
            $matchingReleases += @($releases | Where-Object { $_.tag_name -ceq $Tag })
            if ($matchingReleases.Count -gt 1) { throw 'Multiple releases match the intended tag; none were changed.' }
            if ($releases.Count -lt 100) { $complete = $true; break }
        }
        if (!$complete) { throw 'Release listing exceeded the inspection limit; absence could not be established.' }
        if ($matchingReleases.Count -eq 1) {
            $id = $matchingReleases[0].id
            if ("$id" -notmatch '^[1-9][0-9]*$') { throw 'The matching release has no valid immutable ID.' }
            $release = Invoke-GitHubRead "releases/$id"
            if ($release.id -ne $id -or $release.tag_name -cne $Tag) { throw 'Release identity changed between listing and read-back.' }
            return $release
        }
        if ($AllowMissing) { return $null }
        # Creation may have completed even if its response was lost. Only repeat
        # discovery reads while waiting for the draft; never create it again here.
        if ($attempt -lt 4) { Start-Sleep -Seconds 3 }
    }
    throw 'The expected release was not visible after repeated published/draft discovery; no duplicate was created.'
}

function Get-TagCommit([string]$Tag, [switch]$AllowMissing) {
    $reference = Invoke-GitHubRead "git/ref/tags/$Tag" -AllowMissing:$AllowMissing
    if ($null -eq $reference) { return $null }
    $object = $reference.object
    for ($depth = 0; $depth -lt 5 -and $object.type -eq 'tag'; $depth++) {
        $object = (Invoke-GitHubRead "git/tags/$($object.sha)").object
    }
    if ($object.type -ne 'commit') { throw 'Release tag does not resolve to a commit.' }
    return $object.sha
}

function Assert-ReleaseAssets($Release, [string[]]$Expected) {
    $assets = @($Release.assets)
    if ($assets.Count -ne $Expected.Count -or
        @($assets | Where-Object { $_.name -cnotin $Expected -or $_.size -le 0 -or $_.state -ne 'uploaded' }).Count -ne 0 -or
        @($assets.name | Sort-Object -Unique).Count -ne $Expected.Count) {
        throw 'The remote release asset set is incomplete or contains unexpected files.'
    }
}

function Assert-ArchiveCommit([string]$Path, [string]$ExpectedCommit) {
    # git archive writes the source commit in the ZIP end-of-directory comment.
    # Inspect it without extracting or trusting paths supplied by an archive.
    $bytes = [IO.File]::ReadAllBytes($Path)
    for ($i = $bytes.Length - 22; $i -ge [Math]::Max(0, $bytes.Length - 65557); $i--) {
        if ([BitConverter]::ToUInt32($bytes, $i) -ne 0x06054b50) { continue }
        $length = [BitConverter]::ToUInt16($bytes, $i + 20)
        if ($i + 22 + $length -ne $bytes.Length) { continue }
        $archiveCommit = [Text.Encoding]::UTF8.GetString($bytes, $i + 22, $length)
        if ($archiveCommit -cne $ExpectedCommit) { throw 'The source ZIP does not identify the release commit.' }
        return
    }
    throw 'The source ZIP has no valid git archive commit comment.'
}

function Assert-DownloadedAssets([string]$Directory, [string[]]$Expected, [string]$SourceCommit, [string]$LocalDirectory = '') {
    $files = @(Get-ChildItem $Directory -File)
    if ($files.Count -ne $Expected.Count -or @($files | Where-Object { $_.Name -cnotin $Expected -or $_.Length -eq 0 }).Count) {
        throw 'Downloaded release assets are incomplete.'
    }
    $hashNames = @($Expected | Where-Object { $_ -ne 'SHA256SUMS.txt' })
    $found = @{}
    foreach ($line in Get-Content (Join-Path $Directory 'SHA256SUMS.txt')) {
        if ($line -notmatch '^([a-fA-F0-9]{64})  (.+)$' -or $Matches[2] -cnotin $hashNames -or $found.ContainsKey($Matches[2])) {
            throw 'Invalid or duplicate entry in SHA256SUMS.txt.'
        }
        $name = $Matches[2]; $expectedHash = $Matches[1]
        if ((Get-FileHash (Join-Path $Directory $name) -Algorithm SHA256).Hash -ne $expectedHash) { throw "Published checksum mismatch: $name" }
        $found[$name] = $true
    }
    if ($found.Count -ne $hashNames.Count) { throw 'SHA256SUMS.txt does not cover every release payload.' }
    Assert-ArchiveCommit (Join-Path $Directory "VoiceInput-Source-$Version.zip") $SourceCommit
    if ($LocalDirectory) {
        foreach ($name in $Expected) {
            if ((Get-FileHash (Join-Path $Directory $name) -Algorithm SHA256).Hash -ne
                (Get-FileHash (Join-Path $LocalDirectory $name) -Algorithm SHA256).Hash) { throw "Uploaded asset differs from the tested build: $name" }
        }
    }
}

function Verify-RemoteRelease($Release, [string[]]$Expected, [string]$SourceCommit, [string]$LocalDirectory = '') {
    Assert-ReleaseAssets $Release $Expected
    $directory = Join-Path ([IO.Path]::GetTempPath()) ("voiceinput-release-verify-" + [Guid]::NewGuid().ToString('N'))
    try {
        for ($attempt = 0; $attempt -lt 3; $attempt++) {
            $code = Invoke-GitHubCommand @('release', 'download', $Release.tag_name, '--repo', $Repository, '--dir', $directory, '--clobber')
            if ($code -eq 0) { break }
            if ($attempt -lt 2) { Start-Sleep -Seconds 3 }
        }
        if ($code -ne 0) { throw 'Unable to download release assets for verification.' }
        Assert-DownloadedAssets $directory $Expected $SourceCommit $LocalDirectory
    }
    finally { if (Test-Path $directory) { Remove-Item $directory -Recurse -Force } }
}

function Wait-PublishedRelease([string]$Tag, [string]$ExpectedCommit, [string[]]$Expected, [string]$LocalDirectory) {
    # Publishing is performed once. An ambiguous mutation response causes only
    # safe read-back; it must never trigger a second release or replace public files.
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        $release = Read-Release $Tag
        if ($release.tag_name -cne $Tag -or $release.target_commitish -cne $ExpectedCommit) { throw 'Published release metadata does not match the intended commit.' }
        if (!$release.draft) {
            # Publication and git-ref visibility may become observable at different
            # times. A confirmed HTTP 404 may consume this bounded read-back loop;
            # authentication errors and an actual mismatched commit still fail.
            $tagCommit = Get-TagCommit $Tag -AllowMissing
            if ($null -ne $tagCommit) {
                if ($tagCommit -cne $ExpectedCommit) { throw 'The published tag points to a different commit.' }
                Verify-RemoteRelease $release $Expected $ExpectedCommit $LocalDirectory
                Write-Host "Verified published $Tag, commit $ExpectedCommit, and all four release assets."
                return
            }
        }
        if ($attempt -lt 4) { Start-Sleep -Seconds 3 }
    }
    throw 'The public release and matching git tag were not both visible after bounded read-back. No publication mutation was repeated.'
}

function Publish-VoiceInputRelease {
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or $Commit -notmatch '^[a-f0-9]{40}$' -or $Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release repository, commit, or version.' }
    if (!(Test-Path $Notes)) { throw "Missing release notes: $Notes" }
    if ((Invoke-GitHubRead 'git/ref/heads/main').object.sha -cne $Commit) {
        Write-Host 'A newer commit is queued; skipping publication of this superseded build.'
        return
    }
    $tag = "v$Version"
    $expected = @("VoiceInput-Windows-x64-$Version.zip", "VoiceInput-Source-$Version.zip", 'build-output.log', 'SHA256SUMS.txt')
    Assert-DownloadedAssets $ArtifactDirectory $expected $Commit
    $release = Read-Release $tag -AllowMissing
    if ($null -eq $release) {
        $existingTag = Get-TagCommit $tag -AllowMissing
        if ($existingTag -and $existingTag -cne $Commit) { throw 'The existing version tag belongs to a different commit; choose a new version.' }
        $null = Invoke-GitHubCommand @('release', 'create', $tag, '--repo', $Repository, '--draft', '--target', $Commit, '--title', "VoiceInput $Version", '--notes-file', $Notes)
        $release = Read-Release $tag
    }
    if ($release.tag_name -cne $tag) { throw 'GitHub returned a different release tag.' }
    if (!$release.draft) {
        $tagCommit = Get-TagCommit $tag
        if ($release.target_commitish -cne $tagCommit) { throw 'Published release metadata and tag commit differ.' }
        if ($tagCommit -cne $Commit) {
            $comparison = Invoke-GitHubRead "compare/$tagCommit...$Commit"
            # A documentation or publication-only change may keep an existing version.
            # Application/test/build changes must receive a new version before release.
            if ($comparison.status -notin @('ahead', 'identical') -or $comparison.total_commits -gt 20 -or
                @($comparison.files).Count -ge 300 -or
                @($comparison.files | Where-Object { $_.filename -notmatch '^(README\.md$|docs/|\.github/workflows/|scripts/(PublishRelease|TestReleasePublication)\.ps1$)' }).Count) {
                throw 'Application code changed without a new version; existing public assets were preserved.'
            }
        }
        Verify-RemoteRelease $release $expected $tagCommit
        Write-Host "Verified existing published $tag; its assets remain unchanged."
        return
    }
    $existingTag = Get-TagCommit $tag -AllowMissing
    if ($existingTag -and $existingTag -cne $Commit) { throw 'The draft tag points to different source; publication stopped.' }
    if ($release.target_commitish -cne $Commit) {
        if ($release.target_commitish -notmatch '^[a-f0-9]{40}$') { throw 'The existing draft does not identify an exact source commit.' }
        $comparison = Invoke-GitHubRead "compare/$($release.target_commitish)...$Commit"
        if ($comparison.status -notin @('ahead', 'identical') -or $comparison.total_commits -gt 20 -or @($comparison.files).Count -ge 300 -or
            @($comparison.files | Where-Object { $_.filename -notmatch '^(\.github/workflows/release\.yml|scripts/(PublishRelease|TestReleasePublication)\.ps1)$' }).Count) {
            throw 'The existing draft belongs to different application code; it was preserved for review.'
        }
        # Only a publication-script repair may advance a draft, and no existing
        # tag is rewritten. Check the result even when the edit response was lost.
        $null = Invoke-GitHubCommand @('release', 'edit', $tag, '--repo', $Repository, '--draft', '--target', $Commit, '--notes-file', $Notes)
        $release = Read-Release $tag
        if (!$release.draft -or $release.target_commitish -cne $Commit) { throw 'Unable to verify the repaired draft target.' }
    }
    if (@($release.assets | Where-Object { $_.name -cnotin $expected }).Count) { throw 'The existing draft contains unexpected assets.' }
    $paths = @($expected | ForEach-Object { (Resolve-Path (Join-Path $ArtifactDirectory $_)).Path })
    for ($attempt = 0; $attempt -lt 3; $attempt++) {
        $release = Read-Release $tag
        if (!$release.draft) { Wait-PublishedRelease $tag $Commit $expected $ArtifactDirectory; return }
        if ($release.target_commitish -cne $Commit) { throw 'Draft source changed during upload.' }
        $code = Invoke-GitHubCommand (@('release', 'upload', $tag, '--repo', $Repository, '--clobber') + $paths)
        if ($code -eq 0) { break }
        if ($attempt -lt 2) { Start-Sleep -Seconds 3 }
    }
    # Even an upload error can mean the assets arrived; verify their actual contents.
    $release = Read-Release $tag
    Verify-RemoteRelease $release $expected $Commit $ArtifactDirectory
    if (!$release.draft) { Wait-PublishedRelease $tag $Commit $expected $ArtifactDirectory; return }
    $null = Invoke-GitHubCommand @('release', 'edit', $tag, '--repo', $Repository, '--draft=false', '--latest')
    Wait-PublishedRelease $tag $Commit $expected $ArtifactDirectory
}

if (!$FunctionsOnly) {
    if (!$Notes) { $Notes = "docs/releases/$Version.md" }
    Publish-VoiceInputRelease
}

