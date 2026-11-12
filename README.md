# Conduit Client Launcher

A windowless Windows x64 launcher for Conduit Client and Minecraft for Windows (Bedrock Edition).

Run `ConduitClientLauncher.exe`. The launcher reuses `%LOCALAPPDATA%\ConduitClient\dll\ConduitClient.dll` if present. Otherwise it installs the `ConduitClient.dll` asset from the most recently published [Conduit Client release](https://github.com/Conduit-BE/Client/releases), including betas. It checks the downloaded size, the SHA-256 digest when provided by GitHub, and the DLL's x64 PE header before installing it.

The launcher looks for `Minecraft.Windows.exe` beside itself, then tries the registered Minecraft for Windows app and looks in `XboxGames` on fixed drives. If those checks find no game, it looks for LeviLauncher and starts its default instance through the LeviLauncher 1.1.0 CLI. For a portable LeviLauncher, place its EXE beside this launcher.

Use `--levi` to select LeviLauncher even when a Store or Xbox install exists. You can optionally give the instance name as it appears in LeviLauncher:

```powershell
.\ConduitClientLauncher.exe --levi 'Conduit - 1.26.60.5'
```

You can also supply an explicit executable path from PowerShell:

```powershell
.\ConduitClientLauncher.exe 'C:\path\to\Minecraft.Windows.exe'
```

Minecraft starts through its package registration, Xbox launch helper, LeviLauncher, or executable, depending on how it was found. The launcher loads the DLL into the Minecraft process and checks that Windows lists it as a loaded module. Launcher diagnostics go to `%LOCALAPPDATA%\ConduitClient\logs\launcher.log`. For diagnostics from Conduit itself, see the [client documentation](https://github.com/Conduit-BE/Client).

Conduit Client selects supported Minecraft executables by hash. A successfully loaded DLL can still decline to initialize on an unsupported build. See the release notes for supported versions and distributions. The `v0.4.4-beta` Windows release documents support for Bedrock 1.26.60.5 from LeviLauncher 1.1.0.

## Build

Run `powershell.exe -NoProfile -File .\build.ps1` on x64 Windows with .NET Framework 4.8. The executable is written to `dist\ConduitClientLauncher.exe`.
