param(
    [Parameter(Mandatory)][string]$PackageDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$package = [IO.Path]::GetFullPath($PackageDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)

# Pin the compiler, including its download hash, independently of the mutable
# windows-latest image. The vendor marks this GitHub release immutable.
# https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3
$compilerVersion = '6.7.3'
$compilerHash = '9c73c3bae7ed48d44112a0f48e66742c00090bdb5bef71d9d3c056c66e97b732'
$toolsDirectory = Join-Path $root 'artifacts/tools'
$compilerDirectory = Join-Path $toolsDirectory "inno-$compilerVersion"
$compiler = Join-Path $compilerDirectory 'ISCC.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    [IO.Directory]::CreateDirectory($toolsDirectory) | Out-Null
    $download = Join-Path $toolsDirectory "innosetup-$compilerVersion.exe"
    Invoke-WebRequest -Uri "https://github.com/jrsoftware/issrc/releases/download/is-6_7_3/innosetup-$compilerVersion.exe" -OutFile $download
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -ne $compilerHash) {
        throw 'The Inno Setup compiler download failed SHA-256 verification.'
    }
    $installArgs = @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/PORTABLE=1', "/DIR=`"$compilerDirectory`"", '/NOICONS', '/TASKS=')
    $setup = Start-Process -FilePath $download -ArgumentList $installArgs -Wait -PassThru
    if ($setup.ExitCode -ne 0) { throw "Installing the verified Inno Setup compiler failed (exit $($setup.ExitCode))." }
}
if (!(Test-Path -LiteralPath $compiler)) { throw 'The Inno Setup compiler is missing.' }
$frontendVersion = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
Write-Host "ISCC frontend file version: $frontendVersion"
Write-Host "Installer compiler: Inno Setup $compilerVersion (pinned SHA-256 verified download)."
[IO.Directory]::CreateDirectory($output) | Out-Null
$setupFile = Join-Path $output "VoiceInput-Setup-$Version.exe"
if (Test-Path -LiteralPath $setupFile) { Remove-Item -LiteralPath $setupFile -Force }
& $compiler "/DAppVersion=$Version" "/DPackageDir=$package" "/DOutputDir=$output" (Join-Path $root 'installer/VoiceInput.iss') | Tee-Object -Variable compilerOutput
if ($LASTEXITCODE -ne 0) { throw 'Compiling the VoiceInput installer failed.' }
# ISCC's frontend version resource is independent of its compiler engine.
# The vendor's ISCC.dpr prints this banner from ISDllGetVersion after loading
# ISCmplr.dll; check that engine version, while retaining the download hash gate.
$engineBanner = @($compilerOutput | Where-Object { [string]$_ -match '^Compiler engine version:' } | Select-Object -First 1)
$expectedEngine = '^Compiler engine version:\s+Inno Setup\s+' + [regex]::Escape($compilerVersion) + '(?:\s|$)'
if ($engineBanner.Count -ne 1 -or [string]$engineBanner[0] -notmatch $expectedEngine) {
    throw 'The Inno Setup compiler engine does not match the pinned version.'
}
if (!(Test-Path -LiteralPath $setupFile)) { throw 'The VoiceInput installer was not produced.' }
Get-FileHash -LiteralPath $setupFile -Algorithm SHA256
