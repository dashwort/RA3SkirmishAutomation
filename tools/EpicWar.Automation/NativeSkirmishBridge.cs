using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace EpicWar.Automation;

// ABI 4 preserves the parsed seed on the game thread. Catalog approval is separate.
internal sealed class NativeSkirmishBridge : INativeSkirmishBridge
{
    private string? enginePath, profileDirectory, sessionDirectory;
    private uint stateAddress;
    internal uint AuthorizedSession { get; private set; }
    internal uint AuthorizedGeneration { get; private set; }

    // Internal fixture entry only. Production CLI always verifies the catalog
    // and installation transaction through VerifyInstalled below.
    internal static NativeSkirmishBridge CreateIsolatedTestBridge(string imagePath, string expectedSha256)
    {
        var bridge = new NativeSkirmishBridge();
        if (!Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(imagePath))).Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Isolated test engine hash mismatch.");
        bridge.BindEngine(imagePath);
        return bridge;
    }

    public void VerifyInstalled(string gameRoot, string? backupRoot)
    {
        NativePatchCommands.VerifyInstalled(gameRoot, backupRoot);
        BindEngine(NativePatchCommands.EnginePath(gameRoot));
    }

    private void BindEngine(string imagePath)
    {
        enginePath = Path.GetFullPath(imagePath);
        var image = File.ReadAllBytes(enginePath);
        int pe = checked((int)U32(image, 0x3C)), optional = pe + 24;
        int table = optional + BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 20, 2));
        int count = BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(pe + 6, 2));
        for (int i = 0; i < count; i++)
        {
            int section = table + i * 40;
            if (!image.AsSpan(section, 8).SequenceEqual(".ewstate"u8)) continue;
            if (U32(image, section + 36) != 0xC0000040 || U32(image, section + 8) != 0x4000)
                throw new InvalidDataException("Native state section permissions or ABI reservation differ.");
            stateAddress = checked(U32(image, optional + 28) + U32(image, section + 12));
        }
        if (stateAddress == 0) throw new InvalidDataException("Reviewed native state section is missing.");
    }

    public void ConfigureChild(ProcessStartInfo start, string sessionId, string profile, string presetSha256)
    {
        if (enginePath == null || !Path.GetFullPath(start.FileName).Equals(enginePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Verify the installed native engine before configuring its child.");
        profileDirectory = start.Environment["RA3_AUTOSKIRMISH_PROFILE_DIRECTORY"] ??
            throw new InvalidDataException("Requested profile directory is missing.");
        sessionDirectory = start.Environment.TryGetValue("RA3_AUTOSKIRMISH_SESSION_DIRECTORY", out var directory)
            && !string.IsNullOrWhiteSpace(directory) ? Path.GetFullPath(directory) : null;
        if (!Path.GetFileName(profileDirectory).Equals(profile, StringComparison.OrdinalIgnoreCase) ||
            sessionId.Length is < 1 or > 63 || sessionId.Any(c => c > 127 || c == '\0'))
            throw new InvalidDataException("Native profile/session request is invalid.");
        start.Environment["EPICWAR_NATIVE_SKIRMISH"] = "1";
        start.Environment["EPICWAR_NATIVE_SESSION"] = sessionId;
    }

    public void WaitForGameplay(Process process, string sessionId, string profile, string presetSha256,
        TimeSpan timeout, CancellationToken token)
    {
        if (enginePath == null || profileDirectory == null || stateAddress == 0)
            throw new InvalidOperationException("Native child was not configured.");
        // Query the process image itself. MainModule enumeration can observe
        // the initial loader module before the child has initialized its list.
        var ownedImage = new StringBuilder(32768);
        uint ownedImageLength = (uint)ownedImage.Capacity;
        if (!QueryFullProcessImageNameW(process.Handle, 0, ownedImage, ref ownedImageLength))
            throw new InvalidOperationException($"Could not identify the owned engine process (Win32 {Marshal.GetLastWin32Error()}).");
        if (!string.Equals(ownedImage.ToString(), enginePath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"The owned process image differs from the verified engine: {ownedImage}.");
        var presetPath = Path.Combine(profileDirectory, "Skirmish.ini");
        var preset = File.ReadAllBytes(presetPath);
        if (!Convert.ToHexString(SHA256.HashData(preset)).Equals(presetSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Applied preset hash changed before native verification.");
        var expected = NativeSkirmishPreset.Parse(preset);
        var expectedInfo = expected.GameInfo;
        var expectedMap = expected.Map;
        uint approvedSession = 0, readyFrame = 0;
        var stages = new HashSet<string>(StringComparer.Ordinal);
        void Record(string stage, byte[] snapshot)
        {
            if (!stages.Add(stage)) return;
            var line = JsonSerializer.Serialize(new { schemaVersion = 1, stage, sessionId, processId = process.Id,
                utc = DateTimeOffset.UtcNow, generation = U32(snapshot, 0x10),
                navigationAttempts = U32(snapshot, 0x254), presetParses = U32(snapshot, 0x218),
                startAttempts = U32(snapshot, 0x268), worldInitializations = U32(snapshot, 0x230) });
            if (sessionDirectory != null) File.AppendAllText(Path.Combine(sessionDirectory, "native-events.jsonl"), line + Environment.NewLine);
            Console.Error.WriteLine(line);
        }
        var elapsed = Stopwatch.StartNew();
        while (elapsed.Elapsed < timeout)
        {
            token.ThrowIfCancellationRequested();
            if (process.HasExited) throw new InvalidOperationException($"Native engine exited before match load (exit {process.ExitCode}).");
            var state = Read(process, stateAddress, 0x4000);
            if (U32(state, 0) == 0) { token.WaitHandle.WaitOne(50); continue; }
            if (U32(state, 0) != 0x414E5745 || U32(state, 4) != 4)
                throw new InvalidDataException("Native telemetry ABI differs from the reviewed package.");
            if ((U32(state, 8) & 0x10) == 0 || (U32(state, 8) & 8) != 0)
            { token.WaitHandle.WaitOne(50); continue; }
            if (NativeText(state, 0x70, U32(state, 0x68), 63, false) != sessionId)
            {
                if ((U32(state, 8) & 0x10) == 0) { token.WaitHandle.WaitOne(50); continue; }
                throw new InvalidDataException("Native child session identifier did not match.");
            }
            if (approvedSession == 0 && U32(state, 0x214) == 1 && U32(state, 0x25C) == 1 && U32(state, 0x290) == 1)
            {
                Record("native-navigation-completed", state);
                if (U32(state, 0x200) != 1 || U32(state, 0x218) != 1 || U32(state, 0x210) != 3 ||
                    !NativeText(state, 0x400, U32(state, 0x208), 1023, true).Equals(presetPath, StringComparison.OrdinalIgnoreCase) ||
                    NativeText(state, 0xC00, U32(state, 0x20C), 8191, false) != expectedInfo)
                    throw new InvalidDataException("Native profile or consumed preset did not match the request.");
                approvedSession = U32(state, 0x20);
                if (approvedSession == 0 || U32(state, 0x10) == 0) throw new InvalidDataException("Native lobby generation is missing.");
                if (U32(state, 0x28C) != U32(state, 0x10) || unchecked((int)U32(state, 0x280)) != expected.Seed)
                    throw new InvalidDataException("Native seed restoration does not belong to the current lobby.");
                expected.VerifySession(approvedSession, (address, count) => Read(process, address, count));
                expected.VerifyTemplates((address, count) => Read(process, address, count));
                Record("native-preset-verified", state);
                AuthorizedSession = approvedSession;
                AuthorizedGeneration = U32(state, 0x10);
                var request = BitConverter.GetBytes(U32(state, 0x10));
                if (!WriteProcessMemory(process.Handle, (nint)(stateAddress + 0x260), request, 4, out var written) || written != 4)
                    throw new InvalidOperationException($"Native Start request failed (Win32 {Marshal.GetLastWin32Error()}).");
                Record("native-start-requested", state);
            }
            if (U32(state, 0x268) != 0) Record("native-start-attempted", state);
            if (U32(state, 0x230) > 0)
            {
                if (approvedSession == 0 || U32(state, 0x230) != 1 || U32(state, 0x234) != 2 ||
                    U32(state, 0x238) != approvedSession || U32(state, 0x268) != 1 || U32(state, 0x26C) != 1 ||
                    !NativeText(state, 0x2C00, U32(state, 0x23C), 1023, false).Equals(expectedMap, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Native world completion did not match the authorized skirmish.");
                Record("native-world-initialized", state);
                // Require the normal game loop to advance after world initialization.
                if (readyFrame == 0) readyFrame = U32(state, 0x30);
                else if (U32(state, 0x30) > readyFrame) return;
            }
            token.WaitHandle.WaitOne(50);
        }
        throw new TimeoutException("Native skirmish timed out; inspect navigation, preset and Start telemetry. No UI fallback was used.");
    }

    internal static string NativeText(byte[] state, int offset, uint length, int maximum, bool wide)
    {
        if (length == 0 || length > maximum) throw new InvalidDataException("Native string is empty or truncated.");
        int bytes = checked((int)length * (wide ? 2 : 1));
        int terminator = wide ? 2 : 1;
        if (offset < 0 || (long)offset + bytes + terminator > state.Length ||
            state.AsSpan(offset + bytes, terminator).IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("Native string bounds or terminator are invalid.");
        if (!wide && state.AsSpan(offset, bytes).IndexOfAnyInRange((byte)128, byte.MaxValue) >= 0)
            throw new InvalidDataException("Native narrow string encoding is unsupported; exact verification was refused.");
        return (wide ? Encoding.Unicode : Encoding.ASCII).GetString(state, offset, bytes);
    }
    private static uint U32(byte[] bytes, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
    private static byte[] Read(Process process, uint address, int count)
    {
        var bytes = new byte[count];
        if (!ReadProcessMemory(process.Handle, (nint)address, bytes, (nuint)count, out var read) || read != (nuint)count)
            throw new InvalidOperationException($"Native memory read failed (Win32 {Marshal.GetLastWin32Error()}).");
        return bytes;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, byte[] buffer, nuint count, out nuint read);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(nint process, uint flags, StringBuilder path, ref uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(nint process, nint address, byte[] buffer, nuint count, out nuint written);
}
