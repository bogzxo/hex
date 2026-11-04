using System.Runtime.InteropServices;
using Hex.Shell;

using Key = Silk.NET.Input.Key;

namespace Hex.Dawn;

internal sealed partial class DawnScene
{
    private void TestNativeShortcuts(HexPointerScript test)
    {
        if (!OperatingSystem.IsWindows()) return;
        string file = Path.Combine(testFolder, "native-shortcuts.md");
        int swaps = 0;
        test.Check("native keys: Dawn advertises the new mode shortcut", () => Commands().Any(command =>
            command.Item.Label == Said("menus.dusk_terminal") && command.Item.Shortcut == "Ctrl+Alt+Shift+D"));
        test.Run(() =>
        {
            File.WriteAllText(file, "# Keyboard preview\n\nA document with two views.\n");
            Open(file);
            SwitchView(HexView.Code);
            code.Focus();
            // Keep the real dispatcher and chord, but do not launch another process in an isolated self-test.
            commands.Keys.Add(Key.D, () => { swaps++; return true; }, control: true,
                whileTyping: true, shift: true, alt: true, first: true);
        });
        test.Settle(0.2f);
        test.Run(() => PostNativeChord(0x76)); // F7
        test.Settle(0.2f);
        test.Check("native keys: F7 opens Markdown preview from code", () => MarkdownShown);
        test.Run(() => PostNativeChord(0x76));
        test.Settle(0.2f);
        test.Check("native keys: F7 returns from preview to code", () => document.View == HexView.Code);
        test.Run(() => PostNativeChord(0x11, 0x12, 0x10, 0x44)); // Ctrl+Alt+Shift+D
        test.Settle(0.2f);
        test.Check("native keys: mode shortcut reaches application commands from code", () => swaps == 1);
        test.Run(() => RealKeys.Focus());
        test.Check("native keys: OS input is targeted at the test window", RealKeys.InFront);
        test.Settle(0.5f);
        test.Run(() => RealKeys.Press(0x76));
        test.Settle(0.2f);
        test.Check("native keys: OS F7 opens preview from a focused code editor", () => MarkdownShown);
        test.Run(() => RealKeys.Press(0x76));
        test.Settle(0.2f);
        test.Check("native keys: OS F7 returns to the code editor", () => document.View == HexView.Code);
        test.Run(() => RealKeys.Chord(0x11, 0x12, 0x10, 0x44));
        test.Settle(0.2f);
        test.Check("native keys: OS Ctrl+Alt+Shift+D reaches the mode shortcut", () => swaps == 2);
        test.Run(() => { Open(savedFile); SwitchView(HexView.Code); code.Focus(); });
        test.Settle(0.2f);
        test.Run(() => RealKeys.Press(0x76));
        test.Settle(0.2f);
        test.Check("native keys: OS F7 switches layout source to the designer", () => !document.IsText && document.View == HexView.Design);
        test.Run(() => RealKeys.Press(0x76));
        test.Settle(0.2f);
        test.Check("native keys: OS F7 returns from the designer to layout source", () => document.View == HexView.Code);
        test.Check("native keys: no OS keys were withheld by the foreground guard", () => RealKeys.HeldBack == 0);
        test.Run(() =>
        {
            commands.Keys.Remove(Key.D, control: true, shift: true, alt: true);
            commands.Keys.Add(Key.D, SwapMode, control: true, whileTyping: true, shift: true, alt: true);
        });
    }

    // Post to this test window only. Unlike Keyboard.Tap, these traverse the native window's key callbacks.
    private void PostNativeChord(params uint[] keys)
    {
        nint window = Engine.WindowManager.Window.Native!.Win32!.Value.Hwnd;
        foreach (uint key in keys) Post(key, false);
        for (int i = keys.Length - 1; i >= 0; i--) Post(keys[i], true);
        void Post(uint key, bool up)
        {
            uint bits = 1 | (NativeKeyScan(key, 0) << 16) | (up ? 0xC0000000u : 0);
            if (!NativeKeyPost(window, up ? 0x101u : 0x100u, (nint)key, unchecked((nint)(int)bits)))
                throw new InvalidOperationException("Could not post a key to the self-test window.");
        }
    }

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyW")]
    private static extern uint NativeKeyScan(uint code, uint kind);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool NativeKeyPost(nint window, uint message, nint key, nint data);
}
