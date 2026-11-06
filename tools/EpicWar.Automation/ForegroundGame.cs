using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text;

namespace EpicWar.Automation;

internal static class ForegroundGame
{
    internal static event Action<string>? Progress;
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X,Y; public uint Data,Flags,Time; public UIntPtr Extra; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Value; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window,int command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window,out Rect rectangle);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window,ref Point point);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll",SetLastError=true)] private static extern uint SendInput(uint count,Input[] inputs,int size);
    [DllImport("user32.dll",SetLastError=true)] private static extern IntPtr OpenInputDesktop(uint flags,bool inherit,uint access);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern bool GetUserObjectInformationW(IntPtr handle,int index,StringBuilder buffer,uint bytes,out uint required);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);

    public static object StartMatch(Process process, string evidenceDirectory, int setupSeconds, int loadSeconds, CancellationToken cancellationToken=default)
    {
        Directory.CreateDirectory(evidenceDirectory);
        var timer = Stopwatch.StartNew();
        IntPtr window = IntPtr.Zero;
        bool focusRequested=false;
        int observations=0;
        int consecutive = 0;
        while (timer.Elapsed.TotalSeconds < setupSeconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAlive(process);
            process.Refresh();
            if (process.MainWindowHandle == IntPtr.Zero) { Thread.Sleep(300); continue; }
            if (window == IntPtr.Zero)
            {
                EnsureDesktop();
                var candidate=process.MainWindowHandle;
                if(!focusRequested)
                {
                    ShowWindow(candidate,9);SetForegroundWindow(candidate);focusRequested=true;
                }
                if(GetForegroundWindow()!=candidate)
                {
                    if(observations==0)Log("waiting-for-foreground",process.Id);
                    if(observations++%20==0)File.WriteAllText(Path.Combine(evidenceDirectory,"focus-wait.json"),JsonSerializer.Serialize(new{candidate=candidate.ToInt64(),foreground=GetForegroundWindow().ToInt64(),observedAtUtc=DateTimeOffset.UtcNow}));
                    Thread.Sleep(300);continue;
                }
                window=candidate;
                Thread.Sleep(400);
            }
            using var frame = Capture(process, window);
            if(observations++%20==0) frame.Save(Path.Combine(evidenceDirectory,"last-setup-observation.png"),ImageFormat.Png);
            consecutive = GameScreen.Classify(frame) == GameScreenKind.Setup ? consecutive + 1 : 0;
            if (consecutive >= 3)
            {
                frame.Save(Path.Combine(evidenceDirectory, "setup.png"), ImageFormat.Png);
                ClickBegin(process, window);
                Log("begin-sent", process.Id);
                break;
            }
            Thread.Sleep(400);
        }
        if (consecutive < 3) throw new TimeoutException("Skirmish setup was not recognized before the deadline. No Begin input was sent.");
        var sentAt = DateTimeOffset.UtcNow;
        timer.Restart();
        consecutive = 0;
        while (timer.Elapsed.TotalSeconds < loadSeconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureAlive(process);
            using var frame = Capture(process, window);
            observations++;
            consecutive = GameScreen.Classify(frame) == GameScreenKind.Gameplay ? consecutive + 1 : 0;
            if (observations % 20 == 0 && consecutive == 0)
            {
                frame.Save(Path.Combine(evidenceDirectory, "last-load-observation.png"), ImageFormat.Png);
                File.WriteAllText(Path.Combine(evidenceDirectory, "last-load-scores.json"), JsonSerializer.Serialize(GameScreen.Scores(frame)));
            }
            if (consecutive >= 5)
            {
                frame.Save(Path.Combine(evidenceDirectory, "gameplay.png"), ImageFormat.Png);
                var result = new { status = "match-loaded", processId = process.Id, beginSentAtUtc = sentAt,
                    observedAtUtc = DateTimeOffset.UtcNow, observedSeconds = Math.Round(timer.Elapsed.TotalSeconds, 1),
                    matchStartVerified = true, verification = "five consecutive gameplay-HUD observations after one normal Begin click",
                    aiActionsVerified = false };
                File.WriteAllText(Path.Combine(evidenceDirectory, "match.json"), JsonSerializer.Serialize(result));
                Log("match-loaded", process.Id);
                return result;
            }
            Thread.Sleep(500);
        }
        throw new TimeoutException("Begin was sent once, but a gameplay HUD was not recognized within the loading deadline. Match loading is unverified.");
    }

    internal static void ObserveGameplay(Process process,string evidenceDirectory,int seconds,CancellationToken cancellationToken)
    {
        process.Refresh();
        var window=process.MainWindowHandle;
        var timer=Stopwatch.StartNew();
        int observations=0;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var frame=Capture(process,window);
            if(GameScreen.Classify(frame)!=GameScreenKind.Gameplay)
            {
                frame.Save(Path.Combine(evidenceDirectory,"test-failure.png"),ImageFormat.Png);
                File.WriteAllText(Path.Combine(evidenceDirectory,"test-failure-scores.json"),JsonSerializer.Serialize(GameScreen.Scores(frame)));
                throw new InvalidOperationException("The gameplay HUD disappeared during the smoke-test observation period.");
            }
            observations++;
            if(timer.Elapsed.TotalSeconds>=seconds)
            {
                frame.Save(Path.Combine(evidenceDirectory,"gameplay-end.png"),ImageFormat.Png);
                break;
            }
            if(cancellationToken.WaitHandle.WaitOne(500))cancellationToken.ThrowIfCancellationRequested();
        }while(true);
        File.WriteAllText(Path.Combine(evidenceDirectory,"observation.json"),JsonSerializer.Serialize(new{
            processId=process.Id,requestedSeconds=seconds,observedSeconds=timer.Elapsed.TotalSeconds,observations,aiActionsVerified=false}));
        Log("test-observation-complete",process.Id);
    }

    private static void EnsureAlive(Process process)
    {
        if (process.HasExited) throw new InvalidOperationException("The owned RA3 process exited before gameplay was verified.");
        process.Refresh();
        if (process.MainWindowTitle.Contains("Error",StringComparison.OrdinalIgnoreCase) || process.MainWindowTitle.Contains("crash",StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"RA3 reported a failure: {process.MainWindowTitle}");
    }

    private static void EnsureDesktop()
    {
        var desktop = OpenInputDesktop(0, false, 1);
        if (desktop == IntPtr.Zero) throw new InvalidOperationException("An unlocked interactive desktop is required; no game input was sent.");
        try
        {
            var name=new StringBuilder(256);
            if(!GetUserObjectInformationW(desktop,2,name,512,out _) || !name.ToString().Equals("Default",StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The desktop is locked or unavailable; no game input was sent.");
        }
        finally { CloseDesktop(desktop); }
    }

    private static Rect CheckWindow(Process process, IntPtr window)
    {
        EnsureDesktop();
        GetWindowThreadProcessId(window, out var owner);
        if (owner != process.Id || GetForegroundWindow() != window)
            throw new GameFocusLostException();
        if (!GetClientRect(window, out var rectangle) || rectangle.Right < 800 || rectangle.Bottom < 600)
            throw new InvalidOperationException("RA3 client area is unavailable or too small.");
        return rectangle;
    }

    private static Bitmap Capture(Process process, IntPtr window)
    {
        var rectangle = CheckWindow(process, window);
        var origin = new Point();
        if (!ClientToScreen(window, ref origin)) throw new Win32Exception();
        var bounds = SystemInformation.VirtualScreen;
        if (!bounds.Contains(new Rectangle(origin.X,origin.Y,rectangle.Right,rectangle.Bottom)))
            throw new InvalidOperationException("RA3 must be fully visible on the desktop.");
        var image = new Bitmap(rectangle.Right, rectangle.Bottom);
        try { using var graphics = Graphics.FromImage(image); graphics.CopyFromScreen(origin.X, origin.Y,0,0,image.Size); return image; }
        catch { image.Dispose(); throw; }
    }

    private static void ClickBegin(Process process, IntPtr window)
    {
        var rectangle = CheckWindow(process, window);
        // Recheck the actual frame immediately before the single input action.
        using var frame = Capture(process, window);
        if (GameScreen.Classify(frame) != GameScreenKind.Setup) throw new InvalidOperationException("Setup changed before Begin; no input was sent.");
        var point = new Point { X = (int)(rectangle.Right * .66), Y = (int)(rectangle.Bottom * .94) };
        if (!ClientToScreen(window, ref point)) throw new Win32Exception();
        var x = (int)((long)(point.X - GetSystemMetrics(76)) * 65535 / (GetSystemMetrics(78)-1));
        var y = (int)((long)(point.Y - GetSystemMetrics(77)) * 65535 / (GetSystemMetrics(79)-1));
        CheckWindow(process, window);
        void Send(uint flags)
        {
            var input=new[]{Mouse(x,y,flags)};
            if(SendInput(1,input,Marshal.SizeOf<Input>())!=1)
                throw new Win32Exception(Marshal.GetLastWin32Error(),"Windows rejected part of the Begin input; it will not be repeated.");
        }
        // RA3 polls mouse state per rendered frame. A down/up pair in one
        // SendInput batch can disappear between polls. Hold a normal click over
        // multiple frames, and always release the button even if focus changes.
        Send(0xC001);Thread.Sleep(60);CheckWindow(process,window);
        Send(0xC003);
        try{Thread.Sleep(120);}finally{Send(0xC005);}
        CheckWindow(process,window);
    }

    private static Input Mouse(int x,int y,uint flags) => new() { Type = 0, Value = new InputUnion { Mouse = new MouseInput { X=x,Y=y,Flags=flags } } };
    internal static void Log(string stage,int processId)
    {
        Console.Error.WriteLine(JsonSerializer.Serialize(new { stage,processId,observedAtUtc=DateTimeOffset.UtcNow }));
        Progress?.Invoke(stage);
    }
}

internal sealed class GameFocusLostException : InvalidOperationException
{
    internal GameFocusLostException() : base("RA3 lost foreground focus. Automation stopped without sending further input.") { }
}
