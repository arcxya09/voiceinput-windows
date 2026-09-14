param(
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output = [IO.Path]::GetFullPath($OutputDirectory)
$intermediate = Join-Path $root 'artifacts/build/launcher'
[IO.Directory]::CreateDirectory($output) | Out-Null
[IO.Directory]::CreateDirectory($intermediate) | Out-Null

# Use the installed Visual Studio developer shell rather than rely on a
# particular runner edition, year, or machine-wide compiler PATH.
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ build tools are required to compile VoiceInput.exe.' }
$visualStudio = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or !$visualStudio) { throw 'The Visual Studio x64 C++ toolchain was not found.' }
$developerShell = Join-Path ([string]$visualStudio) 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll'
Import-Module $developerShell
Enter-VsDevShell -VsInstallPath ([string]$visualStudio) -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64' | Out-Null

$versionParts = $Version.Split('.') | ForEach-Object { [int]$_ }
if (@($versionParts | Where-Object { $_ -gt 65535 }).Count) { throw 'The native executable version components must fit in 16 bits.' }
$nativeVersion = ($versionParts -join ',') + ',0'
$icon = (Join-Path $root 'src/Desktop/Assets/AppIcon.ico').Replace('\','/')
$resources = @"
#include <windows.h>
1 ICON "$icon"
1 VERSIONINFO
 FILEVERSION $nativeVersion
 PRODUCTVERSION $nativeVersion
 FILEFLAGSMASK 0x3fL
 FILEFLAGS 0x0L
 FILEOS 0x40004L
 FILETYPE 0x1L
 FILESUBTYPE 0x0L
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904b0"
    BEGIN
      VALUE "CompanyName", "VoiceInput"
      VALUE "FileDescription", "VoiceInput"
      VALUE "FileVersion", "$Version.0"
      VALUE "InternalName", "VoiceInput"
      VALUE "OriginalFilename", "VoiceInput.exe"
      VALUE "ProductName", "VoiceInput"
      VALUE "ProductVersion", "$Version"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x409, 1200
  END
END
"@
$rcFile = Join-Path $intermediate 'VoiceInput.rc'
$resFile = Join-Path $intermediate 'VoiceInput.res'
$objFile = Join-Path $intermediate 'VoiceInput.obj'
$exeFile = Join-Path $output 'VoiceInput.exe'
$manifest = Join-Path $intermediate 'VoiceInput.manifest'
[IO.File]::WriteAllText($rcFile, $resources, [Text.UTF8Encoding]::new($false))
$manifestText = [IO.File]::ReadAllText((Join-Path $root 'src/Launcher/launcher.manifest')).Replace('@VERSION@', "$Version.0")
[IO.File]::WriteAllText($manifest, $manifestText, [Text.UTF8Encoding]::new($false))
& rc.exe /nologo /fo $resFile $rcFile
if ($LASTEXITCODE -ne 0) { throw 'Compiling the native launcher icon and version resources failed.' }
& cl.exe /nologo /O2 /MT /EHsc /W4 /utf-8 /std:c++17 /DUNICODE /D_UNICODE /D_WIN32_WINNT=0x0A00 "/Fo$objFile" "/Fe$exeFile" (Join-Path $root 'src/Launcher/VoiceInput.cpp') $resFile /link /SUBSYSTEM:WINDOWS,10.00 /DYNAMICBASE /NXCOMPAT /MANIFEST:EMBED "/MANIFESTINPUT:$manifest" user32.lib shell32.lib
if ($LASTEXITCODE -ne 0) { throw 'Compiling the native VoiceInput launcher failed.' }
if (!(Test-Path -LiteralPath $exeFile)) { throw 'The native VoiceInput launcher is missing.' }
Write-Host "Native launcher: VoiceInput.exe $Version ($((Get-Item -LiteralPath $exeFile).Length) bytes; static C++ runtime)."
