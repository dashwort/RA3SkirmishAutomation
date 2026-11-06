using EpicWar.Automation;
using System.Text.Json;

Console.SetOut(new SupervisorTextWriter(Console.Out));
Console.SetError(new SupervisorTextWriter(Console.Error));

try
{
    ForegroundGame.SetProcessDpiAwarenessContext(new IntPtr(-4));
    if(args.Length==0 || args[0]=="launcher")
    {
        var ui = Path.Combine(AppContext.BaseDirectory, "RA3SkirmishLauncher.exe");
        if (!File.Exists(ui)) throw new FileNotFoundException("The WinUI launcher is missing. Use the complete portable release folder.", ui);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(ui) { UseShellExecute = true });
        return 0;
    }
    if (args[0] == "memory-patch" && !args.Contains("--help"))
    {
        if (args.Length < 2) throw new ArgumentException("Choose memory-patch status, enable or disable.");
        Write(MemoryPatchCommands.Run(args[1], CliOptions.Parse("memory-patch", args.Skip(2))));
        return 0;
    }
    if (args[0] == "launch-mod-menu" && !args.Contains("--help"))
    {
        using var cancellation = new CancellationTokenSource();
        WatchStop(cancellation);
        Write(ModMenuLaunch.Launch(CliOptions.Parse("launch-mod-menu", args.Skip(1)), cancellation.Token));
        return 0;
    }
    if(args[0]=="native-patch" && !args.Contains("--help"))
    {
        if(args.Length<2) throw new ArgumentException("Choose native-patch status, install, restore or recover.");
        Write(NativePatchCommands.Run(args[1],CliOptions.Parse("native-patch",args.Skip(2))));
        return 0;
    }
    if(args[0]=="recover-skirmish" && !args.Contains("--help"))
    {
        if(args.Length!=2) throw new ArgumentException("Usage: recover-skirmish <profile-restoration.json>.");
        Write(SkirmishSession.Recover(args[1]));
        return 0;
    }
    if(args[0]=="launch-skirmish" && !args.Contains("--help"))
    {
        var options=CliOptions.Parse("launch-skirmish",args.Skip(1));
        using var cancellation=new CancellationTokenSource();
        WatchStop(cancellation);
        ConsoleCancelEventHandler handler=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
        Console.CancelKeyPress+=handler;
        try { Write(VanillaSkirmish.Launch(options,cancellation.Token));return 0; }
        finally { Console.CancelKeyPress-=handler; }
    }
    if(args.Length==2 && args[0]=="classify-frame" && args[1]!="--help")
    {
        using var frame=new System.Drawing.Bitmap(Path.GetFullPath(args[1]));
        Write(new {schemaVersion=1,status="frame-classified",classification=GameScreen.Classify(frame).ToString(),scores=GameScreen.Scores(frame)});
        return 0;
    }
    if(args.Length==2 && args[0]=="cancel-test" && args[1]!="--help")
    {
        Write(TestGame.RequestCancellation(args[1]));
        return 0;
    }
    if(args[0]=="test-game" && !args.Contains("--help"))
    {
        var options=CliOptions.Parse("test-game",args.Skip(1));
        using var cancellation=new CancellationTokenSource();
        ConsoleCancelEventHandler handler=(_,e)=>{e.Cancel=true;cancellation.Cancel();};
        Console.CancelKeyPress+=handler;
        try
        {
            var outcome=TestGame.Run(options,cancellation.Token);
            Write(outcome.Report);
            return outcome.ExitCode;
        }
        finally { Console.CancelKeyPress-=handler; }
    }
    if(args.Length==2 && args[0]=="recover-maps")
    {
        using var exclusion=LaunchExclusion.Acquire();
        LaunchPreset.EnsureGameClosed();
        MapCompatibilityLease.Recover(args[1]);
        Write(new {status="maps-restored",journal=Path.GetFullPath(args[1])});
        return 0;
    }
    if (args.Length == 4 && args[0] == "merge-big")
    {
        Write(Big4Merger.Merge(args[1], args[2], args[3]));
        return 0;
    }

    if (args.Length == 4 && args[0] == "replace-big")
    {
        Write(Big4Merger.Merge(args[1], args[2], args[3], replaceConflicts: true));
        return 0;
    }

    if (args.Length == 4 && args[0] == "clone-apt-action")
    {
        Write(AptBinaryPatch.CloneAction(args[1], args[2], args[3]));
        return 0;
    }

    if (args.Length == 3 && args[0] == "patch-menu-main-inplace")
    {
        Write(AptInPlacePatch.PatchMain(args[1], args[2]));
        return 0;
    }

    if (args.Length == 3 && args[0] == "patch-menu-skirmish-inplace")
    {
        Write(AptInPlacePatch.Skirmish(args[1], args[2]));
        return 0;
    }

    if (args.Length == 3 && args[0] == "probe-flat-menu-exit")
    {
        Write(AptInPlacePatch.ProbeFlatMenuExit(args[1], args[2]));
        return 0;
    }

    if (args.Length == 3 && args[0] == "probe-menu-root-class")
    {
        Write(AptInPlacePatch.ProbeRootClass(args[1], args[2]));
        return 0;
    }

    if (args.Length == 5 && args[0] == "patch-apt-binary")
    {
        if (args[4] is not ("main" or "skirmish"))
            throw new ArgumentException("Menu kind must be main or skirmish.");
        Write(AptBinaryPatch.PatchAction(args[1], args[2], args[3], args[4] == "main"));
        return 0;
    }

    if (args.Length == 4 && args[0] == "patch-apt")
    {
        if (args[3] is not ("main" or "skirmish" or "skirmish-immediate" or "skirmish-after-intro" or "skirmish-after-intro-url"))
            throw new ArgumentException("Menu kind must be main, skirmish, skirmish-immediate, skirmish-after-intro, or skirmish-after-intro-url.");
        AptPatch.Apply(args[1], args[2], args[3]);
        Write(new { status = "patched", output = args[2] });
        return 0;
    }

    if (args.Length > 0 && !args.Contains("--help") && args[0] is "capture-preset" or "launch-preset" or "launch-base" or "launch-game")
    {
        var options = CliOptions.Parse(args[0],args.Skip(1));
        if(args[0]=="launch-game")options["start-match"]="true";
        if (args[0] == "capture-preset")
            Write(LaunchPreset.Capture(options));
        else
        {
            using var cancellation = new CancellationTokenSource();
            WatchStop(cancellation);
            Write(LaunchPreset.Launch(options, args[0] != "launch-base", cancellation.Token));
        }
        return 0;
    }

    Console.WriteLine("""
        RA3 Skirmish Automation commands:
          memory-patch status|enable|disable [--game-root <path>] [--backup-root <path>]
            Changes only 4 GB memory support, preserving native automation.
          launch-mod-menu --game-root <path> --base-skudef <path> [--large-address-aware on|off]
            Opens a mod normally without applying a preset or an automation overlay.
          Supervised skirmish commands additionally accept --large-address-aware on|off
            and --remember-preset <path>. Omit these options to preserve existing behavior.
          launch-skirmish --profile <name> --preset <Skirmish.ini> [--game-root <path>] [--backup-root <path>]
            [--config-skudef <path>] [--runtime-root <path>] [--width <pixels>] [--height <pixels>]
            [--isolate-incompatible-maps] [--test-seconds <1..300>] [--dry-run]
            [--mod-directory <path> --base-skudef <path> --experimental]
            Launches base RA3 1.12 directly with a reviewed native engine patch.
            The explicitly gated mod options are for native Epic War compatibility testing only.
            Requires native profile/preset/gameplay verification; never sends Begin input.
            The dry-run works before a native package has passed acceptance.
          native-patch status|install|restore|recover [--game-root <path>] [--backup-root <path>]
            Only reviewed embedded production recipes may be installed.
          recover-skirmish <session/profile-restoration.json>
            Restores the exact original INI after an interrupted vanilla supervisor.
          launcher                       Open the preset-to-game launcher window.
          test-game --profile <name> [--preset <Skirmish.ini>] [--name <preset>]
            [--test-seconds <1..300>] [--evidence-root <new-folder>] [--dry-run]
            [--isolate-incompatible-maps] [--setup-timeout <10..300>] [--load-timeout <10..300>]
            Runs a bounded match/HUD smoke test, stops its owned game and checks
            preset/map SHA256 restoration. Archives and restores Last Replay.
            --replay-path overrides the replay location for an isolated engine.
            Writes one JSON result to stdout and <evidence-root>/result.json.
            Progress JSON goes to stderr. Exit 0=pass/dry-run, 1=failure,
            2=invalid arguments, 130=Ctrl+C cancellation after cleanup.
          classify-frame <client.png>     Classify a saved client capture without launching RA3.
          cancel-test <evidence-folder>   Request cooperative cancellation; await the test's result.json for cleanup.
          launch-game --profile <name> [--preset <Skirmish.ini>] [--name <preset>]
            [--isolate-incompatible-maps] [--map-root <path>] [--evidence-root <path>]
            [--setup-timeout <10..300>] [--load-timeout <10..300>] [--dry-run]
            Applies the complete preset, waits for setup, sends normal Begin once,
            verifies a gameplay HUD, and restores the original preset/maps on exit.
          recover-maps <session/map-restoration.json>  Restore maps after an interrupted supervisor.
          merge-big <base.big> <overlay.big> <output.big>
          replace-big <base.big> <overlay.big> <output.big>
          clone-apt-action <input.apt> <input.const> <output.apt>
          patch-menu-main-inplace <input.apt> <output.apt>
          patch-menu-skirmish-inplace <input.const> <output.const>
          probe-flat-menu-exit <input.const> <output.const>
          probe-menu-root-class <input.apt> <output.apt>
          patch-apt-binary <input.apt> <input.const> <output.apt> <main|skirmish>
          patch-apt <input.xml> <output.xml> <main|skirmish|skirmish-immediate|skirmish-after-intro|skirmish-after-intro-url>
          capture-preset --profile <name> [--name <preset>] [--output <path>]
          launch-base --profile <name> [--name <preset>] [--preset <path>] [--dry-run]
          launch-preset --profile <name> [--name <preset>] [--preset <path>] [--mod-directory <path>] [--base-skudef <path>]
            [--overlay <path>] [--runtime-root <path>] [--game-root <path>]
            [--engine-image <path>] [--config-skudef <path>] [--dry-run] [--experimental]
            The published Skirmish-setup navigation package runs without --experimental; other overlays require it.
        """);
    return args.Contains("--help") || args[0] is "help" or "-h" ? 0 : 2;
}
catch (Exception e)
{
    var exitCode=e is ArgumentException ? 2 : e is OperationCanceledException ? 130 : 1;
    var error=JsonSerializer.Serialize(new
    {
        schemaVersion=1,
        status = "error",
        exitCode,
        message = e.Message,
        nativeErrorCode = (e as System.ComponentModel.Win32Exception)?.NativeErrorCode
    });
    if(args.Length>0 && args[0]=="test-game")Console.WriteLine(error);
    else Console.Error.WriteLine(error);
    return exitCode;
}

static void Write(object value) => Console.WriteLine(JsonSerializer.Serialize(value));

static void WatchStop(CancellationTokenSource cancellation)
{
    if (!Console.IsInputRedirected) return;
    _ = Task.Run(async () =>
    {
        try
        {
            if (await Console.In.ReadLineAsync() == "stop") cancellation.Cancel();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    });
}
