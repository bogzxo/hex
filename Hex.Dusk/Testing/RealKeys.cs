using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Hex.Dusk;

/// <summary>
/// Keys pressed for real, through Windows, into Dusk's own window, so the self test goes the whole way a person's
/// keys go (the window's events, the engine's keyboard hook, the compositor, the terminal, the shell) and not round
/// it. Nothing of it on another platform, the keys suite says so and skips.
/// </summary>
internal static partial class RealKeys
{
    public static bool Available => OperatingSystem.IsWindows();

    /// <summary>Brings Dusk's window to the front, which is where the keys go. False if Windows wouldn't.</summary>
    public static bool Focus()
    {
        if (!Available)
            return false;

        nint window = OwnWindow();
        if (window == 0)
            return false;

        SetForegroundWindow(window);
        return GetForegroundWindow() == window;
    }

    /// <summary>Types text, a key a character.</summary>
    public static void Type(string text)
    {
        foreach (char character in text)
            Send(new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = character, dwFlags = KEYEVENTF_UNICODE } } },
                 new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = character, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
    }

    /// <summary>Presses and releases a key by its virtual key code, Enter, Up, Down and the like.</summary>
    public static void Press(ushort virtualKey)
    {
        Send(Down(virtualKey), Up(virtualKey));
    }

    /// <summary>Holds the first keys down, presses the last, and lets them all go in the other order, Ctrl+Shift+T.</summary>
    public static void Chord(params ushort[] keys)
    {
        var inputs = new List<INPUT>();
        foreach (ushort key in keys)
            inputs.Add(Down(key));
        for (int i = keys.Length - 1; i >= 0; i--)
            inputs.Add(Up(keys[i]));
        Send([.. inputs]);
    }

    // A key as a keyboard sends it, the virtual key and its scancode both, GLFW goes by the scancode and a key without
    // one arrives as nothing it knows. The arrows and their neighbours are extended keys, without the flag their
    // scancode is the keypad's
    private static INPUT Down(ushort virtualKey) => Key(virtualKey, 0);

    private static INPUT Up(ushort virtualKey) => Key(virtualKey, KEYEVENTF_KEYUP);

    private static INPUT Key(ushort virtualKey, uint flags)
    {
        uint extended = virtualKey is >= 0x21 and <= 0x28 or 0x2D or 0x2E ? KEYEVENTF_EXTENDEDKEY : 0;
        ushort scan = (ushort)MapVirtualKeyW(virtualKey, MAPVK_VK_TO_VSC);
        return new INPUT { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = virtualKey, wScan = scan, dwFlags = flags | extended } } };
    }

    private const uint MAPVK_VK_TO_VSC = 0;

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint code, uint mapType);

    public const ushort ENTER = 0x0D, LEFT = 0x25, UP = 0x26, RIGHT = 0x27, DOWN = 0x28, BACKSPACE = 0x08, CONTROL = 0x11, SHIFT = 0x10;

    /// <summary>How many keys were held back because Dusk wasn't in front, which a check counts as keys that never arrived.</summary>
    public static int HeldBack { get; private set; }

    /// <summary>Whether Dusk's own window is the one in front this moment.</summary>
    public static bool InFront() => Available && OwnWindow() is var window && window != 0 && GetForegroundWindow() == window;

    // Windows sends keys to whatever window is in front, so every key is sent only once Dusk is seen to be that window.
    // Without this a click somewhere else in the middle of a run had the self test typing into that, a chat window among
    // them, which is how this was found
    private static void Send(params INPUT[] inputs)
    {
        if (!InFront())
        {
            HeldBack++;
            return;
        }

        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static nint OwnWindow()
    {
        nint found = 0;
        uint own = (uint)Environment.ProcessId;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out uint process);
            if (process == own && IsWindowVisible(window))
            {
                found = window;
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public nint dwExtraInfo;
    }

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, INPUT[] inputs, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint process);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);
}
