# Windows distribution

`build.ps1` produces both distributions from one self-contained WinUI publish:

- `VoiceInput-Portable-x64-<version>.zip`: `VoiceInput.exe`, `app/`, `licenses/`, `README.txt`.
- `VoiceInput-Setup-<version>.exe`: the same files in a per-user install, plus Windows uninstall registration and a Start menu shortcut. A desktop shortcut is optional.

`src/Launcher/VoiceInput.cpp` is compiled for x64 with the installed Visual Studio C++ toolchain and a static C++ runtime. It starts `app/RealtimeTranscription.exe` using its absolute path and forwards the original Unicode command line without a shell. The launcher exits immediately for normal use. Smoke and UI Automation worker modes instead inherit only the three redirected standard handles, wait for the child, and propagate its exit code. Launcher, application and installed shortcuts use `VoiceInput.Desktop` as their AppUserModelID.

`scripts/BuildInstaller.ps1` downloads the immutable [Inno Setup 6.7.3 release](https://github.com/jrsoftware/issrc/releases/tag/is-6_7_3), verifies its pinned SHA-256, and installs the compiler in portable mode under `artifacts/tools/`. No additional CI action or machine-wide package manager is needed. The Simplified Chinese translation is retained from the [same source tag](https://github.com/jrsoftware/issrc/blob/is-6_7_3/Files/Languages/Unofficial/ChineseSimplified.isl); its original attribution is preserved. The Inno Setup license is included under `licenses/`.

The installer defaults to `%LocalAppData%\Programs\VoiceInput` and does not request elevation. A write-access preflight detects an executable that is still running before either installation or uninstallation changes files. Users must exit through the tray menu; setup never kills the application. Silent setup fails when the target executable is in use.

Both distributions keep the established `%LocalAppData%\RealtimeTranscription` data location. The installer does not delete that directory. Startup registration is controlled only from application settings. Uninstallation removes the `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` value `VoiceInput` only when its full command belongs to the installation being removed, preserving another portable copy's registration.

Portable upgrades should be extracted into a new empty directory so obsolete runtime files from previous releases cannot remain in the payload. The native launcher reports an incomplete extraction if the application is missing.
