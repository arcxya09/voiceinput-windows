param(
    [string]$Package = '',
    [string]$Executable = '',
    [string]$Report = 'windows-smoke.json'
)
$ErrorActionPreference = 'Stop'
$reportPath = [IO.Path]::GetFullPath($Report)
$smokeLog = [IO.Path]::ChangeExtension($reportPath, '.log')
$evidenceStem = [IO.Path]::GetFileNameWithoutExtension($reportPath) -replace '-smoke$', ''
$evidenceFolder = [IO.Path]::GetDirectoryName($reportPath)
$captures = @([IO.Path]::ChangeExtension($reportPath, '.png')) + @('overlay', 'frames', 'responsive' | ForEach-Object { Join-Path $evidenceFolder "$evidenceStem-$_.png" })
$startupReport = Join-Path $evidenceFolder "$evidenceStem-startup.json"
$unpacked = $null

try {
    if (!$Executable) {
        if (!$Package) {
            $props = [xml](Get-Content (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw)
            $version = [string]$props.Project.PropertyGroup.Version
            if (!$version) { throw 'The release version is missing from Directory.Build.props.' }
            $Package = Join-Path $PSScriptRoot "../artifacts/VoiceInput-Portable-x64-$version.zip"
        }
        $archive = (Resolve-Path $Package).Path
        $unpacked = Join-Path ([IO.Path]::GetTempPath()) ('VoiceInput Portable Smoke ' + [Guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $archive -DestinationPath $unpacked
        $Executable = Join-Path $unpacked 'VoiceInput.exe'
        $rootNames = @(Get-ChildItem -LiteralPath $unpacked | ForEach-Object Name | Sort-Object)
        if (@(Compare-Object @('app', 'licenses', 'README.txt', 'VoiceInput.exe') $rootNames).Count -ne 0) {
            throw "The portable root is not organized as documented: $($rootNames -join ', ')"
        }
        foreach ($required in @('app/RealtimeTranscription.exe', 'app/RealtimeTranscription.pri', 'app/Microsoft.UI.Xaml.dll', 'app/coreclr.dll')) {
            if (!(Test-Path (Join-Path $unpacked $required) -PathType Leaf)) { throw "The portable app directory is missing $required" }
        }
        Write-Host "Testing the extracted release package: $([IO.Path]::GetFileName($archive))"
    }
    $path = (Resolve-Path $Executable).Path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
    foreach ($stale in (@($reportPath, $smokeLog, $startupReport) + $captures)) {
        if (Test-Path $stale) { Remove-Item $stale -Force }
    }

    $start = [Diagnostics.ProcessStartInfo]::new($path)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetTempPath()
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--smoke-test')
    $start.ArgumentList.Add($reportPath)
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (!$process.WaitForExit(120000)) { $process.Kill($true); $process.WaitForExit(); throw 'Offline WinUI 3 desktop smoke test timed out.' }
        @($stdout.GetAwaiter().GetResult(), $stderr.GetAwaiter().GetResult()) | Set-Content -LiteralPath $smokeLog -Encoding utf8
        if (!(Test-Path $reportPath)) { throw "The published WinUI executable exited without a smoke report (exit $($process.ExitCode)). See windows-smoke.log." }
        $result = Get-Content $reportPath -Raw | ConvertFrom-Json
        foreach ($check in $result.checks) { Write-Host "PASS Windows: $check" }
        foreach ($failure in $result.errors) { Write-Host "FAIL Windows: $failure" }
        if ($process.ExitCode -ne 0 -or !$result.passed) { throw 'Offline WinUI 3 desktop smoke test failed.' }
        foreach ($capture in $captures) {
            if (!(Test-Path $capture) -or (Get-Item $capture).Length -lt 1024) { throw "A required actual WinUI screenshot is missing or empty: $capture" }
        }
        Write-Host 'Published WinUI 3 package smoke passed. Microphone, cloud, and global keyboard hooks were not activated.'
    }
    finally {
        if (!$process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        if (!(Test-Path $smokeLog)) {
            @($stdout.GetAwaiter().GetResult(), $stderr.GetAwaiter().GetResult()) | Set-Content -LiteralPath $smokeLog -Encoding utf8
        }
        $process.Dispose()
    }

    # Use the executable from the same extracted portable package. Ping exits
    # before WinUI initialization and exercises its redirected standard handles
    # without querying another app, changing focus, or generating input.
    $workerStart = [Diagnostics.ProcessStartInfo]::new($path)
    $workerStart.UseShellExecute = $false
    $workerStart.WorkingDirectory = [IO.Path]::GetTempPath()
    $workerStart.CreateNoWindow = $true
    $workerStart.RedirectStandardInput = $true
    $workerStart.RedirectStandardOutput = $true
    $workerStart.RedirectStandardError = $true
    $workerStart.ArgumentList.Add('--voiceinput-uia-worker')
    $workerStart.ArgumentList.Add($PID.ToString())
    $worker = [Diagnostics.Process]::Start($workerStart)
    $workerErrors = $worker.StandardError.ReadToEndAsync()
    try {
        $worker.StandardInput.WriteLine('{"Operation":"Ping"}')
        $worker.StandardInput.Flush()
        $reply = $worker.StandardOutput.ReadLineAsync()
        if (!$reply.Wait([TimeSpan]::FromSeconds(10))) { throw 'The isolated input worker did not answer Ping within 10 seconds.' }
        $line = $reply.GetAwaiter().GetResult()
        if (!$line -or ($line | ConvertFrom-Json).Code -ne 'Ready') { throw 'The isolated input worker returned an invalid Ping response.' }
        $worker.StandardInput.Close()
        if (!$worker.WaitForExit(10000) -or $worker.ExitCode -ne 0) { throw 'The isolated input worker failed to exit cleanly after its pipe closed.' }
        Write-Host 'PASS Windows: portable WinUI package input worker answers Ping through redirected standard pipes and exits on EOF.'
    }
    finally {
        if (!$worker.HasExited) { $worker.Kill($true); $worker.WaitForExit() }
        $workerErrors.GetAwaiter().GetResult() | Add-Content -LiteralPath $smokeLog -Encoding utf8
        $worker.Dispose()
    }

    # Verify the real launcher also preserves a failing child exit code.
    $invalidStart = [Diagnostics.ProcessStartInfo]::new($path)
    $invalidStart.UseShellExecute = $false
    $invalidStart.CreateNoWindow = $true
    $invalidStart.WorkingDirectory = [IO.Path]::GetTempPath()
    $invalidStart.ArgumentList.Add('--smoke-test')
    $invalidStart.ArgumentList.Add('relative-report-is-invalid.json')
    $invalid = [Diagnostics.Process]::Start($invalidStart)
    try {
        if (!$invalid.WaitForExit(15000) -or $invalid.ExitCode -ne 2) { throw 'The launcher did not propagate the invalid smoke request exit code 2.' }
        Write-Host 'PASS Windows: root launcher resolves app/ from another working directory, preserves spaced paths, and propagates child exit codes.'
    }
    finally {
        if (!$invalid.HasExited) { $invalid.Kill($true); $invalid.WaitForExit() }
        $invalid.Dispose()
    }

    $startupStart = [Diagnostics.ProcessStartInfo]::new($path)
    $startupStart.UseShellExecute = $false
    $startupStart.CreateNoWindow = $true
    $startupStart.WorkingDirectory = [IO.Path]::GetTempPath()
    $startupStart.ArgumentList.Add('--startup')
    $startupStart.ArgumentList.Add('--startup-smoke')
    $startupStart.ArgumentList.Add($startupReport)
    $startup = [Diagnostics.Process]::Start($startupStart)
    try {
        if (!$startup.WaitForExit(30000)) { throw 'The isolated silent-startup probe timed out.' }
        if ($startup.ExitCode -ne 0 -or !(Test-Path $startupReport)) { throw "The silent-startup probe did not produce its report (exit $($startup.ExitCode))." }
        $startupResult = Get-Content -LiteralPath $startupReport -Raw | ConvertFrom-Json
        if (!$startupResult.passed -or $startupResult.visible -or !$startupResult.trayVisible -or !$startupResult.captureIdle -or !$startupResult.focusPreserved -or $startupResult.networkRequests -ne 0) {
            throw "The --startup launch was not silent and offline: $($startupResult | ConvertTo-Json -Compress)"
        }
        Write-Host 'PASS Windows: --startup reaches the production launch decision with a hidden window and visible tray, without microphone, network, or global hooks.'
    }
    finally {
        if (!$startup.HasExited) { $startup.Kill($true); $startup.WaitForExit() }
        $startup.Dispose()
    }
}
finally {
    if ($unpacked -and (Test-Path $unpacked)) { Remove-Item -LiteralPath $unpacked -Recurse -Force }
}
