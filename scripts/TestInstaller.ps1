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
    foreach ($relative in @('VoiceInput.exe', 'app/RealtimeTranscription.exe', 'app/RealtimeTranscription.pri', 'app/Microsoft.UI.Xaml.dll', 'unins000.exe')) {
        if (!(Test-Path -LiteralPath (Join-Path $installDirectory $relative) -PathType Leaf)) { throw "The installer did not create $relative." }
    }
    $registration = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallSubkey)
    try {
        if (!$registration -or $registration.GetValue('DisplayVersion') -ne $version) { throw 'The per-user uninstall registration has no matching application version.' }
        if ($registration.GetValue('UninstallString') -notlike "*$installDirectory*unins000.exe*") { throw 'The uninstall registration points outside the installed application.' }
    }
    finally { if ($registration) { $registration.Dispose() } }
    Assert-UserDataPreserved
}

function Invoke-TestUninstall([string]$Suffix) {
    $uninstaller = Join-Path $installDirectory 'unins000.exe'
    if (!(Test-Path -LiteralPath $uninstaller)) { throw 'The installed uninstaller is missing.' }
    Invoke-InstallerProcess $uninstaller @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', "/LOG=$(Join-Path $evidenceDirectory "installer-uninstall$Suffix.log")")
    # Inno can complete final file cleanup through its temporary uninstaller.
    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    do {
        $registration = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($uninstallSubkey)
        $registered = $null -ne $registration
        if ($registration) { $registration.Dispose() }
        if (!$registered -and !(Test-Path -LiteralPath $launcher)) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($registered -or (Test-Path -LiteralPath $launcher) -or (Test-Path -LiteralPath (Join-Path $installDirectory 'app/RealtimeTranscription.exe'))) {
        throw 'Uninstall left the registration, launcher, or application executable behind.'
    }
    if ((Test-Path -LiteralPath $shortcutDirectory) -and @(Get-ChildItem -LiteralPath $shortcutDirectory -Filter '*.lnk' -Recurse).Count -ne 0) {
        throw 'Uninstall left the installed Start menu shortcut behind.'
    }
    Assert-UserDataPreserved
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
    if ($mayUninstall -and (Test-Path -LiteralPath (Join-Path $installDirectory 'unins000.exe'))) {
        try { Invoke-TestUninstall '-cleanup' } catch { $errors.Add('Installer cleanup: ' + $_.Exception.Message) }
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
