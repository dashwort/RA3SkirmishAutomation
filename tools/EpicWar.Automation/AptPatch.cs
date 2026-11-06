using System.Xml.Linq;

namespace EpicWar.Automation;

// Experimental callback generation. The skirmish variant overrides playGame
// and requires the matching CONST call redirection. Match startup is unverified.
internal static class AptPatch
{
    public static void Apply(string input, string output, bool mainMenu) =>
        Apply(input, output, mainMenu ? "main" : "skirmish");

    public static void Apply(string input, string output, string kind)
    {
        var doc = XDocument.Load(input);
        var frame = doc.Root?.Element("movieclip")?.Element("frames")?.Elements("frame").FirstOrDefault();
        var action = frame?.Elements("action").SingleOrDefault();
        if (action is null)
            throw new InvalidDataException("Expected exactly one action block in the RA3 menu's initial frame.");
        if (doc.Descendants("string").Any(e => ((string?)e.Attribute("str"))?.StartsWith("epicWarAuto") == true))
            throw new InvalidDataException("This APT is already patched.");

        var stop = action.Elements("noarg").FirstOrDefault(e => (string?)e.Attribute("action") == "7")
            ?? action.Elements().LastOrDefault(e => e.Name == "noarg" && (string?)e.Attribute("action") == "0");
        if (stop is null)
            throw new InvalidDataException("Could not find the end of the stock screen constructor.");

        var injected = kind switch
        {
            "main" => CreateAction(mainMenu: true),
            "skirmish" => CreateAction(mainMenu: false),
            "skirmish-immediate" => CreateImmediateStartAction(),
            "skirmish-after-intro" => CreateAfterIntroStartAction(callStockPlayGame: true),
            "skirmish-after-intro-url" => CreateAfterIntroStartAction(callStockPlayGame: false),
            _ => throw new ArgumentException($"Unsupported APT patch kind: {kind}", nameof(kind))
        };
        foreach (var instruction in injected.Elements().ToArray())
            stop.AddBeforeSelf(instruction);

        doc.Save(output);
    }

    internal static XElement CreateAction(bool mainMenu)
    {
        var a = new Actions();

        // Keep superclass initialization at its original method location:
        // avoid relying on superclass lookup through an alias in the game VM.
        if (mainMenu)
        {
        a.Var("screen");
        a.Str("epicWarAutoOriginalInit");
        a.Var("screen");
        a.Str("frameDelayedInit");
        a.Op(0x4e); // GetMember
        a.Op(0x4f); // SetMember

        a.Var("screen");
        a.Str("frameDelayedInit");
        }
        else
        {
            // Preserve the screen's deferred initializer and use it as the
            // point where the setup screen is ready to schedule one start.
            a.Var("screen");
            a.Str("epicWarAutoOriginalInit");
            a.Var("screen");
            a.Str("frameDelayedInit");
            a.Op(0x4e); // GetMember
            a.Op(0x4f); // SetMember

            a.Var("screen");
            a.Str("playGame");
        }
        var function = a.Function2();
        var bodyStart = a.Position;

        if (mainMenu)
        {
        a.Op(0x59); // PushZero arguments
        a.Register(1);
        a.Str("epicWarAutoOriginalInit");
        a.Op(0x52); // CallMethod
        a.Op(0x17); // Pop
        }

        var flag = mainMenu ? "epicWarAutoNavigated" : "epicWarAutoStarted";
        if (mainMenu) a.Var("_global"); else a.Register(1);
        a.Member(flag);
        var alreadyRan = a.BranchIfTrue();

        if (mainMenu) a.Var("_global"); else a.Register(1);
        a.Str(flag);
        a.Op(0x73); // PushTrue
        a.Op(0x4f); // SetMember

        if (mainMenu)
        {
            // Use the game's own screen transition, including its outtro.
            a.Register(2); // _global, as in the stock menu transition
            a.Member("SCREEN");
            a.Member("FES_SKIRMISH_SETUP");
            a.Op(0x5a); // PushOne argument
            a.Register(1);
            a.Str("startOuttro");
            a.Op(0x52); // CallMethod
            a.Op(0x17); // Pop
        }
        else
        {
            // Leave the intro callback before asking the engine to unload its
            // screen. A one-shot movie callback gives setup updates 180 frames.
            a.Var("Cafe2_BaseUIScreen");
            a.Member("m_screen");
            a.Str("epicWarFrames");
            a.Byte(180);
            a.Op(0x4f);
            a.Var("Cafe2_BaseUIScreen");
            a.Member("m_screen");
            a.Str("onEnterFrame");
            var tick = a.Function2();
            var tickStart = a.Position;
            const string startIssuedFlag = "epicWarAutoStartIssued";
            a.Var("_global");
            a.Member(startIssuedFlag);
            var alreadyIssued = a.BranchIfTrue();

            a.Register(1);
            a.Str("epicWarFrames");
            a.Register(1);
            a.Member("epicWarFrames");
            a.Op(0x5a);
            a.Op(0x0b); // Subtract
            a.Op(0x4f);
            a.Op(0x59);
            a.Register(1);
            a.Member("epicWarFrames");
            a.Op(0x48); // 0 < remaining frames
            var waiting = a.BranchIfTrue();

            // Keep the running callback attached until it returns. Clearing
            // onEnterFrame from inside itself can collect the active APT
            // function while the VM is still executing it. A global guard
            // stops later frames from issuing another native start request.
            a.Var("_global");
            a.Str(startIssuedFlag);
            a.Op(0x73); // PushTrue
            a.Op(0x4f);
            a.Url("FSCommand:CallGameFunction", "%StartGame");
            a.ResolveBranch(waiting);
            a.ResolveBranch(alreadyIssued);
            a.Op(0x59);
            a.Op(0x3e);
            tick.SetAttributeValue("size", a.Position - tickStart);
            a.Op(0x4f);
        }

        a.ResolveBranch(alreadyRan);
        a.Op(0x59); // PushZero result
        a.Op(0x3e); // Return
        function.SetAttributeValue("size", a.Position - bodyStart);
        a.Op(0x4f); // SetMember: install the wrapper on screen

        if (!mainMenu)
        {
            // The original UI only calls playGame after a button click. Call
            // our wrapper once after inherited setup initialization instead.
            a.Var("screen");
            a.Str("frameDelayedInit");
            var init = a.Function2();
            var initStart = a.Position;

            a.Op(0x59); // PushZero arguments
            a.Register(1);
            a.Str("epicWarAutoOriginalInit");
            a.Op(0x52); // CallMethod
            a.Op(0x17); // Pop

            const string launchGuard = "epicWarAutoMatchLaunchScheduled";
            a.Var("_global");
            a.Member(launchGuard);
            var alreadyScheduled = a.BranchIfTrue();
            a.Var("_global");
            a.Str(launchGuard);
            a.Op(0x73); // PushTrue
            a.Op(0x4f); // SetMember
            a.Op(0x59); // PushZero arguments
            a.Register(1);
            a.Str("playGame");
            a.Op(0x52); // CallMethod
            a.Op(0x17); // Pop
            a.ResolveBranch(alreadyScheduled);

            a.Op(0x59); // PushZero result
            a.Op(0x3e); // Return
            init.SetAttributeValue("size", a.Position - initStart);
            a.Op(0x4f); // SetMember: install the deferred-init wrapper
        }

        return a.Xml;
    }

