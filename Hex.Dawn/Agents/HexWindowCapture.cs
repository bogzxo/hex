using System.Diagnostics;
using System.Runtime.InteropServices;

using Horizon.Graphics;

namespace Hex.Dawn;

/// <summary>
/// A picture of another program's window, the one being debugged, for an agent to see what it draws. Windows only for
/// now. The window is asked to draw itself into a bitmap (PrintWindow with PW_RENDERFULLCONTENT), which takes a Vulkan or
/// DirectX window's content too and works with the window behind others. A program stopped at a breakpoint can't answer
/// that, so the caller can ask for what's on the screen where its window is instead. Null with why when there's nothing.
/// </summary>
internal static partial class HexWindowCapture
{
    private const uint PW_RENDERFULLCONTENT = 2;
    private const int DIB_RGB_COLORS = 0;
    private const uint SRCCOPY = 0x00CC0020;

    /// <summary>The visible top-level window of a process (or of a process it started), its title and its pixels.</summary>
    public static (ImagePixels? Pixels, string Said) Take(int processId, bool fromScreen = false)
    {
        if (!OperatingSystem.IsWindows())
            return (null, "Pictures of the debugged program's window are only taken on Windows for now.");

        nint window = WindowOf(processId);
        if (window == 0)
            return (null, "The program has no window up (a console program, or one that hasn't opened its window yet).");

        if (!GetClientRect(window, out Rect client) || client.Right - client.Left <= 0 || client.Bottom - client.Top <= 0)
            return (null, "Its window is minimised or has no size.");

        int width = client.Right - client.Left, height = client.Bottom - client.Top;
        nint screen = GetDC(0);
        nint dc = CreateCompatibleDC(screen);
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height,
            Planes = 1,
            BitCount = 32
        };
        nint bitmap = CreateDIBSection(dc, ref header, DIB_RGB_COLORS, out nint bits, 0, 0);
        nint was = SelectObject(dc, bitmap);
        try
        {
            // The client area alone, the frame is Windows' and not the program's. Off the screen it's whatever is over it too
            if (fromScreen)
            {
                var corner = new Point();
                ClientToScreen(window, ref corner);
                if (!BitBlt(dc, 0, 0, width, height, screen, corner.X, corner.Y, SRCCOPY))
                    return (null, "Windows wouldn't copy the screen where the window is.");
            }
            else if (!PrintWindow(window, dc, PW_RENDERFULLCONTENT | 1))
                return (null, "Windows wouldn't draw the window.");

            byte[] pixels = new byte[width * height * 4];
            Marshal.Copy(bits, pixels, 0, pixels.Length);

            // BGRA to RGBA, opaque
            for (int at = 0; at < pixels.Length; at += 4)
            {
                (pixels[at], pixels[at + 2]) = (pixels[at + 2], pixels[at]);
                pixels[at + 3] = 255;
            }

            return (new ImagePixels(width, height, pixels), $"\"{TitleOf(window)}\"");
        }
        finally
        {
            SelectObject(dc, was);
            DeleteObject(bitmap);
            DeleteDC(dc);
            ReleaseDC(0, screen);
        }
    }

    /// <summary>
    /// The program's window brought to the front so the screen can be copied with it on top, and the window that was in
    /// front before handed back to put there again after. 0 when it has no window.
    /// </summary>
    public static nint Raise(int processId)
    {
        if (!OperatingSystem.IsWindows() || WindowOf(processId) is var window && window == 0)
            return 0;

        nint was = GetForegroundWindow();
        SetForegroundWindow(window);
        return was;
    }

    /// <summary>The window that was in front put back there.</summary>
    public static void Restore(nint window)
    {
        if (OperatingSystem.IsWindows() && window != 0)
            SetForegroundWindow(window);
    }

    // The process's own visible top-level window with a title, or else that of a process it started (dotnet run starts the
    // program as a child)
    private static nint WindowOf(int processId)
    {
        var ids = new HashSet<int> { processId };
        try
        {
            foreach (Process each in Process.GetProcesses())
            {
                using (each)
                {
                    if (ParentOf(each.Id) == processId)
                        ids.Add(each.Id);
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        // Every top-level window in turn, the program's own titled one first, a child's titled one next, any of theirs last
        nint found = 0, fallback = 0;
        for (nint window = FindWindowExW(0, 0, null, null); window != 0; window = FindWindowExW(0, window, null, null))
        {
            if (!IsWindowVisible(window))
                continue;
            GetWindowThreadProcessId(window, out uint owner);
            if (!ids.Contains((int)owner))
                continue;
            if (GetWindowTextLengthW(window) > 0)
            {
                if ((int)owner == processId)
                    return window;
                found = found == 0 ? window : found;
            }
            else
            {
                fallback = fallback == 0 ? window : fallback;
            }
        }

        return found != 0 ? found : fallback;
    }

    private static unsafe string TitleOf(nint window)
    {
        char* title = stackalloc char[256];
        int length = GetWindowTextW(window, title, 256);
        return new string(title, 0, Math.Max(0, length));
    }

    // A process's parent by its id, 0 when it can't be read
    private static int ParentOf(int processId)
    {
        nint handle = OpenProcess(0x1000, false, processId);
        if (handle == 0)
            return 0;
        try
        {
            var information = new ProcessBasicInformation();
            return NtQueryInformationProcess(handle, 0, ref information, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0 ? (int)information.InheritedFromUniqueProcessId : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, SizeImage;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ClrUsed, ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public nint ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowExW(nint parent, nint after, string? kind, string? title);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial int GetWindowTextLengthW(nint window);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetWindowTextW(nint window, char* text, int most);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetClientRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PrintWindow(nint window, nint dc, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref Point point);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BitBlt(nint to, int x, int y, int width, int height, nint from, int fromX, int fromY, uint operation);

    [LibraryImport("user32.dll")]
    private static partial nint GetDC(nint window);

    [LibraryImport("user32.dll")]
    private static partial int ReleaseDC(nint window, nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateDIBSection(nint dc, ref BitmapInfoHeader header, int usage, out nint bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    private static partial nint SelectObject(nint dc, nint thing);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint thing);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteDC(nint dc);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int kind, ref ProcessBasicInformation information, int size, out int returned);
}
