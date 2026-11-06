using System.Drawing;
using System.Text.Json;
using EpicWar.Automation;

// A real owned process for exercising cancellation after process creation.
// It receives the fixed engine argument shape from LaunchPreset and has no UI.
if(args.Length>0 && args[0]=="-config" && args.Contains("-modConfig")) { Thread.Sleep(Timeout.Infinite);return; }
var repo=Path.GetFullPath(args[0]);
if(args.Length>1 && args[1]=="--vanilla-skirmish-only")
{
    var vanillaResults=new List<string>();
    VanillaSkirmishTests.Run(repo,(condition,name)=>{if(!condition)throw new Exception(name);vanillaResults.Add(name);});
    LauncherDefaultsTests.Run(repo,(condition,name)=>{if(!condition)throw new Exception(name);vanillaResults.Add(name);});
    Console.WriteLine(JsonSerializer.Serialize(new {passed=vanillaResults.Count,checks=vanillaResults},new JsonSerializerOptions{WriteIndented=true}));
    return;
}
if(args.Length>1 && args[1]=="--native-patch-only")
{
    var nativeResults=new List<string>();
    NativePatchTests.Run(repo,(condition,name)=>{if(!condition)throw new Exception(name);nativeResults.Add(name);});
    Console.WriteLine(JsonSerializer.Serialize(new {passed=nativeResults.Count,checks=nativeResults},new JsonSerializerOptions{WriteIndented=true}));
    return;
}
var servicesOnly = args.Length > 1 && args[1] == "--services-only";
if(args.Length>1 && !servicesOnly){using var actual=new Bitmap(args[1]);Console.WriteLine(JsonSerializer.Serialize(new{classification=GameScreen.Classify(actual),scores=GameScreen.Scores(actual)}));return;}
var results=new List<string>();
void Check(bool condition,string name){if(!condition)throw new Exception(name);results.Add(name);}
var nativeModOptions = CliOptions.Parse("launch-skirmish", [
    "--profile", "dave", "--preset", "fixture.ini", "--mod-directory", "mods",
    "--base-skudef", "RA3 Epic War_5.4.skudef", "--experimental"]);
Check(nativeModOptions["mod-directory"] == "mods" &&
    nativeModOptions["base-skudef"] == "RA3 Epic War_5.4.skudef" &&
    nativeModOptions.ContainsKey("experimental"),
    "Native compatibility route accepts an explicitly gated mod configuration");
var nativeBaseOptions = CliOptions.Parse("launch-skirmish", [
    "--profile", "dave", "--preset", "fixture.ini"]);
Check(!nativeBaseOptions.ContainsKey("mod-directory") && !nativeBaseOptions.ContainsKey("base-skudef"),
    "Native base-game route remains free of mod configuration");
Check(VanillaSkirmish.ModeName(true) == "native-mod-compatibility" &&
    VanillaSkirmish.ModeName(false) == "vanilla-native-skirmish",
    "Native compatibility results use a distinct mode label");
var shippedNavigation = File.ReadAllBytes(BundledNavigation.Extract(Path.Combine(repo, ".build", "navigation-regression")));
byte[] unconditionalMenuNavigation = [0x5A, 0x5A, 0xB9, 0x01, 0xB2, 0x65, 0x59, 0x17, 0x59, 0x17];
Check(shippedNavigation.AsSpan().IndexOf(unconditionalMenuNavigation) < 0,
    "Shipped menu overlay does not retain the unconditional main-menu-to-skirmish action");
Check(System.Text.Encoding.ASCII.GetString(shippedNavigation).Contains("epicWarAutoNavigated", StringComparison.Ordinal),
    "Shipped menu overlay includes the persistent one-shot navigation guard");
using (var alliesHud = new Bitmap(Path.Combine(repo, "tools", "EpicWar.Automation.Tests", "Fixtures", "allies-gameplay.png")))
    Check(GameScreen.Classify(alliesHud) == GameScreenKind.Gameplay,
        "Recognizes a real Allies gameplay HUD after automatic Begin");
using (var blankHud = new Bitmap(1280, 720))
    Check(GameScreen.Classify(blankHud) == GameScreenKind.Unknown,
        "A blank frame cannot satisfy gameplay verification for either HUD");
