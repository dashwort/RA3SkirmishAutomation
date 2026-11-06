namespace RA3SkirmishLauncher.Core.Mods
{
    public sealed class GameChoice
    {
        public GameChoice(string name, string? skuDef, LaunchRoute route)
        {
            Name = name;
            SkuDef = skuDef;
            Route = route;
        }

        public string Name { get; }
        public string? SkuDef { get; }
        public LaunchRoute Route { get; }
        public string Id => SkuDef ?? "base-game";
        public bool SupportsPresets => Route != LaunchRoute.ModMenu;
        public string Description => Route switch
        {
            LaunchRoute.NativeSkirmish => "Start a base-game skirmish directly from your saved preset.",
            LaunchRoute.EpicWarSkirmish => "Start Epic War directly from your saved preset using native skirmish support.",
            _ => "Open this mod normally. Preset launching is unavailable for this mod."
        };
        public override string ToString() => Name;
    }
}
