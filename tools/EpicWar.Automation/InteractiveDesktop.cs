using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EpicWar.Automation;

internal sealed record InteractiveDesktopSnapshot(int SessionId, int SessionState, int SessionFlags);

internal static class InteractiveDesktop
{
    private const int WtsInfoEx = 25;
    private const int WtsActive = 0;
    private const int WtsDisconnected = 4;
    private const int WtsSessionStateLock = 0;
    private const int WtsSessionStateUnlock = 1;

    internal static void EnsureCurrentSessionReady()
    {
        using var process = Process.GetCurrentProcess();
        var sessionId = process.SessionId;
        if (!WTSQuerySessionInformationW(IntPtr.Zero, sessionId, WtsInfoEx, out var buffer, out var length))
            throw QueryFailure(Marshal.GetLastWin32Error());
        try
        {
            if (buffer == IntPtr.Zero || length > int.MaxValue)
                throw new InvalidDataException("Windows returned invalid WTS session information; native launch was refused.");
            var bytes = new byte[(int)length];
            Marshal.Copy(buffer, bytes, 0, bytes.Length);
            var snapshot = ParseWtsInfoEx(bytes);
            if (snapshot.SessionId != sessionId)
                throw new InvalidDataException("Windows returned a different session than the current process; native launch was refused.");
            Validate(snapshot);
        }
        finally
        {
            if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
        }
    }

    internal static InteractiveDesktopSnapshot ParseWtsInfoEx(ReadOnlySpan<byte> data)
    {
        // WTSINFOEX.Level is followed by an 8-byte-aligned union on Windows.
        // The Level 1 fields begin at union offset 8: SessionId, State, Flags.
        if (data.Length < 20) throw new InvalidDataException("WTSINFOEX data is truncated; native launch was refused.");
        var level = BitConverter.ToInt32(data.Slice(0, 4));
        if (level != 1) throw new InvalidDataException($"Windows returned unsupported WTSINFOEX level {level}; native launch was refused.");
        return new(
            BitConverter.ToInt32(data.Slice(8, 4)),
            BitConverter.ToInt32(data.Slice(12, 4)),
            BitConverter.ToInt32(data.Slice(16, 4)));
    }

    internal static void Validate(InteractiveDesktopSnapshot snapshot)
    {
        if (snapshot.SessionState != WtsActive)
        {
            var state = snapshot.SessionState == WtsDisconnected ? "disconnected" : $"not active (state {snapshot.SessionState})";
            throw new InvalidOperationException($"RA3 native launch requires the current Windows desktop session to be active and unlocked; it is {state}. Sign in or reconnect and unlock the desktop, then try again.");
        }
        if (snapshot.SessionFlags != WtsSessionStateUnlock)
        {
            var state = snapshot.SessionFlags == WtsSessionStateLock ? "locked" : $"in an unknown lock state ({snapshot.SessionFlags})";
            throw new InvalidOperationException($"RA3 native launch requires the current Windows desktop session to be unlocked; it is {state}. Unlock the desktop, then try again.");
        }
    }

    internal static Win32Exception QueryFailure(int error) =>
        new(error, "Windows could not verify the current desktop session; native launch was refused.");

    [DllImport("wtsapi32.dll", EntryPoint = "WTSQuerySessionInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WTSQuerySessionInformationW(IntPtr server, int sessionId, int informationClass,
        out IntPtr buffer, out uint bytesReturned);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern void WTSFreeMemory(IntPtr memory);
}