var menuAction = AptPatch.CreateAction(mainMenu: true);
var menuInstructions = menuAction.Elements().ToArray();
Check(menuInstructions.Where(node => (string?)node.Attribute("str") == "epicWarAutoNavigated")
    .All(node => menuInstructions.TakeWhile(candidate => !ReferenceEquals(candidate, node))
        .LastOrDefault(candidate => (string?)candidate.Attribute("str") is "_global" or "screen")?.Attribute("str")?.Value == "_global"),
    "Main-menu navigation guard persists across menu instances");
using (var stopDuringFocusLoss = new CancellationTokenSource())
{
    _ = Task.Run(() => { Thread.Sleep(20); stopDuringFocusLoss.Cancel(); });
    Check(LaunchPreset.PreferStopAfterFocusLoss(new GameFocusLostException(), stopDuringFocusLoss.Token)
        is OperationCanceledException, "Stop request wins a concurrent focus loss");
}
Bitmap Fixture(string name){using var raw=new Bitmap(Path.Combine(repo,".build","durable-route-20261016",name));return raw.Clone(new Rectangle(1,33,1600,900),raw.PixelFormat);}
if (!servicesOnly)
{
using(var setup=Fixture("setup-0.png"))Check(GameScreen.Classify(setup)==GameScreenKind.Setup,"Recognizes actual clean setup");
using(var scaled=new Bitmap(Path.Combine(repo,".build/full-launcher-20261016/qa-run2/evidence/last-setup-observation.png")))Check(GameScreen.Classify(scaled)==GameScreenKind.Setup,"Recognizes actual client capture with fractional-DPI pixel alignment");
using(var hovered=new Bitmap(Path.Combine(repo,".build/full-launcher-20261016/qa-run4/evidence/last-setup-observation.png")))Check(GameScreen.Classify(hovered)==GameScreenKind.Setup,"Begin remains recognizable while hovered and drawn white");
using(var loading=Fixture("loading-0.png"))Check(GameScreen.Classify(loading)==GameScreenKind.Unknown,"Loading progress is never gameplay");
using(var loaded=Fixture("loaded-match-0.png"))Check(GameScreen.Classify(loaded)==GameScreenKind.Gameplay,"Recognizes actual loaded match");
using(var ai=Fixture("ai-base-after-0.png"))Check(GameScreen.Classify(ai)==GameScreenKind.Gameplay,"Recognizes gameplay with changed resources and minimap");
using(var blank=new Bitmap(1280,720))Check(GameScreen.Classify(blank)==GameScreenKind.Unknown,"Blank/occluded window never triggers input or success");
using(var setup=Fixture("setup-0.png"))
{
    using(var g=Graphics.FromImage(setup))g.FillRectangle(Brushes.Black,new Rectangle(975,810,175,70));
    Check(GameScreen.Classify(setup)==GameScreenKind.Unknown,"Setup without Begin button cannot trigger a click");
}
}
var root=Path.Combine(repo,".build","full-launcher-20261016","tests-"+Guid.NewGuid().ToString("N"));
var maps=Path.Combine(root,"Maps");Directory.CreateDirectory(maps);
var bad=Path.Combine(maps,"long-map");Directory.CreateDirectory(bad);
File.WriteAllText(Path.Combine(bad,new string('a',44)+".map"),"map data");
File.WriteAllText(Path.Combine(bad,"metadata.xml"),"sidecar");
var good=Path.Combine(maps,"good");Directory.CreateDirectory(good);File.WriteAllText(Path.Combine(good,"good.map"),"good");
Check(MapCompatibilityLease.FindIncompatible(maps).Length==1,"Finds filename that needs 49 bytes including NUL");
try{using var rejected=MapCompatibilityLease.Acquire(maps,Path.Combine(root,"reject"),false);throw new Exception("Expected map preflight rejection");}
catch(InvalidDataException){Check(Directory.Exists(bad),"Map preflight rejection leaves files in place");}
var original=MapCompatibilityLease.Hashes(maps);
using(var lease=MapCompatibilityLease.Acquire(maps,Path.Combine(root,"session"),true))
{
    Check(!Directory.Exists(bad)&&Directory.Exists(good),"Only incompatible map directory is held");
    Check(File.Exists(Path.Combine(root,"session","map-restoration.json")),"Recovery journal exists before session");
}
var restored=MapCompatibilityLease.Hashes(maps);
Check(original.Count==restored.Count&&original.All(p=>restored.GetValueOrDefault(p.Key)==p.Value),"Complete map file set and sidecar hashes restored");
var bundled=BundledNavigation.Extract(Path.Combine(root,"bundled-patch"));
Check(BundledNavigation.Matches(bundled),"Portable launcher extracts its embedded navigation patch with the pinned hash");
File.WriteAllText(bundled,"corrupted");
try{BundledNavigation.Extract(Path.GetDirectoryName(bundled));throw new Exception("Expected corrupt-cache rejection");}
catch(InvalidDataException){Check(true,"Corrupt cached patch is refused rather than launched");}
var fakeProfile=Path.Combine(root,"profiles","test");Directory.CreateDirectory(fakeProfile);
File.WriteAllText(Path.Combine(fakeProfile,"Skirmish.ini"),"Rts:GameInfo = partial");
try{LaunchPreset.Capture(new Dictionary<string,string?>{{"profile","test"},{"profile-root",Path.Combine(root,"profiles")}});throw new Exception("Expected incomplete-preset rejection");}
catch(InvalidDataException){Check(!Directory.Exists(Path.Combine(fakeProfile,"EpicWarPresets")),"Incomplete Skirmish.ini rejected before creating a preset");}
using(var first=LaunchExclusion.Acquire())
{
    // Windows mutexes are recursive on the owning thread; another thread must be refused.
    var refused=Task.Run(()=>{try{using var second=LaunchExclusion.Acquire();return false;}catch(InvalidOperationException){return true;}}).Result;
    Check(refused,"Second launcher cannot race preset or map changes");
}
foreach(var badArgs in new[]{new[]{"--proflie","dave"},new[]{"--profile","dave","--profile","dave"},new[]{"--preset","--dry-run"},new[]{"--test-seconds","5"}})
{
    try { CliOptions.Parse("launch-game",badArgs);throw new Exception("Expected CLI validation rejection"); }
    catch(ArgumentException) { Check(true,"Rejects ambiguous or unsupported CLI arguments: "+string.Join(" ",badArgs)); }
}
Check(CliOptions.Parse("test-game",["--profile","test","--test-seconds","2","--dry-run"])["test-seconds"]=="2","Parses bounded test command without dropping options");
var replay=Path.Combine(root,"Last Replay.RA3Replay");var replayEvidence=Path.Combine(root,"replay-test");Directory.CreateDirectory(replayEvidence);
File.WriteAllText(replay,"original replay");var replayBefore=TestGame.HashOrNull(replay)!;
File.Copy(replay,Path.Combine(replayEvidence,"replay.before.RA3Replay"));File.WriteAllText(replay,"new replay");
TestGame.RestoreReplay(replay,replayBefore,replayEvidence);
Check(TestGame.HashOrNull(replay)==replayBefore && File.ReadAllText(Path.Combine(replayEvidence,"generated.RA3Replay"))=="new replay","Archives generated replay and restores exact previous bytes");
var absentReplay=Path.Combine(root,"absent-replay");var absentEvidence=Path.Combine(root,"absent-evidence");Directory.CreateDirectory(absentEvidence);File.WriteAllText(absentReplay,"generated");
TestGame.RestoreReplay(absentReplay,null,absentEvidence);
Check(!File.Exists(absentReplay) && File.Exists(Path.Combine(absentEvidence,"generated.RA3Replay")),"Restores replay absence while retaining the generated test replay");
Check(!TestGame.SameHashes(original,new Dictionary<string,string>(original){{"extra","hash"}}),"Map audit rejects unexpected added files");
var fakeGame=Path.Combine(root,"game");var fakeMod=Path.Combine(root,"mod");Directory.CreateDirectory(fakeGame);Directory.CreateDirectory(fakeMod);
File.WriteAllBytes(Path.Combine(fakeGame,"RA3.exe"),[0]);File.WriteAllText(Path.Combine(fakeMod,"base.big"),"placeholder");
File.WriteAllText(Path.Combine(fakeMod,"RA3 Epic War_5.4.skudef"),"mod-game 1.12\nadd-big base.big\n");
var validPreset=Path.Combine(root,"preset.ini");File.WriteAllText(validPreset,"Rts:Map = internal/test\nRts:GameInfo = M=internal/test;RU=1;S=Htest;\n");
File.WriteAllText(Path.Combine(fakeProfile,"Skirmish.ini"),"original settings");
var testOptions=new Dictionary<string,string?> { ["profile"]="test",["profile-root"]=Path.Combine(root,"profiles"),["preset"]=validPreset,
    ["game-root"]=fakeGame,["mod-directory"]=fakeMod,["runtime-root"]=Path.Combine(Directory.GetParent(repo)!.FullName,"runtime-native-tests",Path.GetFileName(root)[^12..]),["map-root"]=maps,
    ["overlay"]=BundledNavigation.Extract(Path.Combine(root,"test-patch")),["evidence-root"]=Path.Combine(root,"smoke"),
    ["isolate-incompatible-maps"]="true",["replay-path"]=replay,["test-seconds"]="1" };
