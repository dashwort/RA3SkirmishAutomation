using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;

namespace EpicWar.Automation;

internal static class TestGame
{
    internal sealed record Outcome(int ExitCode, object Report);
    private sealed record Control(int ProcessId,DateTime StartedAtUtc);

    // A finite, machine-readable installed-game smoke test. The normal launcher
    // remains supervised until the user closes it; this command owns its stop.
    internal static Outcome Run(Dictionary<string,string?> options, CancellationToken cancellationToken)
    {
        options["start-match"]="true";
        options["test-seconds"]=options.GetValueOrDefault("test-seconds") ?? "10";
        var started=DateTimeOffset.UtcNow;
        var dryRun=options.GetValueOrDefault("dry-run")=="true";
        var evidence=Path.GetFullPath(options.GetValueOrDefault("evidence-root") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"EpicWar.Automation","Tests",
            $"run-{started:yyyyMMddTHHmmssfff}-{Guid.NewGuid():N}"));
        options["evidence-root"]=evidence;
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var watcher=new System.Threading.Timer(_=>
        {
            try { if(File.Exists(Path.Combine(evidence,"cancel.request")))cancellation.Cancel(); }
            catch(ObjectDisposedException) { /* A final timer callback raced supervisor shutdown. */ }
        },null,Timeout.Infinite,Timeout.Infinite);
        cancellationToken=cancellation.Token;
        object? plan=null, session=null;
        Exception? error=null;
        string? ini=null, originalIniHash=null, replay=null, replayHash=null;
        Dictionary<string,string>? mapHashes=null;
        string? mapRoot=null;
        bool? profileRestored=null, mapsRestored=null, replayRestored=null;
        bool evidenceCreated=false;
        using var exclusion=LaunchExclusion.Acquire();
        try
        {
            // Resolve and validate everything before acquiring any user files.
            var preview=new Dictionary<string,string?>(options) { ["dry-run"]="true" };
            var resolved=JsonSerializer.SerializeToElement(LaunchPreset.Launch(preview,true,cancellationToken));
            plan=resolved;
            if(dryRun) return new Outcome(0,new {schemaVersion=1,status="dry-run",exitCode=0,plan});
            LaunchPreset.EnsureGameClosed();
            cancellationToken.ThrowIfCancellationRequested();
            var profileDirectory=resolved.GetProperty("profileDirectory").GetString()!;
            mapRoot=resolved.GetProperty("mapRoot").GetString()!;
            replay=Path.GetFullPath(options.GetValueOrDefault("replay-path") ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),"Red Alert 3","Replays","Last Replay.RA3Replay"));
            foreach(var root in new[]{profileDirectory,mapRoot,resolved.GetProperty("modDirectory").GetString()!,resolved.GetProperty("gameDirectory").GetString()!})
                if(IsInside(evidence,root)) throw new ArgumentException("Test evidence must be outside the profile, maps, mod and game folders.");
            if(IsInside(replay,evidence)) throw new ArgumentException("The replay path must be outside the test evidence folder.");
            if(Directory.Exists(evidence) || File.Exists(evidence)) throw new ArgumentException("Use a new --evidence-root for each test to prevent stale evidence.");
            Directory.CreateDirectory(evidence); evidenceCreated=true;
            using(var self=Process.GetCurrentProcess())
                File.WriteAllText(Path.Combine(evidence,"control.json"),JsonSerializer.Serialize(new Control(self.Id,self.StartTime.ToUniversalTime())));
            watcher.Change(0,250);
            File.WriteAllText(Path.Combine(evidence,"plan.json"),resolved.GetRawText());
            ini=Path.Combine(profileDirectory,"Skirmish.ini");
            originalIniHash=HashOrNull(ini);
            if(originalIniHash!=null) File.Copy(ini,Path.Combine(evidence,"Skirmish.before.ini"));
            replayHash=HashOrNull(replay);
            if(replayHash!=null) File.Copy(replay,Path.Combine(evidence,"replay.before.RA3Replay"));
            mapHashes=Directory.Exists(mapRoot) ? MapCompatibilityLease.Hashes(mapRoot) : new(StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(Path.Combine(evidence,"preservation-before.json"),JsonSerializer.Serialize(new {ini,originalIniHash,replay,replayHash,mapRoot,mapHashes}));
            ForegroundGame.Log("test-preflight-complete",Environment.ProcessId);
            session=LaunchPreset.Launch(options,true,cancellationToken);
        }
        catch(Exception ex) { error=ex; }
        finally
        {
            // LaunchPreset must finish stopping its owned engine before replay
            // restoration. If an engine survives, leave backups intact for recovery.
            if(mapHashes!=null)
            {
                try
                {
                    LaunchPreset.EnsureGameClosed();
                    profileRestored=HashOrNull(ini!)==originalIniHash;
                    var after=Directory.Exists(mapRoot) ? MapCompatibilityLease.Hashes(mapRoot!) : new(StringComparer.OrdinalIgnoreCase);
                    mapsRestored=SameHashes(mapHashes,after);
                    RestoreReplay(replay!,replayHash,evidence);
                    replayRestored=HashOrNull(replay!)==replayHash;
                    if(profileRestored!=true || mapsRestored!=true || replayRestored!=true)
                        throw new IOException("Test restoration verification failed. Inspect the preserved backups and map recovery journal.");
                }
                catch(Exception cleanupError) { error=error==null ? cleanupError : new AggregateException(error,cleanupError); }
            }
        }
        var exitCode=error==null ? 0 : error is OperationCanceledException ? 130 : error is ArgumentException ? 2 : 1;
        var report=new {schemaVersion=1,status=exitCode==0 ? "test-passed" : exitCode==130 ? "test-cancelled" : "test-failed",exitCode,
            startedAtUtc=started,completedAtUtc=DateTimeOffset.UtcNow,evidenceDirectory=evidence,
            matchStartVerified=session!=null && error==null,aiActionsVerified=false,
            preservation=new {profileRestored,mapsRestored,replayRestored,mapFiles=mapHashes?.Count},
            error=error==null ? null : new {type=error.GetType().Name,message=error.Message},plan,session};
        if(evidenceCreated) WriteReport(Path.Combine(evidence,"result.json"),report);
        return new Outcome(exitCode,report);
    }

    internal static bool SameHashes(Dictionary<string,string> expected, Dictionary<string,string> actual) =>
        expected.Count==actual.Count && expected.All(p=>actual.TryGetValue(p.Key,out var hash)&&hash==p.Value);

    internal static object RequestCancellation(string directory)
    {
        directory=Path.GetFullPath(directory);
        var control=JsonSerializer.Deserialize<Control>(File.ReadAllText(Path.Combine(directory,"control.json")))
            ?? throw new InvalidDataException("Invalid test control record.");
        if(File.Exists(Path.Combine(directory,"result.json")))return new {schemaVersion=1,status="test-already-ended",exitCode=0,evidenceDirectory=directory};
        using var process=Process.GetProcessById(control.ProcessId);
        if(process.HasExited || process.StartTime.ToUniversalTime()!=control.StartedAtUtc)
            throw new InvalidOperationException("Test supervisor is no longer running; inspect its backups and recovery journal.");
        var request=Path.Combine(directory,"cancel.request");
        if(!File.Exists(request))File.WriteAllText(request,DateTimeOffset.UtcNow.ToString("o"));
        return new {schemaVersion=1,status="test-cancellation-requested",exitCode=0,evidenceDirectory=directory};
    }

    private static bool IsInside(string path,string root) =>
        path.Equals(root,StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);

    internal static string? HashOrNull(string path)
    {
        if(!File.Exists(path))return null;
        using var stream=File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void RestoreReplay(string replay,string? beforeHash,string evidence)
    {
        var afterHash=HashOrNull(replay);
        if(afterHash==beforeHash)return;
        if(afterHash!=null)File.Copy(replay,Path.Combine(evidence,"generated.RA3Replay"),false);
        if(beforeHash==null)File.Delete(replay);
        else
        {
            var backup=Path.Combine(evidence,"replay.before.RA3Replay");
            if(HashOrNull(backup)!=beforeHash)throw new IOException("Replay backup integrity check failed; original replay was not overwritten.");
            File.Copy(backup,replay,true);
        }
    }

    private static void WriteReport(string path,object report)
    {
        var temporary=path+".new";
        File.WriteAllText(temporary,JsonSerializer.Serialize(report,new JsonSerializerOptions{WriteIndented=true}));
        File.Move(temporary,path,false);
    }
}
