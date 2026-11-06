namespace EpicWar.Automation
{
    internal static class MemoryPatchCommands
    {
        public static object Run(string action, IReadOnlyDictionary<string, string?> options)
        {
            var game = NativePatchCommands.ResolveGameRoot(options.GetValueOrDefault("game-root"));
            var backup = NativePatchCommands.ResolveBackupRoot(options.GetValueOrDefault("backup-root"));
            var definition = NativePatchCatalog.Current ?? throw new InvalidOperationException("The engine catalog is unavailable.");
            var installer = new NativePatchInstaller(NativePatchCommands.EnginePath(game), backup, definition.Identity);
            var state = action switch
            {
                "status" => installer.Inspect(),
                "enable" => installer.SetMemory(true),
                "disable" => installer.SetMemory(false),
                _ => throw new ArgumentException("Choose memory-patch status, enable or disable.")
            };
            return new { schemaVersion = 1, status = "memory-patch-status", transaction = state };
        }

        public static bool? RequestedState(IReadOnlyDictionary<string, string?> options)
        {
            return options.GetValueOrDefault("large-address-aware") switch
            {
                null => null,
                "on" => true,
                "off" => false,
                _ => throw new ArgumentException("--large-address-aware must be on or off.")
            };
        }

        public static void ApplyUnderLease(IReadOnlyDictionary<string, string?> options, string gameRoot)
        {
            var requested = RequestedState(options);
            if (!requested.HasValue) { return; }
            if (options.ContainsKey("engine-image"))
            {
                throw new ArgumentException("4 GB changes are supported only for the installed engine.");
            }
            var definition = NativePatchCatalog.Current ?? throw new InvalidOperationException("The engine catalog is unavailable.");
            var backup = NativePatchCommands.ResolveBackupRoot(options.GetValueOrDefault("backup-root"));
            var installer = new NativePatchInstaller(NativePatchCommands.EnginePath(gameRoot), backup, definition.Identity);
            installer.SetMemoryUnderLease(requested.Value);
        }
    }
}
