namespace RA3SkirmishLauncher.Core.Mods
{
    public interface IModDiscovery
    {
        IReadOnlyList<GameChoice> Discover(IEnumerable<string> roots);
    }
}
