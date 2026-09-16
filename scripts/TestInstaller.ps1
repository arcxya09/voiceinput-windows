param(
    [string]$Installer = '',
    [string]$Report = 'installer-checks.json'
)
$ErrorActionPreference = 'Stop'
$reportPath = [IO.Path]::GetFullPath($Report)
$evidenceDirectory = [IO.Path]::GetDirectoryName($reportPath)
[IO.Directory]::CreateDirectory($evidenceDirectory) | Out-Null
$checks = [Collections.Generic.List[string]]::new()
$errors = [Collections.Generic.List[string]]::new()
$version = [string]([xml](Get-Content (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw)).Project.PropertyGroup.Version
if (!$Installer) { $Installer = Join-Path $PSScriptRoot "../artifacts/VoiceInput-Setup-$version.exe" }
$setup = (Resolve-Path -LiteralPath $Installer).Path
$token = [Guid]::NewGuid().ToString('N')
$installDirectory = Join-Path ([IO.Path]::GetTempPath()) "VoiceInput Installer Smoke $token"
$group = "VoiceInput Installer Smoke $token"
$shortcutDirectory = Join-Path ([Environment]::GetFolderPath('Programs')) $group
$uninstallSubkey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{D2D24A36-3D20-4EAE-9A0B-82C57CF0C671}_is1'
$runSubkey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$launcher = Join-Path $installDirectory 'VoiceInput.exe'
$ownedStartup = '"' + $launcher + '" --startup'
$portableStartup = '"' + (Join-Path ([IO.Path]::GetTempPath()) "Another Portable $token\VoiceInput.exe") + '" --startup'
$fixture = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) "RealtimeTranscription\installer-preserve-$token.txt"
$fixtureText = "VoiceInput installer preservation fixture $token 词库和历史保留"
$mayUninstall = $false
$fixtureCreated = $false
$script:installedUninstaller = $null

function Add-Check([string]$Message) {
    $checks.Add($Message)
    Write-Host "PASS Installer: $Message"
}

function Read-Startup {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runSubkey)
    try { if ($key) { return $key.GetValue('VoiceInput', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } }
    finally { if ($key) { $key.Dispose() } }
    return $null
}

function Write-TestStartup([string]$Command) {
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runSubkey)
    try { $key.SetValue('VoiceInput', $Command, [Microsoft.Win32.RegistryValueKind]::String) }
    finally { $key.Dispose() }
}

function Assert-UserDataPreserved {
    if (!(Test-Path -LiteralPath $fixture) -or [IO.File]::ReadAllText($fixture) -ne $fixtureText) {
        throw 'Installation or removal changed the isolated fixture in the existing user-data directory.'
    }
}

function Read-RegisteredUninstaller {
    $registration = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallSubkey)
    try {
        if (!$registration) { return $null }
        $command = [string]$registration.GetValue('UninstallString')
    }
    finally { if ($registration) { $registration.Dispose() } }
    # Inno chooses an available uninsNNN name. Only accept a single executable
    # belonging directly to this test installation; never execute arbitrary
    # registry command text or an uninstaller from another installation.
    if ($command -notmatch '^"([^"\r\n]+)"$') { throw 'The uninstall registration is not a single quoted executable path.' }
    $candidate = [IO.Path]::GetFullPath($Matches[1])
    if ([IO.Path]::GetFileName($candidate) -notmatch '^unins\d{3}\.exe$' -or
        ![string]::Equals([IO.Path]::GetDirectoryName($candidate), [IO.Path]::GetFullPath($installDirectory), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The uninstall registration points outside the installed application.'
    }
    if (!(Test-Path -LiteralPath $candidate -PathType Leaf) -or !(Test-Path -LiteralPath ([IO.Path]::ChangeExtension($candidate, '.dat')) -PathType Leaf)) {
        throw 'The registered uninstaller executable or its data file is missing.'
    }
    return $candidate
}

function Invoke-InstallerProcess([string]$Path, [string[]]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Path)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(120000)) { $process.Kill($true); $process.WaitForExit(); throw 'The silent installer or uninstaller timed out.' }
        if ($process.ExitCode -ne 0) { throw "The silent installer or uninstaller returned exit $($process.ExitCode)." }
    }
    finally { $process.Dispose() }
}

