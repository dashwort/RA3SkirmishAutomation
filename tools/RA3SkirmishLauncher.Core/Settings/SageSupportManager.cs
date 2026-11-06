using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RA3SkirmishLauncher.Core.Settings
{
    public sealed class SageSupportManager : ISageSupportManager
    {
        private static readonly string[] FileNames = ["d3d9.dll", "CnCFpsUnlocker.dll", "SAGEUnlocked.ini"];
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly string assetDirectory;
        private readonly string ownershipFile;

        public SageSupportManager(string assetDirectory, string ownershipFile)
        {
            this.assetDirectory = Path.GetFullPath(assetDirectory);
            this.ownershipFile = Path.GetFullPath(ownershipFile);
        }

        public string Apply(string gameRoot, bool enabled)
        {
            if (Directory.Exists(ownershipFile))
            {
                throw new IOException($"SAGE ownership path is a directory: {ownershipFile}");
            }

            var state = LoadState();
            if (!enabled) { return Disable(gameRoot, state); }

            var files = LoadAssets();
            var root = NormalizeRoot(gameRoot);
            var dataDirectory = GetSupportedDataDirectory(root);
            var current = state.Installations.FirstOrDefault(item => SamePath(item.GameRoot, root));
            if (current == null)
            {
                EnsureNoUnmanagedConflicts(dataDirectory);
                current = new ManagedInstallation
                {
                    GameRoot = root,
                    FileHashes = files.ToDictionary(item => item.Name, item => item.Hash, StringComparer.OrdinalIgnoreCase)
                };
                InstallFiles(dataDirectory, files);
                state.Installations.Add(current);
                try { SaveState(state); }
                catch (Exception saveError)
                {
                    try { RemoveManagedFiles(dataDirectory, current, out _); }
                    catch (Exception rollbackError)
                    {
                        throw new AggregateException("Could not save SAGE ownership or roll back its files.", saveError, rollbackError);
                    }
                    throw;
                }
            }
            else
            {
                VerifyManagedFiles(dataDirectory, current, files);
            }

            var preservedOldFiles = false;
            foreach (var old in state.Installations.Where(item => !SamePath(item.GameRoot, root)).ToArray())
            {
                if (!TryGetSupportedDataDirectory(old.GameRoot, out var oldData))
                {
                    preservedOldFiles = true;
                    continue;
                }
                RemoveManagedFiles(oldData, old, out var preserved);
                preservedOldFiles |= preserved;
            }

            state.Installations = [current];
            SaveState(state);
            return preservedOldFiles
                ? "SAGE Unlocked 60 FPS is enabled. Changed files from the previous game folder were preserved."
                : "SAGE Unlocked 60 FPS is enabled.";
        }

        private string Disable(string gameRoot, OwnershipManifest state)
        {
            var preservedFiles = false;
            foreach (var installation in state.Installations)
            {
                if (!TryGetSupportedDataDirectory(installation.GameRoot, out var dataDirectory))
                {
                    preservedFiles = true;
                    continue;
                }
                RemoveManagedFiles(dataDirectory, installation, out var preserved);
                preservedFiles |= preserved;
            }

            if (File.Exists(ownershipFile)) { File.Delete(ownershipFile); }
            if (!string.IsNullOrWhiteSpace(gameRoot) && TryGetOptionalDataDirectory(gameRoot, out var currentData) &&
                FileNames.Any(name => File.Exists(Path.Combine(currentData, name)) || Directory.Exists(Path.Combine(currentData, name))))
            {
                preservedFiles = true;
            }

            return preservedFiles
                ? "SAGE setting is off. Existing or changed SAGE files were left untouched."
                : "SAGE Unlocked 60 FPS is off.";
        }

        private IReadOnlyList<AssetFile> LoadAssets()
        {
            var files = FileNames.Select(name =>
            {
                var path = Path.Combine(assetDirectory, name);
                if (!File.Exists(path)) { throw new FileNotFoundException($"Bundled SAGE file is missing: {name}", path); }
                var bytes = File.ReadAllBytes(path);
                return new AssetFile(name, bytes, Hash(bytes));
            }).ToArray();
            var config = System.Text.Encoding.UTF8.GetString(files.Single(item => item.Name == "SAGEUnlocked.ini").Bytes);
            if (!Regex.IsMatch(config, @"(?im)^\s*fps\s*=\s*60\s*(?:;.*)?$"))
            {
                throw new InvalidDataException("The bundled SAGE configuration must select 60 FPS.");
            }
            return files;
        }

        private OwnershipManifest LoadState()
        {
            if (!File.Exists(ownershipFile)) { return new OwnershipManifest(); }
            try
            {
                var state = JsonSerializer.Deserialize<OwnershipManifest>(File.ReadAllText(ownershipFile), JsonOptions)
                    ?? throw new InvalidDataException("SAGE ownership record is empty.");
                if (state.SchemaVersion != 1 || state.Installations == null)
                {
                    throw new InvalidDataException("SAGE ownership record has an unsupported format.");
                }
                foreach (var installation in state.Installations)
                {
                    installation.GameRoot = NormalizeRoot(installation.GameRoot);
                    if (installation.FileHashes == null || FileNames.Any(name => !installation.FileHashes.ContainsKey(name)) ||
                        installation.FileHashes.Keys.Any(name => !FileNames.Contains(name, StringComparer.OrdinalIgnoreCase)) ||
                        installation.FileHashes.Values.Any(value => !Regex.IsMatch(value, "^[0-9a-fA-F]{64}$")))
                    {
                        throw new InvalidDataException("SAGE ownership record contains invalid file hashes.");
                    }
                }
                return state;
            }
            catch (JsonException error)
            {
                throw new InvalidDataException("SAGE ownership record is invalid.", error);
            }
        }

        private void SaveState(OwnershipManifest state)
        {
            if (Directory.Exists(ownershipFile)) { throw new IOException($"SAGE ownership path is a directory: {ownershipFile}"); }
            var parent = Path.GetDirectoryName(ownershipFile)!;
            Directory.CreateDirectory(parent);
            var temporary = ownershipFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(state, JsonOptions));
                File.Move(temporary, ownershipFile, true);
            }
            finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
        }

        private static void VerifyManagedFiles(string dataDirectory, ManagedInstallation installation, IReadOnlyList<AssetFile> files)
        {
            foreach (var file in files)
            {
                var path = Path.Combine(dataDirectory, file.Name);
                if (!installation.FileHashes.TryGetValue(file.Name, out var ownedHash) ||
                    !ownedHash.Equals(file.Hash, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(path) || !Hash(File.ReadAllBytes(path)).Equals(ownedHash, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException($"Managed SAGE file changed or is missing: {path}. Resolve it before applying settings.");
                }
            }
        }

        private static void EnsureNoUnmanagedConflicts(string dataDirectory)
        {
            foreach (var name in FileNames)
            {
                var path = Path.Combine(dataDirectory, name);
                if (File.Exists(path) || Directory.Exists(path))
                {
                    throw new InvalidOperationException($"Cannot enable SAGE because this file already exists and is not launcher-managed: {path}");
                }
            }
        }

        private static void InstallFiles(string dataDirectory, IReadOnlyList<AssetFile> files)
        {
            var installed = new List<AssetFile>();
            var temporaryFiles = new List<string>();
            try
            {
                foreach (var file in files)
                {
                    var temporary = Path.Combine(dataDirectory, ".sage-" + Guid.NewGuid().ToString("N") + ".tmp");
                    temporaryFiles.Add(temporary);
                    File.WriteAllBytes(temporary, file.Bytes);
                    var target = Path.Combine(dataDirectory, file.Name);
                    File.Move(temporary, target);
                    temporaryFiles.Remove(temporary);
                    installed.Add(file);
                }
            }
            catch (Exception installError)
            {
                var rollbackErrors = new List<Exception>();
                foreach (var file in installed.AsEnumerable().Reverse())
                {
                    try
                    {
                        var path = Path.Combine(dataDirectory, file.Name);
                        if (File.Exists(path) && Hash(File.ReadAllBytes(path)).Equals(file.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(path);
                        }
                    }
                    catch (Exception error) { rollbackErrors.Add(error); }
                }
                if (rollbackErrors.Count > 0)
                {
                    throw new AggregateException("SAGE installation failed and rollback was incomplete.", [installError, .. rollbackErrors]);
                }
                throw;
            }
            finally
            {
                foreach (var temporary in temporaryFiles)
                {
                    if (File.Exists(temporary)) { File.Delete(temporary); }
                }
            }
        }

        private static void RemoveManagedFiles(string dataDirectory, ManagedInstallation installation, out bool preserved)
        {
            preserved = false;
            foreach (var name in FileNames)
            {
                var path = Path.Combine(dataDirectory, name);
                if (Directory.Exists(path)) { preserved = true; continue; }
                if (!File.Exists(path)) { continue; }
                var currentHash = Hash(File.ReadAllBytes(path));
                if (installation.FileHashes.TryGetValue(name, out var ownedHash) &&
                    currentHash.Equals(ownedHash, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(path);
                }
                else { preserved = true; }
            }
        }

        private static string GetSupportedDataDirectory(string root)
        {
            if (!TryGetSupportedDataDirectory(root, out var dataDirectory))
            {
                throw new DirectoryNotFoundException($"Select a Red Alert 3 installation with a supported Data folder: {root}");
            }
            return dataDirectory;
        }

        private static bool TryGetSupportedDataDirectory(string root, out string dataDirectory)
        {
            dataDirectory = Path.Combine(NormalizeRoot(root), "Data");
            return Directory.Exists(dataDirectory) &&
                (File.Exists(Path.Combine(dataDirectory, "ra3_1.12.game")) || File.Exists(Path.Combine(dataDirectory, "ra3_1.13.game")));
        }

        private static bool TryGetOptionalDataDirectory(string root, out string dataDirectory)
        {
            try
            {
                var normalized = NormalizeRoot(root);
                dataDirectory = Path.Combine(normalized, "Data");
                return Directory.Exists(dataDirectory);
            }
            catch (ArgumentException)
            {
                dataDirectory = "";
                return false;
            }
            catch (NotSupportedException)
            {
                dataDirectory = "";
                return false;
            }
        }

        private static string NormalizeRoot(string root)
        {
            if (string.IsNullOrWhiteSpace(root)) { throw new ArgumentException("The RA3 game folder is required.", nameof(root)); }
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }

        private static bool SamePath(string left, string right) =>
            StringComparer.OrdinalIgnoreCase.Equals(NormalizeRoot(left), NormalizeRoot(right));

        private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

        private sealed record AssetFile(string Name, byte[] Bytes, string Hash);

        private sealed class OwnershipManifest
        {
            public OwnershipManifest() { }
            public int SchemaVersion { get; set; } = 1;
            public List<ManagedInstallation> Installations { get; set; } = [];
        }

        private sealed class ManagedInstallation
        {
            public ManagedInstallation() { }
            public string GameRoot { get; set; } = "";
            public Dictionary<string, string> FileHashes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        }
    }
}
