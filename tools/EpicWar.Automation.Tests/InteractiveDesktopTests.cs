using EpicWar.Automation;
using System.Buffers.Binary;

internal static class InteractiveDesktopTests
{
    internal static void Run(Action<bool, string> check)
    {
        byte[] Info(int state, int flags)
        {
            var data = new byte[20];
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(0), 1);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8), 2);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(12), state);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(16), flags);
            return data;
        }

        var active = InteractiveDesktop.ParseWtsInfoEx(Info(0, 1));
        InteractiveDesktop.Validate(active);
        check(active.SessionId == 2 && active.SessionState == 0 && active.SessionFlags == 1,
            "WTSINFOEX reads the Level 1 session fields from their documented union offsets");

        var locked = Throws(() => InteractiveDesktop.Validate(InteractiveDesktop.ParseWtsInfoEx(Info(0, 0))));
        check(locked is InvalidOperationException && locked.Message.Contains("locked", StringComparison.OrdinalIgnoreCase),
            "Locked desktop session is rejected with a clear native-launch message");
        check(Throws(() => InteractiveDesktop.Validate(InteractiveDesktop.ParseWtsInfoEx(Info(0, -1)))) is InvalidOperationException,
            "Unknown desktop lock states are rejected");

        var disconnected = Throws(() => InteractiveDesktop.Validate(InteractiveDesktop.ParseWtsInfoEx(Info(4, 1))));
        check(disconnected is InvalidOperationException && disconnected.Message.Contains("disconnected", StringComparison.OrdinalIgnoreCase),
            "Disconnected desktop session is rejected with a clear native-launch message");

        check(Throws(() => InteractiveDesktop.ParseWtsInfoEx(Info(1, 1)[..16])) is InvalidDataException,
            "Truncated WTSINFOEX data is rejected safely");

        var queryError = InteractiveDesktop.QueryFailure(5);
        check(queryError.NativeErrorCode == 5 && queryError.Message.Contains("verify", StringComparison.OrdinalIgnoreCase),
            "WTS query errors retain their Win32 code and explain that launch was refused");
    }

    private static Exception? Throws(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }
}