function Invoke-TestInstall([string]$Suffix) {
    Invoke-InstallerProcess $setup @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/DIR=$installDirectory", "/GROUP=$group", "/LOG=$(Join-Path $evidenceDirectory "installer-install$Suffix.log")")
    foreach ($relative in @('VoiceInput.exe', 'LICENSE.txt', 'app/RealtimeTranscription.exe', 'app/RealtimeTranscription.pri', 'app/Microsoft.UI.Xaml.dll')) {
        if (!(Test-Path -LiteralPath (Join-Path $installDirectory $relative) -PathType Leaf)) { throw "The installer did not create $relative." }
    }
    if ([IO.File]::ReadAllText((Join-Path $installDirectory 'LICENSE.txt')) -cne [IO.File]::ReadAllText((Join-Path $PSScriptRoot '../LICENSE.txt'))) { throw 'The installed personal-use license differs from the repository license.' }
    $registration = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallSubkey)
    try {
        if (!$registration -or $registration.GetValue('DisplayVersion') -ne $version) { throw 'The per-user uninstall registration has no matching application version.' }
    }
    finally { if ($registration) { $registration.Dispose() } }
    $script:installedUninstaller = Read-RegisteredUninstaller
    if (!$script:installedUninstaller) { throw 'The installer did not register its uninstaller executable.' }
    Write-Host "Registered test uninstaller: $([IO.Path]::GetFileName($script:installedUninstaller))"
    Assert-UserDataPreserved
}

function Invoke-TestUninstall([string]$Suffix) {
    $uninstaller = Read-RegisteredUninstaller
    if (!$uninstaller -or $uninstaller -ne $script:installedUninstaller) { throw 'The installed uninstaller registration changed unexpectedly.' }
    $uninstallData = [IO.Path]::ChangeExtension($uninstaller, '.dat')
    Invoke-InstallerProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $evidenceDirectory "installer-uninstall$Suffix.log")")
    # Inno can return from its bootstrap executable before its temporary
    # uninstaller finishes self-removal. Wait for the exact registered exe/dat
    # as well as the application and registry before reusing this directory.
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $registration = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallSubkey)
        $registered = $null -ne $registration
        if ($registration) { $registration.Dispose() }
        $selfCleanupPending = (Test-Path -LiteralPath $uninstaller) -or (Test-Path -LiteralPath $uninstallData)
        if (!$registered -and !(Test-Path -LiteralPath $launcher) -and !$selfCleanupPending) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($registered -or $selfCleanupPending -or (Test-Path -LiteralPath $launcher) -or (Test-Path -LiteralPath (Join-Path $installDirectory 'app/RealtimeTranscription.exe'))) {
        throw 'Uninstall left the registration, launcher, application executable, or its own executable/data file behind.'
    }
    if ((Test-Path -LiteralPath $shortcutDirectory) -and @(Get-ChildItem -LiteralPath $shortcutDirectory -Filter '*.lnk' -Recurse).Count -ne 0) {
        throw 'Uninstall left the installed Start menu shortcut behind.'
    }
    Assert-UserDataPreserved
    $script:installedUninstaller = $null
}

