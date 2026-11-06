using System.Security.Cryptography;

namespace EpicWar.Automation;

internal sealed record NativePatchCommandStatus(int schemaVersion, string status, string engineState,
    string enginePath, string? engineSha256, string requiredOriginalSha256,
    string patchAvailability, string? patchId, string backupRoot, NativePatchStatus? transaction,
    string message);

internal static class NativePatchCommands
{
    private const string Unavailable = "No reviewed production native patch is available. " +
        "Complete preset settings and the production package must pass acceptance before installation is enabled.";

    internal static object Run(string action, IReadOnlyDictionary<string, string?> options)
        => Run(action, options, NativePatchCatalog.Current);

    internal static object Run(string action, IReadOnlyDictionary<string, string?> options, NativePatchDefinition? definition)
    {
        if (action is not ("status" or "install" or "restore" or "recover"))
            throw new ArgumentException($"Unknown native-patch action: {action}. Use status, install, restore or recover.");

        var gameRoot = ResolveGameRoot(Value(options, "game-root"));
        var backupRoot = ResolveBackupRoot(Value(options, "backup-root"));
        if (action == "status") return Inspect(gameRoot, backupRoot, definition);

        // Deliberately refuse before acquiring a lock, creating directories,
        // writing a journal or touching either the engine or user profiles.
        definition = RequireDefinition(definition);
        var enginePath = EnginePath(gameRoot);
        if (!File.Exists(enginePath))
            throw new FileNotFoundException("The RA3 1.12 engine was not found in the game's Data directory.", enginePath);
        var installer = new NativePatchInstaller(enginePath, backupRoot, definition.Identity);
        NativePatchStatus result;
        if (action == "install")
        {
            var status = installer.Inspect();
            var source = status.State is "original" or "original-large-address-aware" ? enginePath :
                status.BackupVerified ? status.BackupPath :
                throw new InvalidDataException("The engine is unsupported and no verified original backup is available. Its bytes were preserved.");
            var sourceBytes = File.ReadAllBytes(source);
            if (status.State == "original-large-address-aware")
            {
                sourceBytes = LargeAddressAwarePatch.Apply(sourceBytes, false);
            }
            var candidate = NativePatchBuilder.Build(sourceBytes, definition.Identity, definition.Edits);
            if (status.LargeAddressAware)
            {
                candidate = LargeAddressAwarePatch.Apply(candidate, true);
            }
            result = installer.Install(candidate);
        }
        else result = action == "restore" ? installer.Restore() : installer.Recover();
        return Describe(gameRoot, backupRoot, definition, result.EngineSha256, result);
    }

    internal static NativePatchStatus VerifyInstalled(string gameRoot, string? backupRoot = null)
    {
        var definition = RequireDefinition(NativePatchCatalog.Current);
        var status = new NativePatchInstaller(EnginePath(gameRoot), ResolveBackupRoot(backupRoot), definition.Identity).Inspect();
        if (status.State != "installed")
            throw new InvalidDataException("The installed engine does not match the reviewed native patch. Run native-patch status, then install the supported patch.");
        if (!status.BackupVerified)
            throw new InvalidDataException("The native patch's original-engine backup is missing or corrupt. Launch was refused to preserve restoration support.");
        if (status.TransactionState != "installed")
            throw new InvalidDataException("The native patch transaction is incomplete. Run native-patch recover before launching.");
        return status;
    }

    internal static string ResolveGameRoot(string? selected)
    {
        if (selected is not null) return Path.GetFullPath(selected);
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Could not resolve Program Files (x86); pass --game-root.");
        return Path.Combine(root, "Steam", "steamapps", "common", "Command and Conquer Red Alert 3");
    }

    internal static string ResolveBackupRoot(string? selected = null)
    {
        if (selected is not null) return Path.GetFullPath(selected);
        return Path.Combine(NativeAutomationStorage.Root, "NativeBackups");
    }

    internal static string EnginePath(string gameRoot) => Path.Combine(Path.GetFullPath(gameRoot), "Data", "ra3_1.12.game");

    // Definition injection is internal to permit inert fixture tests. The CLI
    // always selects NativePatchCatalog.Current and never accepts external recipes.
    internal static NativePatchCommandStatus Inspect(string gameRoot, string backupRoot, NativePatchDefinition? definition)
    {
        gameRoot = Path.GetFullPath(gameRoot);
        backupRoot = Path.GetFullPath(backupRoot);
        var path = EnginePath(gameRoot);
        NativePatchStatus? transaction = definition is null ? null :
            new NativePatchInstaller(path, backupRoot, definition.Identity).Inspect();
        var hash = transaction?.EngineSha256;
        if (transaction is null && File.Exists(path))
        {
            using var stream = File.OpenRead(path);
            hash = Convert.ToHexString(SHA256.HashData(stream));
        }
        return Describe(gameRoot, backupRoot, definition, hash, transaction);
    }

    internal static string Classify(string? hash, NativePatchDefinition? definition) =>
        hash is null ? "missing" :
        hash.Equals(definition?.Identity.OriginalSha256 ?? NativePatchCatalog.OriginalSha256, StringComparison.OrdinalIgnoreCase) ? "supported-original" :
        definition is not null && definition.Identity.HasNativePatch(hash) ? "installed" :
        definition is not null && definition.Identity.HasLargeAddressAware(hash) ? "supported-original-large-address-aware" : "unknown";

    private static NativePatchCommandStatus Describe(string gameRoot, string backupRoot, NativePatchDefinition? definition,
        string? hash, NativePatchStatus? transaction) => new(1, "native-patch-status", Classify(hash, definition),
        EnginePath(gameRoot), hash, definition?.Identity.OriginalSha256 ?? NativePatchCatalog.OriginalSha256,
        definition is null ? "unavailable" : "available", definition?.Identity.PatchId, backupRoot, transaction,
        definition is null ? Unavailable : "Only the embedded reviewed recipe is accepted; backup and transaction state are reported separately.");

    private static NativePatchDefinition RequireDefinition(NativePatchDefinition? definition) => definition ??
        throw new InvalidOperationException(Unavailable);

    private static string? Value(IReadOnlyDictionary<string, string?> options, string name) =>
        options.TryGetValue(name, out var value) ? value : null;
}