    private static XElement CreateImmediateStartAction()
    {
        var a = new Actions();

        // Run the game's complete deferred setup initialization first. Then
        // invoke the stock playGame method once, which emits the native
        // FSCommand:CallGameFunction %StartGame request.
        a.Var("screen");
        a.Str("epicWarAutoOriginalInit");
        a.Var("screen");
        a.Str("frameDelayedInit");
        a.Op(0x4e); // GetMember
        a.Op(0x4f); // SetMember

        a.Var("screen");
        a.Str("frameDelayedInit");
        var function = a.Function2();
        var bodyStart = a.Position;

        a.Op(0x59); // PushZero arguments
        a.Register(1);
        a.Str("epicWarAutoOriginalInit");
        a.Op(0x52); // CallMethod
        a.Op(0x17); // Pop

        const string launchGuard = "epicWarAutoImmediateStartIssued";
        a.Var("_global");
        a.Member(launchGuard);
        var alreadyIssued = a.BranchIfTrue();

        a.Var("_global");
        a.Str(launchGuard);
        a.Op(0x73); // PushTrue
        a.Op(0x4f); // SetMember

        a.Op(0x59); // PushZero arguments
        a.Register(1);
        a.Str("playGame");
        a.Op(0x52); // CallMethod
        a.Op(0x17); // Pop

        a.ResolveBranch(alreadyIssued);
        a.Op(0x59); // PushZero result
        a.Op(0x3e); // Return
        function.SetAttributeValue("size", a.Position - bodyStart);
        a.Op(0x4f); // SetMember: install the deferred-init wrapper

        return a.Xml;
    }

