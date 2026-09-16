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
function Write-WinUIResourceDiagnostics {
    Write-Host 'WinUI application resource output paths:'
    foreach ($directory in @('src/Desktop/bin', 'src/Desktop/obj', $PublishDirectory)) {
        if (Test-Path $directory) {
            Get-ChildItem $directory -Recurse -File | Where-Object { $_.Extension -in @('.pri', '.xbf') } | ForEach-Object { Write-Host "$($_.FullName) ($($_.Length) bytes)" }
        }
    }
    if (Test-Path $PublishDirectory) {
        Write-Host 'Published runtime files (packaged under app/):'
        Get-ChildItem $PublishDirectory -File | ForEach-Object { Write-Host $_.Name }
    }
    # Inspect only public SDK build definitions, never application data or secrets.
    $nugetRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
    $msixPackage = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools.msix'
    if (Test-Path $msixPackage) {
        Get-ChildItem $msixPackage -Recurse -File -Filter '*.Pri.targets' | ForEach-Object {
            Write-Host "SDK PRI targets: $($_.FullName)"
            Select-String -LiteralPath $_.FullName -Pattern 'ProjectPri|OutputFile|ResolvedFileToPublish|ComputeFilesToPublish' -Context 1,1 | ForEach-Object { Write-Host $_.ToString() }
        }
    }
}
try {
    Invoke-Dotnet -DotnetArgs @('publish','src/Desktop/Desktop.csproj','-c','Release','-r','win-x64','-p:VoiceDependencySet=win-x64','--self-contained','true','--no-restore','-m:1','-o',$PublishDirectory)
}
catch {
    Write-WinUIResourceDiagnostics
    throw
}
foreach ($required in @('RealtimeTranscription.exe','Microsoft.UI.Xaml.dll','coreclr.dll','Assets/AppIcon.ico','Assets/Logo.png')) {
    if (!(Test-Path (Join-Path $PublishDirectory $required))) { throw "Portable WinUI package is missing $required" }
}
$ResourceIndices = @(Get-ChildItem -LiteralPath $PublishDirectory -Filter '*.pri' -File | Where-Object { $_.Name -notlike 'Microsoft.*' })
if ($ResourceIndices.Count -eq 0) { Write-WinUIResourceDiagnostics; throw 'Portable WinUI package is missing its application resource index.' }
Write-Host "Application resource indices: $($ResourceIndices.Name -join ', ')"
$AppVersion = [string]([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
if ($AppVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid release version.' }
$PackageDirectory = 'artifacts/package/win-x64'
if (Test-Path $PackageDirectory) { Remove-Item $PackageDirectory -Recurse -Force }
[IO.Directory]::CreateDirectory([IO.Path]::GetFullPath($PackageDirectory)) | Out-Null
Copy-Item -LiteralPath $PublishDirectory -Destination (Join-Path $PackageDirectory 'app') -Recurse
Copy-Item -LiteralPath 'licenses' -Destination (Join-Path $PackageDirectory 'licenses') -Recurse
# Use a BOM for Inno Setup's license page; preserve the repository text verbatim.
[IO.File]::WriteAllText([IO.Path]::GetFullPath((Join-Path $PackageDirectory 'LICENSE.txt')), [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'LICENSE.txt')), [Text.UTF8Encoding]::new($true))
$PackageReadme = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'installer/README.txt')).Replace('@VERSION@', $AppVersion)
[IO.File]::WriteAllText([IO.Path]::GetFullPath((Join-Path $PackageDirectory 'README.txt')), $PackageReadme, [Text.UTF8Encoding]::new($true))
& (Join-Path $PSScriptRoot 'scripts/BuildLauncher.ps1') -OutputDirectory $PackageDirectory -Version $AppVersion

# The user-facing root is intentionally small; all framework and application
# resources remain together under app/ so WinUI's resource lookup stays intact.
$ExpectedRoot = @('VoiceInput.exe','app','licenses','README.txt','LICENSE.txt')
$ActualRoot = @(Get-ChildItem -LiteralPath $PackageDirectory | Select-Object -ExpandProperty Name)
if (@(Compare-Object $ExpectedRoot $ActualRoot).Count) { throw 'The portable root contains unexpected or missing files.' }
foreach ($required in @('VoiceInput.exe','README.txt','LICENSE.txt','app/RealtimeTranscription.exe','app/RealtimeTranscription.pri','app/Microsoft.UI.Xaml.dll','app/coreclr.dll','app/Assets/AppIcon.ico','app/Assets/Logo.png')) {
    if (!(Test-Path (Join-Path $PackageDirectory $required))) { throw "The organized portable package is missing $required" }
}
$OutputZip = "artifacts/VoiceInput-Portable-x64-$AppVersion.zip"
if (Test-Path $OutputZip) { Remove-Item $OutputZip -Force }
[IO.Compression.ZipFile]::CreateFromDirectory([IO.Path]::GetFullPath($PackageDirectory), [IO.Path]::GetFullPath($OutputZip), [IO.Compression.CompressionLevel]::Optimal, $false)
Get-FileHash $OutputZip -Algorithm SHA256
& (Join-Path $PSScriptRoot 'scripts/BuildInstaller.ps1') -PackageDirectory $PackageDirectory -OutputDirectory 'artifacts' -Version $AppVersion
