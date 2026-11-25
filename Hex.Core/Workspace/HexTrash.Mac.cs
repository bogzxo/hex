using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Hex.Core;

// The Trash of a Mac through Foundation's own call, which is what the Finder does: the file lands in ~/.Trash (or the volume's
// .Trashes) and the Finder records where it was, so Put Back works. A move into ~/.Trash alone has nobody to say where it came
// from. Hex.Core doesn't reference the engine's Objective-C bridge, so these are the few calls it needs, blittable and with
// no reflection or delegates, which is what a trimmed native program needs.
internal static unsafe partial class HexTrash
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string Foundation = "/System/Library/Frameworks/Foundation.framework/Foundation";

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint Send(nint receiver, nint selector, nint a);

    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern nint SendBytes(nint receiver, nint selector, byte* a);

    // - (BOOL)trashItemAtURL:(NSURL *)url resultingItemURL:(NSURL **)out error:(NSError **)error
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern byte SendTrash(nint receiver, nint selector, nint url, nint* resulting, nint* error);

    [DllImport(ObjC, EntryPoint = "sel_registerName")]
    private static extern nint SelRegister(byte* name);

    [DllImport(ObjC, EntryPoint = "objc_getClass")]
    private static extern nint GetClass(byte* name);

    [DllImport(ObjC, EntryPoint = "objc_autoreleasePoolPush")]
    private static extern nint PoolPush();

    [DllImport(ObjC, EntryPoint = "objc_autoreleasePoolPop")]
    private static extern void PoolPop(nint pool);

    private static nint Sel(string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* pointer = bytes)
            return SelRegister(pointer);
    }

    private static nint Class(string name)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(name + "\0");
        fixed (byte* pointer = bytes)
            return GetClass(pointer);
    }

    /// <summary>
    /// Has the system put a file or a folder in the Trash, with Put Back. False with <paramref name="why"/> when it wouldn't
    /// (the caller moves it itself). Any thread, NSFileManager is safe on one.
    /// </summary>
    [SupportedOSPlatform("macos")]
    internal static bool TryDiscardNative(string full, out string? why, out string? trashed)
    {
        why = null;
        trashed = null;
        nint pool = PoolPush();
        try
        {
            _ = NativeLibrary.TryLoad(Foundation, out _);
            nint manager = Send(Class("NSFileManager"), Sel("defaultManager"));
            byte[] text = Encoding.UTF8.GetBytes(full + "\0");
            nint url;
            fixed (byte* pointer = text)
                url = Send(Class("NSURL"), Sel("fileURLWithPath:"), SendBytes(Class("NSString"), Sel("stringWithUTF8String:"), pointer));
            if (manager == 0 || url == 0)
            {
                why = "Foundation has no file manager";
                return false;
            }

            nint resulting = 0, error = 0;
            bool done = SendTrash(manager, Sel("trashItemAtURL:resultingItemURL:error:"), url, &resulting, &error) != 0;
            if (done)
            {
                if (resulting != 0)
                {
                    nint path = Send(resulting, Sel("path"));
                    nint utf8 = path == 0 ? 0 : Send(path, Sel("UTF8String"));
                    trashed = utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
                }

                return true;
            }

            nint described = error == 0 ? 0 : Send(error, Sel("localizedDescription"));
            nint chars = described == 0 ? 0 : Send(described, Sel("UTF8String"));
            why = chars == 0 ? "the system wouldn't" : Marshal.PtrToStringUTF8(chars);
            return false;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            why = e.Message;
            return false;
        }
        finally
        {
            PoolPop(pool);
        }
    }
}