    private static XElement CreateAfterIntroStartAction(bool callStockPlayGame)
    {
        var a = new Actions();

        // Hook the base screen's completion callback before the Skirmish
        // initializer starts its intro. Defer playGame to the next movie
        // frame so StartGame cannot unload the screen from inside init or the
        // completion callback itself.
        a.Var("screen");
        a.Str("epicWarAutoOriginalInit");
        a.Var("screen");
        a.Str("frameDelayedInit");
        a.Op(0x4e); // GetMember
        a.Op(0x4f); // SetMember

        a.Var("screen");
        a.Str("frameDelayedInit");
        var init = a.Function2();
        var initStart = a.Position;

        a.Register(1);
        a.Member("epicWarAutoIntroHookInstalled");
        var alreadyInstalled = a.BranchIfTrue();

        a.Register(1);
        a.Str("epicWarAutoIntroHookInstalled");
        a.Op(0x73); // PushTrue
        a.Op(0x4f); // SetMember

        a.Register(1);
        a.Str("epicWarAutoOriginalIntrosComplete");
        a.Register(1);
        a.Str("onIntrosComplete");
        a.Op(0x4e); // GetMember
        a.Op(0x4f); // SetMember

        a.Register(1);
        a.Str("onIntrosComplete");
        var introComplete = a.Function2();
        var introStart = a.Position;

        a.Op(0x59); // PushZero arguments
        a.Register(1);
        a.Str("epicWarAutoOriginalIntrosComplete");
        a.Op(0x52); // CallMethod
        a.Op(0x17); // Pop

        a.Var("_global");
        a.Member("epicWarAutoStartScheduled");
        var alreadyScheduled = a.BranchIfTrue();

        a.Var("_global");
        a.Str("epicWarAutoStartScheduled");
        a.Op(0x73); // PushTrue
        a.Op(0x4f); // SetMember

        a.Var("_global");
        a.Str("epicWarAutoSkirmishScreen");
        a.Register(1);
        a.Op(0x4f); // SetMember

        a.Var("Cafe2_BaseUIScreen");
        a.Member("m_screen");
        a.Str("onEnterFrame");
        var tick = a.Function2();
        var tickStart = a.Position;
        a.Var("_global");
        a.Member("epicWarAutoStartIssued");
        var alreadyIssued = a.BranchIfTrue();

        a.Var("_global");
        a.Str("epicWarAutoStartIssued");
        a.Op(0x73); // PushTrue
        a.Op(0x4f); // SetMember

        if (callStockPlayGame)
        {
            a.Op(0x59); // PushZero arguments
            a.Var("_global");
            a.Member("epicWarAutoSkirmishScreen");
            a.Str("playGame");
            a.Op(0x52); // CallMethod
            a.Op(0x17); // Pop
        }
        else
        {
            a.Url("FSCommand:CallGameFunction", "%StartGame");
        }

        a.ResolveBranch(alreadyIssued);
        a.Op(0x59); // PushZero result
        a.Op(0x3e); // Return
        tick.SetAttributeValue("size", a.Position - tickStart);
        a.Op(0x4f); // SetMember: install the next-frame callback

        a.ResolveBranch(alreadyScheduled);
        a.Op(0x59); // PushZero result
        a.Op(0x3e); // Return
        introComplete.SetAttributeValue("size", a.Position - introStart);
        a.Op(0x4f); // SetMember: install the intro-complete wrapper

        a.ResolveBranch(alreadyInstalled);
        a.Op(0x59); // PushZero arguments
        a.Register(1);
        a.Str("epicWarAutoOriginalInit");
        a.Op(0x52); // CallMethod
        a.Op(0x17); // Pop
        a.Op(0x59); // PushZero result
        a.Op(0x3e); // Return
        init.SetAttributeValue("size", a.Position - initStart);
        a.Op(0x4f); // SetMember: install the deferred-init wrapper

        return a.Xml;
    }

    // APT operands are aligned to 32-bit boundaries. Position tracks the
    // encoded byte count for branch offsets and DefineFunction2 code size.
    private sealed class Actions
    {
        public XElement Xml { get; } = new("injected");
        public int Position { get; private set; }

        public void Op(int opcode)
        {
            Xml.Add(new XElement("noarg", new XAttribute("action", opcode)));
            Position++;
        }

        private void Aligned(int operandBytes) => Position = ((Position + 1 + 3) & ~3) + operandBytes;

        public void Str(string value)
        {
            Xml.Add(new XElement("string", new XAttribute("action", 0xa1), new XAttribute("str", value)));
            Aligned(4);
        }

        public void Var(string name)
        {
            Str(name);
            Op(0x1c); // GetVariable
        }

        public void Member(string name)
        {
            Str(name);
            Op(0x4e); // GetMember
        }

        public void Register(int number)
        {
            Xml.Add(new XElement("byte", new XAttribute("action", 0xb9), new XAttribute("val", number)));
            Position += 2;
        }

        public void Byte(int number)
        {
            Xml.Add(new XElement("byte", new XAttribute("action", 0xb5), new XAttribute("val", number)));
            Position += 2;
        }

        public XElement Function2()
        {
            // These flags are taken from RA3's own Cafe2_BaseUIScreen
            // onIntrosComplete function (register 1=this, register 2=_global).
            var element = new XElement("definefunction2",
                new XAttribute("action", 0x8e),
                new XAttribute("name", ""),
                new XAttribute("flags", 19464196),
                new XAttribute("size", 0));
            Xml.Add(element);
            Aligned(28);
            return element;
        }

        public (XElement Element, int End) BranchIfTrue()
        {
            var element = new XElement("goto", new XAttribute("action", 0x9d), new XAttribute("pos", 0));
            Xml.Add(element);
            Aligned(4);
            return (element, Position);
        }

        public void ResolveBranch((XElement Element, int End) branch) =>
            branch.Element.SetAttributeValue("pos", Position - branch.End);

        public void Url(string url, string target)
        {
            Xml.Add(new XElement("geturl", new XAttribute("action", 0x83), new XAttribute("str1", url), new XAttribute("str2", target)));
            Aligned(8);
        }
    }
}
