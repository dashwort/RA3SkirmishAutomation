# Red Alert 3 Skirmish Launcher

A Windows launcher for Command & Conquer: Red Alert 3 and supported mods.

## Launch routes

The launcher uses one of two routes, based on the selected game or mod:

- **Direct launch:** starts the base game or supported Epic War 5.4 configuration straight into a saved skirmish preset. This route uses the supported patched game binary; install support from the Recovery tab before launching.
- **Standard launch:** starts other mods through Red Alert 3's normal menu.

There is no separate launch-mode selector.

## Download and run

After a commit is pushed, GitHub Actions builds a Windows package. Download the latest `RA3SkirmishLauncher` artifact from the [Actions page](https://github.com/dashwort/RA3SkirmishAutomation/actions), keep the package folder intact, and run `RA3SkirmishLauncher.exe`. CI artifacts are retained for 30 days.

Select the game or mod, choose a preset when the direct route is available, and select **Launch game**. Keep the launcher open until the game closes and cleanup finishes.

Direct launch requires an installed supported game version and an active, unlocked Windows desktop. The launcher saves the files it needs to restore and reports when cleanup completes.

## Settings and presets

Settings include the game and profile locations, preset folder, backup location, theme, map compatibility, and SAGE Unlocked. Select **Apply settings** after changing an option.

Presets can be imported or saved under a new name. A successful direct launch can update `default.ini` with the preset used. Failed or cancelled launches do not replace it.

## SAGE Unlocked 60 FPS

SAGE Unlocked v1.9.5 is included and defaults to off. Turn on **SAGE Unlocked 60 FPS** and apply settings to enable it. The setting applies to both launch routes and targets up to 60 FPS while keeping normal game speed; the actual rate may be limited by the display's refresh rate.

Turn the setting off and apply to remove unchanged SAGE files installed by the launcher. Existing or modified files are preserved. The release package includes SAGE's GPL-3.0 license, source archive, and SHA-256 checksums.

## Build and tests

On Windows with the .NET 10 SDK, run:

```powershell
.\scripts\Publish-Launcher.ps1

dotnet test .\tools\RA3SkirmishLauncher.Tests -c Release
dotnet run --project .\tools\EpicWar.Automation.Tests -c Release -- . --services-only
dotnet run --project .\tools\EpicWar.NativePatch.Tests -c Release -- .
```

GitHub Actions runs these checks and builds a release package on each push. CI does not start the game. The package manifest remains marked for runtime verification until real-game checks are completed.