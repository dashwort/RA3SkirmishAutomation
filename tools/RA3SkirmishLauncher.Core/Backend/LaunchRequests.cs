using RA3SkirmishLauncher.Core.Mods;
using RA3SkirmishLauncher.Core.Settings;

namespace RA3SkirmishLauncher.Core.Backend
{
    public static class LaunchRequests
    {
        public static BackendRequest Create(GameChoice choice, LauncherSettings settings, string? preset, bool? memoryEnabled)
        {
            var options = new Dictionary<string, string?>
            {
                ["game-root"] = settings.GameRoot, ["backup-root"] = settings.BackupDirectory
            };
            if (memoryEnabled.HasValue) { options["large-address-aware"] = memoryEnabled.Value ? "on" : "off"; }
            if (choice.Route == LaunchRoute.ModMenu)
            {
                options["base-skudef"] = choice.SkuDef;
                return new BackendRequest("launch-mod-menu", options);
            }
            if (string.IsNullOrWhiteSpace(preset) || string.IsNullOrWhiteSpace(settings.Profile))
            {
                throw new ArgumentException("Choose a profile and a skirmish preset.");
            }
            options["profile"] = settings.Profile;
            options["preset"] = preset;
            options["remember-preset"] = Path.Combine(settings.PresetDirectory, "default.ini");
            if (settings.IsolateMaps) { options["isolate-incompatible-maps"] = null; }
            if (choice.Route == LaunchRoute.NativeSkirmish) { return new BackendRequest("launch-skirmish", options); }
            options["base-skudef"] = choice.SkuDef;
            options["mod-directory"] = Path.GetDirectoryName(choice.SkuDef);
            options["experimental"] = null;
            return new BackendRequest("launch-skirmish", options);
        }

    }
}
