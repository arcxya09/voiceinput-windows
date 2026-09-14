param(
    [string]$Package = '',
    [string]$Executable = '',
    [string]$Report = 'windows-smoke.json'
)
$ErrorActionPreference = 'Stop'
$reportPath = [IO.Path]::GetFullPath($Report)
$smokeLog = [IO.Path]::ChangeExtension($reportPath, '.log')
$unpacked = $null

try {
    if (!$Executable) {
        if (!$Package) {
            $props = [xml](Get-Content (Join-Path $PSScriptRoot '../Directory.Build.props') -Raw)
            $version = [string]$props.Project.PropertyGroup.Version
            if (!$version) { throw 'The release version is missing from Directory.Build.props.' }
            $Package = Join-Path $PSScriptRoot "../artifacts/VoiceInput-Windows-x64-$version.zip"
        }
        $archive = (Resolve-Path $Package).Path
        $unpacked = Join-Path ([IO.Path]::GetTempPath()) ('VoiceInputPortableSmoke-' + [Guid]::NewGuid().ToString('N'))
        Expand-Archive -LiteralPath $archive -DestinationPath $unpacked
        $Executable = Join-Path $unpacked 'RealtimeTranscription.exe'
        Write-Host "Testing the extracted release package: $([IO.Path]::GetFileName($archive))"
    }
    $path = (Resolve-Path $Executable).Path
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
    foreach ($stale in @($reportPath, [IO.Path]::ChangeExtension($reportPath, '.png'), (Join-Path ([IO.Path]::GetDirectoryName($reportPath)) 'windows-overlay.png'), $smokeLog)) {
        if (Test-Path $stale) { Remove-Item $stale -Force }
    }

    $start = [Diagnostics.ProcessStartInfo]::new($path)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = [IO.Path]::GetDirectoryName($path)
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.ArgumentList.Add('--smoke-test')
    $start.ArgumentList.Add($reportPath)
    $process = [Diagnostics.Process]::Start($start)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    try {
        if (!$process.WaitForExit(90000)) { $process.Kill($true); $process.WaitForExit(); throw 'Offline WinUI 3 desktop smoke test timed out.' }
        @($stdout.GetAwaiter().GetResult(), $stderr.GetAwaiter().GetResult()) | Set-Content -LiteralPath $smokeLog -Encoding utf8
        if (!(Test-Path $reportPath)) { throw "The published WinUI executable exited without a smoke report (exit $($process.ExitCode)). See windows-smoke.log." }
        $result = Get-Content $reportPath -Raw | ConvertFrom-Json
        foreach ($check in $result.checks) { Write-Host "PASS Windows: $check" }
        foreach ($failure in $result.errors) { Write-Host "FAIL Windows: $failure" }
        if ($process.ExitCode -ne 0 -or !$result.passed) { throw 'Offline WinUI 3 desktop smoke test failed.' }
        foreach ($capture in @([IO.Path]::ChangeExtension($reportPath, '.png'), (Join-Path ([IO.Path]::GetDirectoryName($reportPath)) 'windows-overlay.png'))) {
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
    $workerStart.WorkingDirectory = [IO.Path]::GetDirectoryName($path)
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
}
finally {
    if ($unpacked -and (Test-Path $unpacked)) { Remove-Item -LiteralPath $unpacked -Recurse -Force }
}
