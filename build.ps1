$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
function Invoke-Dotnet {
    param([string[]]$DotnetArgs)
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($DotnetArgs -join ' ')" }
}
Invoke-Dotnet -DotnetArgs @('restore','tests/Tests.csproj','--locked-mode','-m:1')
Invoke-Dotnet -DotnetArgs @('build','tests/Tests.csproj','-c','Release','--no-restore','-m:1')
Invoke-Dotnet -DotnetArgs @('tests/bin/Release/net10.0/Tests.dll')
Invoke-Dotnet -DotnetArgs @('restore','src/Desktop/Desktop.csproj','-r','win-x64','-p:VoiceDependencySet=win-x64','--locked-mode','-m:1')
Invoke-Dotnet -DotnetArgs @('publish','src/Desktop/Desktop.csproj','-c','Release','-r','win-x64','-p:VoiceDependencySet=win-x64','--self-contained','true','--no-restore','-m:1','-o','artifacts/publish/win-x64')
$AppVersion = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
$OutputExe = "artifacts/VoiceInput-Windows-x64-$AppVersion.exe"
Copy-Item artifacts/publish/win-x64/RealtimeTranscription.exe $OutputExe -Force
Get-FileHash $OutputExe -Algorithm SHA256