var dryOptions=new Dictionary<string,string?>(testOptions){["dry-run"]="true"};
var dry=TestGame.Run(dryOptions,CancellationToken.None);
Check(dry.ExitCode==0 && !Directory.Exists(testOptions["evidence-root"]),"Test dry-run resolves without creating session evidence or starting an engine");
var invalidOptions=new Dictionary<string,string?>(testOptions){["setup-timeout"]="0"};
var invalid=TestGame.Run(invalidOptions,CancellationToken.None);
Check(invalid.ExitCode==2 && !Directory.Exists(testOptions["runtime-root"]),"Invalid deadline refused before preset/runtime mutation");
var smoke=TestGame.Run(testOptions,CancellationToken.None);var smokeJson=JsonSerializer.SerializeToElement(smoke.Report);
Check(smoke.ExitCode==1 && smokeJson.GetProperty("preservation").GetProperty("profileRestored").GetBoolean() &&
    smokeJson.GetProperty("preservation").GetProperty("mapsRestored").GetBoolean() && smokeJson.GetProperty("preservation").GetProperty("replayRestored").GetBoolean(),
    "Native process-start failure restores preset, held maps and replay, and returns a failing JSON report");
Check(File.Exists(Path.Combine(testOptions["evidence-root"]!,"result.json")),"Failed live test persists result.json for Codex");
using(var cancelled=new CancellationTokenSource())
{
    cancelled.Cancel();var cancelOptions=new Dictionary<string,string?>(testOptions){["evidence-root"]=Path.Combine(root,"cancelled")};
    Check(TestGame.Run(cancelOptions,cancelled.Token).ExitCode==130,"Cancelled test returns 130 before launching the game");
}
File.WriteAllText(Path.Combine(fakeGame,"RA3_english_1.12.SkuDef"),"test config");
using(var cancelled=new CancellationTokenSource(TimeSpan.FromSeconds(10)))
{
    var activeCancel=new Dictionary<string,string?>(testOptions){["engine-image"]=Path.Combine(AppContext.BaseDirectory,"EpicWar.Automation.Tests.exe"),["evidence-root"]=Path.Combine(root,"active-cancel")};
    var request=Task.Run(()=>
    {
        // Wait until the child has started, then use the same control command as Codex.
        var control=Path.Combine(activeCancel["evidence-root"]!,"control.json");
        while(!File.Exists(control)) { cancelled.Token.ThrowIfCancellationRequested();Thread.Sleep(50); }
        Thread.Sleep(1000);
        TestGame.RequestCancellation(activeCancel["evidence-root"]!);
    });
    var outcome=TestGame.Run(activeCancel,cancelled.Token);var record=JsonSerializer.SerializeToElement(outcome.Report);
    request.GetAwaiter().GetResult();
    Check(outcome.ExitCode==130 && record.GetProperty("preservation").GetProperty("mapsRestored").GetBoolean() && record.GetProperty("preservation").GetProperty("profileRestored").GetBoolean(),
        "CLI cancellation request after native process creation stops the owned process and restores settings/maps");
    var again=JsonSerializer.SerializeToElement(TestGame.RequestCancellation(activeCancel["evidence-root"]!));
    Check(again.GetProperty("status").GetString()=="test-already-ended","Cancellation is idempotent after a completed test");
}
NativePatchTests.Run(repo,Check);
LauncherDefaultsTests.Run(repo,Check);
Console.WriteLine(JsonSerializer.Serialize(new{passed=results.Count,checks=results},new JsonSerializerOptions{WriteIndented=true}));
