param([switch]$UpdateDependencyLocks)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
function Invoke-Dotnet {
    param([string[]]$DotnetArgs)
    & dotnet @DotnetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed: $($DotnetArgs -join ' ')" }
}

# Lock refresh is an explicit maintainer action; the normal build never updates
# dependency resolution. Commit the generated files, then rerun the locked build.
if ($UpdateDependencyLocks) {
    Invoke-Dotnet -DotnetArgs @('restore','src/Desktop/Desktop.csproj','-r','win-x64','--force-evaluate','-m:1')
    Invoke-Dotnet -DotnetArgs @('restore','tests/Tests.csproj','--force-evaluate','-m:1')
    Invoke-Dotnet -DotnetArgs @('restore','src/Desktop/Desktop.csproj','-r','win-x64','-p:VoiceDependencySet=win-x64','--force-evaluate','-m:1')
    foreach ($lock in Get-ChildItem src,tests -Recurse -Filter 'packages*.lock.json' | Sort-Object FullName) {
        $relative = [IO.Path]::GetRelativePath($PSScriptRoot, $lock.FullName).Replace('\','/')
        Write-Host "DEPENDENCY_LOCK_BEGIN $relative"
        Write-Host ([IO.File]::ReadAllText($lock.FullName))
        Write-Host "DEPENDENCY_LOCK_END $relative"
    }
}

Invoke-Dotnet -DotnetArgs @('restore','tests/Tests.csproj','--locked-mode','-m:1')
Invoke-Dotnet -DotnetArgs @('build','tests/Tests.csproj','-c','Release','--no-restore','-m:1')
Invoke-Dotnet -DotnetArgs @('tests/bin/Release/net10.0/Tests.dll')
Invoke-Dotnet -DotnetArgs @('restore','src/Desktop/Desktop.csproj','-r','win-x64','-p:VoiceDependencySet=win-x64','--locked-mode','-m:1')
$PublishDirectory = 'artifacts/publish/win-x64'
if (Test-Path $PublishDirectory) { Remove-Item $PublishDirectory -Recurse -Force }
Invoke-Dotnet -DotnetArgs @('publish','src/Desktop/Desktop.csproj','-c','Release','-r','win-x64','-p:VoiceDependencySet=win-x64','--self-contained','true','--no-restore','-m:1','-o',$PublishDirectory)
foreach ($required in @('RealtimeTranscription.exe','Microsoft.UI.Xaml.dll','coreclr.dll','Assets/AppIcon.ico','Assets/Logo.png')) {
    if (!(Test-Path (Join-Path $PublishDirectory $required))) { throw "Portable WinUI package is missing $required" }
}
$ResourceIndices = @(Get-ChildItem -LiteralPath $PublishDirectory -Filter '*.pri' -File | Where-Object { $_.Name -notlike 'Microsoft.*' })
if ($ResourceIndices.Count -eq 0) { throw 'Portable WinUI package is missing its application resource index.' }
Write-Host "Application resource indices: $($ResourceIndices.Name -join ', ')"
Copy-Item -Path 'licenses' -Destination (Join-Path $PublishDirectory 'licenses') -Recurse -Force
$AppVersion = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
$OutputZip = "artifacts/VoiceInput-Windows-x64-$AppVersion.zip"
if (Test-Path $OutputZip) { Remove-Item $OutputZip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory([IO.Path]::GetFullPath($PublishDirectory), [IO.Path]::GetFullPath($OutputZip), [IO.Compression.CompressionLevel]::Optimal, $false)
Get-FileHash $OutputZip -Algorithm SHA256
