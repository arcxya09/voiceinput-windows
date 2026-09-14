$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/PublishRelease.ps1" -Repository 'test/repo' -Commit ('a' * 40) -Version '2.1.0' -Notes $PSCommandPath -FunctionsOnly
$originals = @{}
foreach ($name in @('Invoke-GitHubRead', 'Invoke-GitHubCommand', 'Read-Release', 'Get-TagCommit', 'Verify-RemoteRelease', 'Assert-DownloadedAssets')) {
    $originals[$name] = (Get-Command $name).ScriptBlock
}
$passed = 0
$fixtureDirectory = Join-Path ([IO.Path]::GetTempPath()) ('release-fixture-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item $fixtureDirectory -ItemType Directory
$ArtifactDirectory = $fixtureDirectory
foreach ($fixtureName in @('VoiceInput-Setup-2.1.0.exe', 'VoiceInput-Portable-x64-2.1.0.zip', 'VoiceInput-Source-2.1.0.zip', 'build-output.log', 'SHA256SUMS.txt')) {
    [IO.File]::WriteAllText((Join-Path $fixtureDirectory $fixtureName), 'offline fixture')
}
function Assert($Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Start-Sleep { param($Seconds) } # All regression retries are immediate and offline.
function Mock([string]$Name, [scriptblock]$Body) { Set-Item "function:script:$Name" $Body }
function Test([string]$Name, [scriptblock]$Body) {
    foreach ($functionName in $originals.Keys) { Set-Item "function:script:$functionName" $originals[$functionName] }
    & $Body
    $script:passed++
    Write-Host "PASS Release: $Name"
}
function Set-PublicationFixture {
    $script:remote = [pscustomobject]@{ tag_name = 'v2.1.0'; target_commitish = ('a' * 40); draft = $true; assets = @() }
    $script:commands = [Collections.Generic.List[string]]::new()
    $script:verified = 0
    Mock 'Invoke-GitHubRead' { param($Endpoint, [switch]$AllowMissing) if ($Endpoint -eq 'git/ref/heads/main') { return @{ object = @{ sha = ('a' * 40) } } }; throw "Unexpected endpoint: $Endpoint" }
    Mock 'Read-Release' { param($Tag, [switch]$AllowMissing) return $script:remote }
    Mock 'Get-TagCommit' { param($Tag, [switch]$AllowMissing) return ('a' * 40) }
    Mock 'Verify-RemoteRelease' { param($Release, $Expected, $SourceCommit, $LocalDirectory) $script:verified++ }
    Mock 'Assert-DownloadedAssets' { param($Directory, $Expected, $SourceCommit, $LocalDirectory) }
}

Test 'HTTP 502 is retried as a read and never classified as missing' {
    $script:reads = 0
    function Invoke-WebRequest { param($Uri, $Headers, [switch]$SkipHttpErrorCheck, $TimeoutSec) $script:reads++; return @{ StatusCode = 502; Content = '' } }
    $failed = $false
    try { $null = Invoke-GitHubRead 'releases/tags/v2.1.0' -AllowMissing } catch { $failed = $true }
    Assert ($failed -and $script:reads -eq 5) 'Transient failures must exhaust safe reads and throw.'
}
Test 'Only an authoritative HTTP 404 permits release creation' {
    function Invoke-WebRequest { param($Uri, $Headers, [switch]$SkipHttpErrorCheck, $TimeoutSec) return @{ StatusCode = 404; Content = '' } }
    Assert ($null -eq (Invoke-GitHubRead 'releases/tags/v2.1.0' -AllowMissing)) '404 was not classified as missing.'
    $failed = $false
    try { $null = Invoke-GitHubRead 'releases/tags/v2.1.0' } catch { $failed = $true }
    Assert $failed 'Unexpected disappearance must not be silently treated as success.'
}
Test 'Publication response failure succeeds after verified public read-back' {
    Set-PublicationFixture
    Mock 'Invoke-GitHubCommand' {
        param($Arguments)
        $script:commands.Add(($Arguments -join ' '))
        if ($Arguments[1] -eq 'edit') { $script:remote.draft = $false; return 1 }
        return 0
    }
    Publish-VoiceInputRelease
    Assert ($script:verified -eq 2) 'Both draft payload and public release must be verified.'
    Assert (@($script:commands | Where-Object { $_ -like 'release edit *' }).Count -eq 1) 'Publication must run once.'
    Assert (@($script:commands | Where-Object { $_ -like 'release create *' }).Count -eq 0) 'An existing release must never be recreated.'
}
Test 'Ambiguous creation is read back without creating a duplicate release' {
    Set-PublicationFixture
    $script:remote = $null
    Mock 'Invoke-GitHubCommand' {
        param($Arguments)
        $script:commands.Add(($Arguments -join ' '))
        if ($Arguments[1] -eq 'create') { $script:remote = [pscustomobject]@{ tag_name = 'v2.1.0'; target_commitish = ('a' * 40); draft = $true; assets = @() }; return 1 }
        if ($Arguments[1] -eq 'edit') { $script:remote.draft = $false }
        return 0
    }
    Publish-VoiceInputRelease
    Assert (@($script:commands | Where-Object { $_ -like 'release create *' }).Count -eq 1) 'Creation must not be repeated after an ambiguous result.'
}
Test 'An existing public release is verified without mutating its assets' {
    Set-PublicationFixture; $script:remote.draft = $false
    Mock 'Invoke-GitHubCommand' { param($Arguments) throw 'Unexpected publication mutation' }
    Publish-VoiceInputRelease
    Assert ($script:verified -eq 1) 'Existing public assets must be verified.'
}
Test 'A mismatched published tag is rejected before success' {
    Set-PublicationFixture; $script:remote.draft = $false
    $script:tagReads = 0
    Mock 'Get-TagCommit' { param($Tag, [switch]$AllowMissing) $script:tagReads++; return ('b' * 40) }
    $failed = $false
    try { Wait-PublishedRelease 'v2.1.0' ('a' * 40) @() '' } catch { $failed = $true }
    Assert ($failed -and $script:tagReads -eq 1 -and $script:verified -eq 0) 'An unrelated tag must fail immediately.'
}
Test 'A published release waits for its git ref without repeating publication' {
    Set-PublicationFixture; $script:remote.draft = $false
    Mock 'Get-TagCommit' $originals['Get-TagCommit']
    $script:tagReads = 0
    Mock 'Invoke-GitHubRead' {
        param($Endpoint, [switch]$AllowMissing)
        Assert ($Endpoint -eq 'git/ref/tags/v2.1.0' -and $AllowMissing) 'Only tag-ref absence may wait after publication.'
        $script:tagReads++
        if ($script:tagReads -lt 3) { return $null }
        return @{ object = @{ type = 'commit'; sha = ('a' * 40) } }
    }
    Mock 'Invoke-GitHubCommand' { param($Arguments) throw 'Post-publication visibility may only trigger reads.' }
    Wait-PublishedRelease 'v2.1.0' ('a' * 40) @() ''
    Assert ($script:tagReads -eq 3 -and $script:verified -eq 1) 'A temporarily invisible tag must be checked again and verified once.'
}
Test 'A persistently missing published git ref exhausts bounded reads and fails' {
    Set-PublicationFixture; $script:remote.draft = $false
    Mock 'Get-TagCommit' $originals['Get-TagCommit']
    $script:tagReads = 0
    Mock 'Invoke-GitHubRead' {
        param($Endpoint, [switch]$AllowMissing)
        Assert ($Endpoint -eq 'git/ref/tags/v2.1.0' -and $AllowMissing) 'Only explicit tag-ref absence may return null.'
        $script:tagReads++; return $null
    }
    Mock 'Invoke-GitHubCommand' { param($Arguments) throw 'A missing published tag must never cause a mutation.' }
    $failed = $false
    try { Wait-PublishedRelease 'v2.1.0' ('a' * 40) @() '' } catch { $failed = $true }
    Assert ($failed -and $script:tagReads -eq 5 -and $script:verified -eq 0) 'Missing tag must fail after exactly five confirmation reads.'
}
Test 'Post-publication tag authentication failures are not treated as visibility delays' {
    Set-PublicationFixture; $script:remote.draft = $false
    Mock 'Get-TagCommit' $originals['Get-TagCommit']
    $script:tagReads = 0
    Mock 'Invoke-GitHubRead' {
        param($Endpoint, [switch]$AllowMissing)
        $script:tagReads++
        throw 'GitHub read rejected: HTTP 403 (git/ref/tags/v2.1.0)'
    }
    Mock 'Invoke-GitHubCommand' { param($Arguments) throw 'Authentication failure must not cause a mutation.' }
    $failed = $false
    try { Wait-PublishedRelease 'v2.1.0' ('a' * 40) @() '' } catch { $failed = $_.Exception.Message -like 'GitHub read rejected: HTTP 403*' }
    Assert ($failed -and $script:tagReads -eq 1 -and $script:verified -eq 0) 'Authentication rejection must propagate immediately.'
}
Test 'A publication that remains a draft is never retried as a mutation' {
    Set-PublicationFixture
    Mock 'Invoke-GitHubCommand' { param($Arguments) $script:commands.Add(($Arguments -join ' ')); return 1 }
    $failed = $false
    try { Publish-VoiceInputRelease } catch { $failed = $true }
    Assert $failed 'A remaining draft must fail the publication gate.'
    Assert (@($script:commands | Where-Object { $_ -like 'release edit *' }).Count -eq 1) 'Uncertain publication may only be followed by reads.'
}
Test 'Archive commit comment is validated against intended source' {
    $path = Join-Path ([IO.Path]::GetTempPath()) ([Guid]::NewGuid().ToString('N') + '.zip')
    try {
        $bytes = [byte[]]::new(62)
        [BitConverter]::GetBytes([uint32]0x06054b50).CopyTo($bytes, 0)
        [BitConverter]::GetBytes([uint16]40).CopyTo($bytes, 20)
        [Text.Encoding]::UTF8.GetBytes('a' * 40).CopyTo($bytes, 22)
        [IO.File]::WriteAllBytes($path, $bytes)
        Assert-ArchiveCommit $path ('a' * 40)
        $failed = $false; try { Assert-ArchiveCommit $path ('b' * 40) } catch { $failed = $true }
        Assert $failed 'A source ZIP with a different commit must be rejected.'
    }
    finally { Remove-Item $path -Force -ErrorAction SilentlyContinue }
}
Test 'Published checksum manifest covers exact payloads and rejects traversal' {
    $directory = Join-Path ([IO.Path]::GetTempPath()) ('release-hashes-' + [Guid]::NewGuid().ToString('N'))
    try {
        $null = New-Item $directory -ItemType Directory
        $names = @('VoiceInput-Setup-2.1.0.exe', 'VoiceInput-Portable-x64-2.1.0.zip', 'VoiceInput-Source-2.1.0.zip', 'build-output.log', 'SHA256SUMS.txt')
        foreach ($name in $names) { [IO.File]::WriteAllText((Join-Path $directory $name), 'payload') }
        [IO.File]::WriteAllText((Join-Path $directory 'SHA256SUMS.txt'), (('a' * 64) + '  ../outside'))
        $failed = $false; try { Assert-DownloadedAssets $directory $names ('a' * 40) } catch { $failed = $true }
        Assert $failed 'A manifest entry outside the release payloads must be rejected.'
    }
    finally { Remove-Item $directory -Recurse -Force -ErrorAction SilentlyContinue }
}
Test 'A draft without a tag ref is recovered through production listing and ID reads' {
    Set-PublicationFixture
    # Keep the actual Read-Release and Get-TagCommit implementations. These REST
    # responses model GitHub: unpublished drafts do not appear at releases/tags,
    # and their git refs need not exist until publication.
    Mock 'Read-Release' $originals['Read-Release']
    Mock 'Get-TagCommit' $originals['Get-TagCommit']
    $script:remote = [pscustomobject]@{ id = 123; tag_name = 'v2.1.0'; target_commitish = ('b' * 40); draft = $true; assets = @() }
    $script:listReads = 0; $script:idReads = 0; $script:tagCreated = $false
    Mock 'Invoke-GitHubRead' {
        param($Endpoint, [switch]$AllowMissing)
        switch ($Endpoint) {
            'git/ref/heads/main' { return @{ object = @{ sha = ('a' * 40) } } }
            'git/ref/tags/v2.1.0' {
                if ($script:tagCreated) { return @{ object = @{ type = 'commit'; sha = ('a' * 40) } } }
                Assert $AllowMissing 'The unpublished draft must not require an existing git ref.'
                return $null
            }
            'releases/tags/v2.1.0' {
                if (!$script:remote.draft) { return $script:remote }
                Assert $AllowMissing 'The by-tag endpoint must allow a draft-only HTTP 404.'
                return $null
            }
            'releases?per_page=100&page=1' { $script:listReads++; return @($script:remote) }
            'releases/123' { $script:idReads++; return $script:remote }
            "compare/$('b' * 40)...$('a' * 40)" { return @{ status = 'ahead'; total_commits = 1; files = @(@{ filename = 'scripts/PublishRelease.ps1' }) } }
            default { throw "Unexpected endpoint: $Endpoint" }
        }
    }
    Mock 'Invoke-GitHubCommand' {
        param($Arguments)
        $script:commands.Add(($Arguments -join ' '))
        if ($Arguments[1] -eq 'create') { throw 'An existing draft must not be recreated.' }
        if ($Arguments[1] -eq 'edit' -and '--target' -in $Arguments) { $script:remote.target_commitish = ('a' * 40); return 1 }
        if ($Arguments[1] -eq 'edit' -and '--draft=false' -in $Arguments) { $script:remote.draft = $false; $script:tagCreated = $true; return 1 }
        return 0
    }
    Publish-VoiceInputRelease
    Assert ($script:listReads -ge 3 -and $script:idReads -eq $script:listReads) 'Draft discovery must retrieve the exact release ID at each read-back.'
    Assert ($script:verified -eq 2 -and $script:tagCreated) 'Recovered draft and final public payload must both be verified.'
    Assert (@($script:commands | Where-Object { $_ -like 'release create *' }).Count -eq 0) 'Recovery must preserve the existing release identity.'
}
Test 'Production draft discovery scans every list page and reads the matching ID' {
    $script:pages = @()
    Mock 'Invoke-GitHubRead' {
        param($Endpoint, [switch]$AllowMissing)
        if ($Endpoint -eq 'releases/tags/v2.1.0') { return $null }
        if ($Endpoint -eq 'releases?per_page=100&page=1') {
            $script:pages += 1
            return @(1..100 | ForEach-Object { @{ id = $_; tag_name = "older-$_"; draft = $false } })
        }
        if ($Endpoint -eq 'releases?per_page=100&page=2') { $script:pages += 2; return @(@{ id = 456; tag_name = 'v2.1.0'; draft = $true }) }
        if ($Endpoint -eq 'releases/456') { return @{ id = 456; tag_name = 'v2.1.0'; draft = $true; target_commitish = ('a' * 40) } }
        throw "Unexpected endpoint: $Endpoint"
    }
    $draft = Read-Release 'v2.1.0' -AllowMissing
    Assert ($draft.id -eq 456 -and $draft.draft -and ($script:pages -join ',') -eq '1,2') 'A draft beyond the first page must not be mistaken for an absent release.'
}
Test 'A failed draft listing cannot authorize duplicate creation' {
    Set-PublicationFixture
    Mock 'Read-Release' $originals['Read-Release']
    Mock 'Invoke-GitHubRead' {
        param($Endpoint, [switch]$AllowMissing)
        if ($Endpoint -eq 'git/ref/heads/main') { return @{ object = @{ sha = ('a' * 40) } } }
        if ($Endpoint -eq 'releases/tags/v2.1.0') { return $null }
        throw 'Simulated failure while listing visible drafts.'
    }
    Mock 'Invoke-GitHubCommand' { param($Arguments) $script:commands.Add(($Arguments -join ' ')); throw 'No mutation is permitted after an uncertain listing.' }
    $failed = $false; try { Publish-VoiceInputRelease } catch { $failed = $true }
    Assert ($failed -and $script:commands.Count -eq 0) 'By-tag 404 with unknown draft state must stop before mutation.'
}
Test 'Ambiguous creation waits for draft visibility without repeating creation' {
    Set-PublicationFixture
    Mock 'Read-Release' $originals['Read-Release']
    Mock 'Get-TagCommit' $originals['Get-TagCommit']
    $script:remote = $null; $script:visibilityDelay = 0; $script:tagCreated = $false
    Mock 'Invoke-GitHubRead' {
        param($Endpoint, [switch]$AllowMissing)
        if ($Endpoint -eq 'git/ref/heads/main') { return @{ object = @{ sha = ('a' * 40) } } }
        if ($Endpoint -eq 'git/ref/tags/v2.1.0') { if ($script:tagCreated) { return @{ object = @{ type = 'commit'; sha = ('a' * 40) } } }; return $null }
        if ($Endpoint -eq 'releases/tags/v2.1.0') { if ($script:remote -and !$script:remote.draft) { return $script:remote }; return $null }
        if ($Endpoint -eq 'releases?per_page=100&page=1') {
            if ($script:visibilityDelay -gt 0) { $script:visibilityDelay--; return @() }
            if ($script:remote) { return @($script:remote) }; return @()
        }
        if ($Endpoint -eq 'releases/789') { return $script:remote }
        throw "Unexpected endpoint: $Endpoint"
    }
    Mock 'Invoke-GitHubCommand' {
        param($Arguments)
        $script:commands.Add(($Arguments -join ' '))
        if ($Arguments[1] -eq 'create') {
            $script:remote = [pscustomobject]@{ id = 789; tag_name = 'v2.1.0'; target_commitish = ('a' * 40); draft = $true; assets = @() }
            $script:visibilityDelay = 1
            return 1
        }
        if ($Arguments[1] -eq 'edit') { $script:remote.draft = $false; $script:tagCreated = $true }
        return 0
    }
    Publish-VoiceInputRelease
    Assert (@($script:commands | Where-Object { $_ -like 'release create *' }).Count -eq 1) 'Only the initial confirmed-absent state permits creation.'
    Assert ($script:verified -eq 2 -and $script:remote.id -eq 789) 'The same created draft must be verified through publication.'
}
Test 'Duplicate tag matches or a changed release ID are rejected' {
    Mock 'Invoke-GitHubRead' {
        param($Endpoint, [switch]$AllowMissing)
        if ($Endpoint -eq 'releases/tags/v2.1.0') { return $null }
        return @(@{ id = 1; tag_name = 'v2.1.0' }, @{ id = 2; tag_name = 'v2.1.0' })
    }
    $failed = $false; try { $null = Read-Release 'v2.1.0' -AllowMissing } catch { $failed = $true }
    Assert $failed 'Ambiguous release identities must not be guessed.'
    Mock 'Invoke-GitHubRead' {
        param($Endpoint, [switch]$AllowMissing)
        if ($Endpoint -eq 'releases/tags/v2.1.0') { return $null }
        if ($Endpoint -eq 'releases?per_page=100&page=1') { return @(@{ id = 1; tag_name = 'v2.1.0' }) }
        return @{ id = 2; tag_name = 'v2.1.0' }
    }
    $failed = $false; try { $null = Read-Release 'v2.1.0' } catch { $failed = $true }
    Assert $failed 'Listing and by-ID data must refer to the same release.'
}
Test 'Both installer and portable payloads are required in the exact five-asset release' {
    $expected = @('VoiceInput-Setup-2.1.0.exe', 'VoiceInput-Portable-x64-2.1.0.zip', 'VoiceInput-Source-2.1.0.zip', 'build-output.log', 'SHA256SUMS.txt')
    $assets = @($expected | ForEach-Object { [pscustomobject]@{ name = $_; size = 100; state = 'uploaded' } })
    Assert-ReleaseAssets ([pscustomobject]@{ assets = $assets }) $expected
    foreach ($missing in $expected[0..1]) {
        $failed = $false
        try { Assert-ReleaseAssets ([pscustomobject]@{ assets = @($assets | Where-Object { $_.name -cne $missing }) }) $expected }
        catch { $failed = $true }
        Assert $failed "Publication must reject a missing installer or portable package: $missing"
    }
    $failed = $false
    try { Assert-ReleaseAssets ([pscustomobject]@{ assets = @($assets[0], $assets[0], $assets[2], $assets[3], $assets[4]) }) $expected }
    catch { $failed = $true }
    Assert $failed 'A duplicate installer cannot stand in for the portable package.'
}
Write-Host "RESULT Release: $passed passed; 0 failed. All requests and publication mutations were simulated."
Remove-Item $fixtureDirectory -Recurse -Force
