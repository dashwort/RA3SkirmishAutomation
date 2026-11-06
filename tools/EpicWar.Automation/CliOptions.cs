namespace EpicWar.Automation;

internal static class CliOptions
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    { "dry-run", "window", "overwrite", "experimental", "start-match", "isolate-incompatible-maps" };

    internal static Dictionary<string,string?> Parse(string command, IEnumerable<string> arguments)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "profile", "profile-root", "preset", "name" };
        if(command is "native-patch" or "memory-patch") allowed = new(StringComparer.OrdinalIgnoreCase) { "game-root", "backup-root" };
        else if(command == "launch-mod-menu") allowed = new(StringComparer.OrdinalIgnoreCase)
            { "game-root", "base-skudef", "backup-root", "large-address-aware", "dry-run" };
        else if(command == "launch-skirmish") allowed = new(StringComparer.OrdinalIgnoreCase) {
            "profile", "profile-root", "preset", "game-root", "backup-root", "config-skudef", "width", "height", "dry-run",
            "runtime-root", "map-root", "isolate-incompatible-maps", "setup-timeout", "load-timeout", "replay-path", "test-seconds",
            "large-address-aware", "remember-preset", "mod-directory", "base-skudef", "experimental" };
        else if(command == "capture-preset") allowed.UnionWith(["output", "overwrite"]);
        else
        {
            allowed.UnionWith(["game-root", "engine-image", "config-skudef", "mod-directory", "base-skudef", "overlay",
                "width", "height", "dry-run", "experimental", "window", "start-match", "isolate-incompatible-maps",
                "map-root", "runtime-root", "evidence-root", "setup-timeout", "load-timeout", "backup-root",
                "large-address-aware", "remember-preset"]);
            if(command == "test-game") allowed.UnionWith(["test-seconds", "replay-path"]);
        }
        var result = new Dictionary<string,string?>(StringComparer.OrdinalIgnoreCase);
        var values = arguments.ToArray();
        for(int i=0; i<values.Length; i++)
        {
            var key = values[i];
            if(!key.StartsWith("--", StringComparison.Ordinal) || !allowed.Contains(key[2..]))
                throw new ArgumentException($"Unknown option for {command}: {key}");
            key=key[2..];
            if(result.ContainsKey(key)) throw new ArgumentException($"Duplicate option --{key}.");
            if(Flags.Contains(key)) result[key]="true";
            else
            {
                if(++i>=values.Length || values[i].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(values[i]))
                    throw new ArgumentException($"Missing value for --{key}.");
                result[key]=values[i];
            }
        }
        return result;
    }
}
