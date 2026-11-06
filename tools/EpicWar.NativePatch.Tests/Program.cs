using System.Security.Cryptography;
using System.Text.Json;
using EpicWar.Automation;

var failures = new List<string>();
var count = 0;
void Check(bool condition, string name)
{
    count++;
    if (!condition) failures.Add(name);
}
NativePresetTests.Run(Check);
bool Refused(Action action)
{
    try { action(); return false; }
    catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException) { return true; }
}
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

// Every engine is inert fixture data under this new workspace folder. No test
// resolves the installed Steam game or user's RA3 profile, and no target runs.
var repo = Path.GetFullPath(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "../../../../.."));
var root = Path.Combine(repo, ".build", "native-patch-command-tests", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var original = Enumerable.Range(0, 512).Select(i => (byte)(i * 17 + 31)).ToArray();
var patched = original.ToArray();
patched[64] ^= 0x5a;
var identity = new NativePatchIdentity("inert-command-fixture-v1", Hash(original), Hash(patched));
var definition = new NativePatchDefinition(identity, [new(64, [original[64]], [patched[64]])]);
string MakeGame(string name, byte[]? bytes)
{
    var game = Path.Combine(root, name, "game");
    Directory.CreateDirectory(Path.Combine(game, "Data"));
    if (bytes is not null) File.WriteAllBytes(NativePatchCommands.EnginePath(game), bytes);
    return game;
}
string Backups(string name) => Path.Combine(root, name, "backups");
NativePatchInstaller Installer(string game, string backup, Action<string>? checkpoint = null) =>
    new(NativePatchCommands.EnginePath(game), backup, identity, () => { }, () => new NoopLease(), checkpoint);

Check(NativePatchCatalog.Current?.Identity.PatchedSha256 == NativePatchCatalog.PatchedSha256 &&
    NativePatchCatalog.Current.Edits.Count == 13, "Reviewed ABI 4 recipe is embedded with its pinned identity");
Check(NativePatchCommands.Classify(NativePatchCatalog.OriginalSha256, null) == "supported-original", "Pinned 1.12 source identity is supported");
Check(NativePatchCommands.Classify(null, null) == "missing", "Missing engine classification");
Check(NativePatchCommands.Classify(new string('A', 64), null) == "unknown", "Unknown hash is never advertised as a native patch");
var missing = MakeGame("missing", null);
Check(NativePatchCommands.Inspect(missing, Backups("missing"), null).engineState == "missing", "Missing status is inspectable");
var unknown = MakeGame("unknown", original);
var unknownStatus = NativePatchCommands.Inspect(unknown, Backups("unknown"), null);
Check(unknownStatus.engineState == "unknown" && unknownStatus.patchAvailability == "unavailable" && unknownStatus.transaction is null,
    "No catalog does not fabricate a patched identity or transaction");
Check(!Directory.Exists(Backups("unknown")), "Status creates no backup directories");

var originalGame = MakeGame("original", original);
var originalStatus = NativePatchCommands.Inspect(originalGame, Backups("original"), definition);
Check(originalStatus.engineState == "supported-original" && originalStatus.transaction?.State == "original", "Fixture recipe recognizes exact original bytes");
var patchedGame = MakeGame("patched", patched);
var patchedStatus = NativePatchCommands.Inspect(patchedGame, Backups("patched"), definition);
Check(patchedStatus.engineState == "installed" && patchedStatus.transaction?.BackupVerified == false, "Patched identity does not imply verified restoration");
Check(!Directory.Exists(Backups("patched")), "Patched status remains read-only");

var noCatalogGame = MakeGame("catalog-refusal", original);
var noCatalogBackup = Backups("catalog-refusal");
var options = new Dictionary<string, string?> { ["game-root"] = noCatalogGame, ["backup-root"] = noCatalogBackup };
foreach (var action in new[] { "install", "restore", "recover" })
{
    Check(Refused(() => NativePatchCommands.Run(action, options, null)), "No catalog refuses " + action);
    Check(File.ReadAllBytes(NativePatchCommands.EnginePath(noCatalogGame)).SequenceEqual(original) && !Directory.Exists(noCatalogBackup),
        "No catalog " + action + " leaves engine and backup tree untouched");
}
Check(Refused(() => NativePatchCommands.VerifyInstalled(noCatalogGame, noCatalogBackup)), "Unproven patch cannot unlock launch");
Check(Refused(() => NativePatchCommands.Run("package", options)), "Arbitrary package action is refused");
Check(NativePatchCommands.ResolveBackupRoot().EndsWith(Path.Combine(".epicwar-automation", "NativeBackups"), StringComparison.OrdinalIgnoreCase),
    "Default backups are in application storage outside the game");

Check(NativePatchBuilder.Build(original, identity, definition.Edits).SequenceEqual(patched), "Reviewed byte recipe reproduces pinned fixture candidate");
Check(Refused(() => NativePatchBuilder.Build(patched, identity, definition.Edits)), "Recipe refuses modified input");
Check(Refused(() => NativePatchBuilder.Build(original, identity, [new(64, [0], [1])])), "Recipe checks displaced original bytes");
Check(Refused(() => NativePatchBuilder.Build(original, identity, [new(64, [original[64]], [0])])), "Recipe checks output SHA-256");

var normalGame = MakeGame("transaction", original);
var normalBackup = Backups("transaction");
var normal = Installer(normalGame, normalBackup);
var installed = normal.Install(patched);
Check(installed.State == "installed" && installed.BackupVerified && installed.TransactionState == "installed", "Install verifies candidate, original backup and journal");
Check(normal.Install(patched).State == "installed", "Repeated install is idempotent");
Check(normal.Restore().State == "original" && File.ReadAllBytes(NativePatchCommands.EnginePath(normalGame)).SequenceEqual(original), "Restore reproduces exact original bytes");
Check(normal.Restore().State == "original", "Repeated restore is idempotent");
Check(Refused(() => new NativePatchInstaller(NativePatchCommands.EnginePath(normalGame), Path.Combine(normalGame, "backups"), identity)),
    "Backups inside game directory are refused");

foreach (var boundary in new[] { "prepared-install", "replaced-install", "prepared-restore", "replaced-restore" })
{
    var game = MakeGame("interrupted-" + boundary, original);
    var backup = Backups("interrupted-" + boundary);
    if (boundary.EndsWith("restore", StringComparison.Ordinal)) Installer(game, backup).Install(patched);
    var interrupted = Installer(game, backup, reached => { if (reached == boundary) throw new IOException("Synthetic interruption"); });
    Check(Refused(() => { if (boundary.EndsWith("restore", StringComparison.Ordinal)) interrupted.Restore(); else interrupted.Install(patched); }),
        boundary + " interruption is reported");
    var recovered = Installer(game, backup).Recover();
    Check(recovered.State == "original" && recovered.EngineSha256 == identity.OriginalSha256, boundary + " recovers exact original");
    Check(Installer(game, backup).Recover().State == "original", boundary + " recovery is repeatable");
}

foreach (var problem in new[] { "backup-missing", "backup-corrupt", "external-update" })
{
    var game = MakeGame(problem, original);
    var backup = Backups(problem);
    var installer = Installer(game, backup);
    var status = installer.Install(patched);
    var external = Enumerable.Repeat((byte)0x73, original.Length).ToArray();
    if (problem == "backup-missing") File.Delete(status.BackupPath);
    else if (problem == "backup-corrupt") File.WriteAllBytes(status.BackupPath, external);
    else File.WriteAllBytes(NativePatchCommands.EnginePath(game), external);
    var before = File.ReadAllBytes(NativePatchCommands.EnginePath(game));
    Check(Refused(() => installer.Restore()), problem + " blocks restoration");
    Check(File.ReadAllBytes(NativePatchCommands.EnginePath(game)).SequenceEqual(before), problem + " preserves installed bytes");
}

var unknownRecoveryGame = MakeGame("unknown-recovery", original);
var unknownRecoveryBackup = Backups("unknown-recovery");
Refused(() => Installer(unknownRecoveryGame, unknownRecoveryBackup, step => { if (step == "replaced-install") throw new IOException("Interrupted"); }).Install(patched));
var unexpected = Enumerable.Repeat((byte)0x2d, original.Length).ToArray();
File.WriteAllBytes(NativePatchCommands.EnginePath(unknownRecoveryGame), unexpected);
Check(Refused(() => Installer(unknownRecoveryGame, unknownRecoveryBackup).Recover()), "Recovery refuses external replacement");
Check(File.ReadAllBytes(NativePatchCommands.EnginePath(unknownRecoveryGame)).SequenceEqual(unexpected), "Recovery preserves external replacement bytes");

Console.WriteLine(JsonSerializer.Serialize(new { status = failures.Count == 0 ? "passed" : "failed", checks = count, failures, fixtureRoot = root }));
return failures.Count == 0 ? 0 : 1;

sealed class NoopLease : IDisposable { public void Dispose() { } }