try {
    # Refuse to touch a developer's real installation or startup registration.
    # CI is disposable; the tests never need to repurpose an existing value.
    $existing = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallSubkey)
    try { if ($existing) { throw 'An existing VoiceInput installation is registered. Run installer verification in an isolated Windows account.' } }
    finally { if ($existing) { $existing.Dispose() } }
    if ($null -ne (Read-Startup)) { throw 'An existing VoiceInput startup entry is present. Run installer verification in an isolated Windows account.' }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($fixture)) | Out-Null
    [IO.File]::WriteAllText($fixture, $fixtureText)
    $fixtureCreated = $true
    $mayUninstall = $true
    Invoke-TestInstall ''
    Add-Check 'Silent per-user installation creates the organized app bundle and a versioned uninstall registration in a path containing spaces.'

    # A controlled parent process proves the updater never writes over a live app.
    $gate = Join-Path ([IO.Path]::GetTempPath()) "VoiceInput-update-gate-$token"
    $parent = $null
    $updater = $null
    $updateLog = Join-Path $evidenceDirectory 'installer-update-wait.log'
    try {
        $parentStart = [Diagnostics.ProcessStartInfo]::new((Get-Process -Id $PID).Path)
        $parentStart.UseShellExecute = $false
        $parentStart.Environment['VOICEINPUT_TEST_GATE'] = $gate
        foreach ($arg in @('-NoProfile', '-Command', 'while (!(Test-Path -LiteralPath $env:VOICEINPUT_TEST_GATE)) { Start-Sleep -Milliseconds 50 }')) { $parentStart.ArgumentList.Add($arg) }
        $parent = [Diagnostics.Process]::Start($parentStart)
        $beforeUpdate = (Get-Item -LiteralPath $launcher).LastWriteTimeUtc
        $updateStart = [Diagnostics.ProcessStartInfo]::new($setup)
        $updateStart.UseShellExecute = $false
        foreach ($arg in @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/UPDATEWAIT=$($parent.Id)", "/DIR=$installDirectory", "/GROUP=$group", "/LOG=$updateLog")) { $updateStart.ArgumentList.Add($arg) }
        $updater = [Diagnostics.Process]::Start($updateStart)
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $waiting = (Test-Path -LiteralPath $updateLog) -and ((Get-Content -LiteralPath $updateLog -Raw) -match 'Waiting for VoiceInput update parent to exit')
            if ($waiting -or $updater.HasExited) { break }
            Start-Sleep -Milliseconds 100
        } while ([DateTime]::UtcNow -lt $deadline)
        if (!$waiting -or $updater.HasExited -or $parent.HasExited -or (Get-Item -LiteralPath $launcher).LastWriteTimeUtc -ne $beforeUpdate) { throw 'Update installation did not wait for the live parent before modifying files.' }
        [IO.File]::WriteAllText($gate, 'exit')
        if (!$parent.WaitForExit(10000) -or !$updater.WaitForExit(120000) -or $updater.ExitCode -ne 0) { throw 'Update installer did not complete after the parent exited.' }
        if ((Get-Content -LiteralPath $updateLog -Raw) -notmatch 'VoiceInput update parent exited') { throw 'Update wait completion was not recorded.' }
        Assert-UserDataPreserved
        Add-Check 'Update installer waits for the exact parent process, then upgrades in place and preserves user data.'
    }
    finally {
        foreach ($process in @($parent, $updater)) {
            if ($process) { if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }; $process.Dispose() }
        }
        if (Test-Path -LiteralPath $gate) { Remove-Item -LiteralPath $gate -Force }
    }
    $invalidStart = [Diagnostics.ProcessStartInfo]::new($setup)
    $invalidStart.UseShellExecute = $false
    foreach ($arg in @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/UPDATEWAIT=invalid')) { $invalidStart.ArgumentList.Add($arg) }
    $invalid = [Diagnostics.Process]::Start($invalidStart)
    try {
        if (!$invalid.WaitForExit(30000)) { $invalid.Kill($true); $invalid.WaitForExit(); throw 'Invalid update parent did not fail promptly.' }
        if ($invalid.ExitCode -eq 0) { throw 'Invalid update parent was accepted.' }
    } finally { $invalid.Dispose() }
    Add-Check 'Malformed update parent arguments abort without proceeding with installation.'

    $shortcuts = @(Get-ChildItem -LiteralPath $shortcutDirectory -Filter '*.lnk' -Recurse)
    if ($shortcuts.Count -lt 1) { throw 'The installer did not create a Start menu shortcut.' }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $launchLinks = @($shortcuts | Where-Object { $shell.CreateShortcut($_.FullName).TargetPath -eq $launcher })
        if ($launchLinks.Count -ne 1) { throw 'The Start menu does not contain exactly one shortcut to the stable root launcher.' }
    }
    finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell) }
    Add-Check 'The Start menu shortcut targets the root VoiceInput.exe launcher.'

    & (Join-Path $PSScriptRoot 'WindowsSmoke.ps1') -Executable $launcher -Report (Join-Path $evidenceDirectory 'installer-smoke.json')
    Add-Check 'The installed launcher passes the full offline native UI, frame, worker-pipe, exit-code, and silent-startup checks.'

    Write-TestStartup $ownedStartup
    Invoke-TestUninstall ''
    if ($null -ne (Read-Startup)) { throw 'Uninstall did not remove its own installed application startup command.' }
    Add-Check 'Uninstall removes its own startup command, application files, shortcut, and registration while preserving user data.'

    Invoke-TestInstall '-portable-preservation'
    Write-TestStartup $portableStartup
    Invoke-TestUninstall '-portable-preservation'
    if ((Read-Startup) -cne $portableStartup) { throw 'Uninstall removed or changed a different portable copy startup command.' }
    Add-Check 'Uninstall preserves a startup command belonging to another portable copy and leaves user data unchanged.'
}
catch {
    $errors.Add($_.Exception.Message)
    Write-Host "FAIL Installer: $($_.Exception.Message)"
}
finally {
    if ($mayUninstall) {
        try {
            $registeredUninstaller = Read-RegisteredUninstaller
            if ($registeredUninstaller) {
                $script:installedUninstaller = $registeredUninstaller
                Invoke-TestUninstall '-cleanup'
            }
        }
        catch { $errors.Add('Installer cleanup: ' + $_.Exception.Message) }
    }
    if ($mayUninstall) {
        $currentStartup = Read-Startup
        if ($currentStartup -ceq $ownedStartup -or $currentStartup -ceq $portableStartup) {
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runSubkey, $true)
            try { if ($key) { $key.DeleteValue('VoiceInput', $false) } }
            finally { if ($key) { $key.Dispose() } }
        }
    }
    if ($fixtureCreated -and (Test-Path -LiteralPath $fixture)) { Remove-Item -LiteralPath $fixture -Force }
    [ordered]@{ passed = $errors.Count -eq 0; version = $version; checks = $checks.ToArray(); errors = $errors.ToArray(); userData = 'Only a unique owned fixture was created and removed; other files were never changed.' } |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $reportPath -Encoding utf8
}
if ($errors.Count -gt 0) { throw 'Installer verification failed. See installer-checks.json and installer-*.log.' }
