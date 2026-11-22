using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Hex.Dawn;

/// <summary>
/// The files and folders macOS asks Hex to open (Finder's Open With, a file dropped on the Dock icon, <c>open -a Hex file</c>),
/// which it says with an Apple Event and not on the command line. Cocoa hands them to the application delegate's
/// <c>application:openFiles:</c>; the delegate is SDL's, which only knows the one file at a time and drops it on the window
/// the Silk window doesn't hear, so the method is added to SDL's delegate class here and the paths go to the same place a
/// line on Hex's pipe does ("open|path", "folder|path"), where Hex opens them on its next tick.
/// </summary>
internal static unsafe class MacOpenDocuments
{
    private static Action<string>? said;

    /// <summary>
    /// Has the paths macOS asks for said to <paramref name="hear"/> as pipe lines, from now on. Once, after the window (and so
    /// SDL's delegate) is there; nothing anywhere but macOS.
    /// </summary>
    public static void Install(Action<string> hear)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        said = hear;
        try
        {
            nint application = Send(GetClass("NSApplication"), Selector("sharedApplication"));
            nint delegateObject = application == 0 ? 0 : Send(application, Selector("delegate"));
            if (delegateObject == 0)
                return;

            nint type = GetObjectClass(delegateObject);
            nint implementation = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&OpenFiles;
            // v@:@@ is void, self, the selector, the application and the array of paths. False when the class has the method already
            AddMethod(type, Selector("application:openFiles:"), implementation, "v@:@@");
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // no Objective-C, nothing opens by Apple Event
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OpenFiles(nint self, nint command, nint application, nint files)
    {
        try
        {
            nuint count = (nuint)Send(files, Selector("count"));
            for (nuint i = 0; i < count; i++)
            {
                nint text = SendIndex(files, Selector("objectAtIndex:"), i);
                string? path = text == 0 ? null : Marshal.PtrToStringUTF8(Send(text, Selector("UTF8String")));
                if (!string.IsNullOrEmpty(path))
                    said?.Invoke((Directory.Exists(path) ? "folder|" : "open|") + path);
            }
        }
        catch (Exception)
        {
            // a path that couldn't be read is a file that isn't opened, not a program that stops
        }

        // The application waits to be told it was heard (NSApplicationDelegateReplySuccess), or it says the files weren't opened
        SendInt(application, Selector("replyToOpenOrPrint:"), 0);
    }

    private static nint Selector(string name) => RegisterName(name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_getClass")]
    private static extern nint GetClass(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "sel_registerName")]
    private static extern nint RegisterName(string name);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "object_getClass")]
    private static extern nint GetObjectClass(nint instance);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "class_addMethod")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AddMethod(nint type, nint selector, nint implementation, string types);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint target, nint selector);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nint SendIndex(nint target, nint selector, nuint index);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern void SendInt(nint target, nint selector, nint value);
}
