# Launcher guide

Run `RA3SkirmishLauncher.exe` from the complete package. Keep the package files together.

## Start a game

1. Select Red Alert 3 or an installed mod.
2. Choose a saved preset when the direct-launch route is available.
3. Select **Launch game** and wait for the status to report that the game has started.
4. Leave the launcher open until Red Alert 3 closes and cleanup finishes.

The launcher uses the supported patched game binary for direct launch with Red Alert 3 and supported Epic War 5.4 installations. Other mods use the standard menu launch.

## Settings

Set the game and profile locations, preset folder, backup location, and any additional mod folders. Optional settings include map compatibility, theme, and SAGE Unlocked. Select **Apply settings** to save changes and apply support options.

Support for the patched game binary can be installed or restored from the **Recovery** tab. The direct route requires a supported game version and an active, unlocked Windows desktop.

## Presets

The preset list uses the shared `Presets` folder beside the launcher. Import a preset or save the current one under a new name. After a successful direct launch, the launcher can update `default.ini` to the preset used for that game. Failed or cancelled launches leave it unchanged.

## SAGE Unlocked 60 FPS

SAGE is off by default. Turn on **SAGE Unlocked 60 FPS**, select **Apply settings**, and restart the game. It applies to both launch routes and targets up to 60 FPS and keeps normal game speed. The display's refresh rate can limit the actual frame rate.

Turn it off and apply settings to remove unchanged SAGE files installed by the launcher. Files that existed beforehand or were changed afterward are left in place.

## If a launch is refused

Check the game folder, active profile, and the status message in the launcher. Direct launch is refused when Windows is locked or disconnected, or when the game files do not match a supported version. Use the standard route for other mods.