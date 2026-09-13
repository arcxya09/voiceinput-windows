param([string]$Executable = 'artifacts/publish/win-x64/RealtimeTranscription.exe', [string]$Report = 'windows-smoke.json')
$ErrorActionPreference = 'Stop'
$path = (Resolve-Path $Executable).Path
$reportPath = [IO.Path]::GetFullPath($Report)
if (Test-Path $reportPath) { Remove-Item $reportPath -Force }
$start = [Diagnostics.ProcessStartInfo]::new($path)
$start.UseShellExecute = $false
$start.ArgumentList.Add('--smoke-test')
$start.ArgumentList.Add($reportPath)
$process = [Diagnostics.Process]::Start($start)
try {
    if (!$process.WaitForExit(90000)) { $process.Kill($true); throw 'Offline Windows desktop smoke test timed out.' }
    if (!(Test-Path $reportPath)) { throw "The Windows executable exited without a smoke report (exit $($process.ExitCode))." }
    $result = Get-Content $reportPath -Raw | ConvertFrom-Json
    foreach ($check in $result.checks) { Write-Host "PASS Windows: $check" }
    foreach ($failure in $result.errors) { Write-Host "FAIL Windows: $failure" }
    if ($process.ExitCode -ne 0 -or !$result.passed) { throw 'Offline Windows desktop smoke test failed.' }
    Write-Host 'Windows executable smoke passed. Microphone, cloud, and global keyboard hooks were not activated.'
}
finally { $process.Dispose() }

# Exercise the self-contained WinExe's actual redirected standard handles. This
# catches packaging/entrypoint regressions that a portable test assembly cannot.
# Ping does not query UI Automation, change focus, or generate input events.
$workerStart = [Diagnostics.ProcessStartInfo]::new($path)
$workerStart.UseShellExecute = $false
$workerStart.CreateNoWindow = $true
$workerStart.RedirectStandardInput = $true
$workerStart.RedirectStandardOutput = $true
$workerStart.RedirectStandardError = $true
$workerStart.ArgumentList.Add('--voiceinput-uia-worker')
$workerStart.ArgumentList.Add($PID.ToString())
$worker = [Diagnostics.Process]::Start($workerStart)
try {
    $worker.StandardInput.WriteLine('{"Operation":"Ping"}')
    $worker.StandardInput.Flush()
    $reply = $worker.StandardOutput.ReadLineAsync()
    if (!$reply.Wait([TimeSpan]::FromSeconds(10))) { throw 'The isolated input worker did not answer Ping within 10 seconds.' }
    $line = $reply.GetAwaiter().GetResult()
    if (!$line -or ($line | ConvertFrom-Json).Code -ne 'Ready') { throw 'The isolated input worker returned an invalid Ping response.' }
    $worker.StandardInput.Close()
    if (!$worker.WaitForExit(10000) -or $worker.ExitCode -ne 0) { throw 'The isolated input worker failed to exit cleanly after its pipe closed.' }
    Write-Host 'PASS Windows: packaged input worker answers Ping through redirected standard pipes and exits on EOF.'
}
finally {
    if (!$worker.HasExited) { $worker.Kill($true) }
    $worker.Dispose()
}
