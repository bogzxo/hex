using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Hex.Core;

/// <summary>
/// The operating systems' own stores, each by P/Invoke or its command line tool so nothing is added to the build:
/// DPAPI on Windows, the login Keychain on macOS, the secret service on Linux. Each answers null rather than throwing
/// when it isn't there, which is what lets <see cref="Protect"/> fall back to the file.
/// </summary>
internal static partial class HexLinkSecrets
{
    // What's mixed into DPAPI's wrapping, so another program of this user's unwrapping blobs blindly doesn't get ours
    private static readonly byte[] ENTROPY = "Hex Link identity"u8.ToArray();

    // CRYPTPROTECT_UI_FORBIDDEN, never a dialog
    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    // How long secret-tool has to answer, it may be waiting for the person to unlock the keyring
    private const int SECRET_TOOL_MS = 15000;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [SupportedOSPlatform("windows")]
    private static byte[]? DpapiProtect(byte[] secret) => Dpapi(secret, protect: true);

    [SupportedOSPlatform("windows")]
    private static byte[]? DpapiUnprotect(byte[] wrapped) => Dpapi(wrapped, protect: false);

    [SupportedOSPlatform("windows")]
    private static unsafe byte[]? Dpapi(byte[] input, bool protect)
    {
        fixed (byte* inputBytes = input, entropyBytes = ENTROPY)
        {
            var dataIn = new DataBlob { Size = input.Length, Data = (nint)inputBytes };
            var entropy = new DataBlob { Size = ENTROPY.Length, Data = (nint)entropyBytes };
            DataBlob dataOut;
            bool done = protect
                ? CryptProtectData(ref dataIn, 0, ref entropy, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, out dataOut)
                : CryptUnprotectData(ref dataIn, 0, ref entropy, 0, 0, CRYPTPROTECT_UI_FORBIDDEN, out dataOut);
            if (!done)
                return null;

            try
            {
                return new ReadOnlySpan<byte>((void*)dataOut.Data, dataOut.Size).ToArray();
            }
            finally
            {
                LocalFree(dataOut.Data);
            }
        }
    }

    private const string SECURITY = "/System/Library/Frameworks/Security.framework/Security";

    // errSecItemNotFound, the Keychain answered and there's no key yet
    private const int ERR_SEC_ITEM_NOT_FOUND = -25300;

    [LibraryImport(SECURITY)]
    private static unsafe partial int SecKeychainFindGenericPassword(nint keychainOrArray, uint serviceLength, byte* service, uint accountLength, byte* account, out uint passwordLength, out nint password, nint itemRef);

    [LibraryImport(SECURITY)]
    private static unsafe partial int SecKeychainAddGenericPassword(nint keychain, uint serviceLength, byte* service, uint accountLength, byte* account, uint passwordLength, byte* password, nint itemRef);

    [LibraryImport(SECURITY)]
    private static partial int SecKeychainItemFreeContent(nint attributes, nint data);

    [LibraryImport(SECURITY)]
    private static partial int SecKeychainSetUserInteractionAllowed(byte state);

    [LibraryImport(SECURITY)]
    private static partial int SecKeychainGetUserInteractionAllowed(out byte state);

    /// <summary>
    /// The wrapping key in the login Keychain, made and added when there's none and <paramref name="create"/>. Null when
    /// the Keychain can't be reached: locked with nobody to ask (an ssh session), no default keychain, or no Security
    /// framework. The Keychain is asked with its dialogs turned off, so Hex never hangs behind one nobody can see.
    /// </summary>
    [SupportedOSPlatform("macos")]
    private static unsafe byte[]? KeychainKey(bool create)
    {
        try
        {
            byte allowed = 1;
            bool knewAllowed = SecKeychainGetUserInteractionAllowed(out allowed) == 0;
            SecKeychainSetUserInteractionAllowed(0);
            try
            {
                byte[] service = Encoding.UTF8.GetBytes(SERVICE), account = Encoding.UTF8.GetBytes(ACCOUNT);
                fixed (byte* serviceBytes = service, accountBytes = account)
                {
                    int found = SecKeychainFindGenericPassword(0, (uint)service.Length, serviceBytes, (uint)account.Length, accountBytes, out uint length, out nint password, 0);
                    if (found == 0)
                    {
                        try
                        {
                            return length == KEY ? new ReadOnlySpan<byte>((void*)password, (int)length).ToArray() : null;
                        }
                        finally
                        {
                            SecKeychainItemFreeContent(0, password);
                        }
                    }

                    if (found != ERR_SEC_ITEM_NOT_FOUND || !create)
                        return null;

                    byte[] key = RandomNumberGenerator.GetBytes(KEY);
                    fixed (byte* keyBytes = key)
                    {
                        if (SecKeychainAddGenericPassword(0, (uint)service.Length, serviceBytes, (uint)account.Length, accountBytes, KEY, keyBytes, 0) != 0)
                            return null;
                    }

                    return key;
                }
            }
            finally
            {
                SecKeychainSetUserInteractionAllowed(knewAllowed ? allowed : (byte)1);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// The wrapping key kept by the secret service through secret-tool, made and stored when there's none and
    /// <paramref name="create"/>. The key goes to secret-tool on its stdin as base64 and comes back on its stdout, never
    /// on a command line where another user's ps could see it. Null when there's no secret-tool or no service.
    /// </summary>
    [SupportedOSPlatform("linux")]
    private static byte[]? SecretServiceKey(bool create)
    {
        string? found = SecretTool(null, "lookup", "service", SERVICE, "account", ACCOUNT);
        if (found is not null)
        {
            try
            {
                byte[] key = Convert.FromBase64String(found.Trim());
                if (key.Length == KEY)
                    return key;
            }
            catch (FormatException)
            {
            }

            return null;
        }

        if (!create)
            return null;

        byte[] made = RandomNumberGenerator.GetBytes(KEY);
        if (SecretTool(Convert.ToBase64String(made), "store", "--label", SERVICE, "service", SERVICE, "account", ACCOUNT) is null)
            return null;

        // Read back, a store that said yes and kept nothing (no keyring unlocked) mustn't leave a key only memory has
        string? back = SecretTool(null, "lookup", "service", SERVICE, "account", ACCOUNT);
        return back is not null && back.Trim() == Convert.ToBase64String(made) ? made : null;
    }

    // secret-tool with the arguments, what's given on its stdin, what it said on stdout, null when it failed or isn't there
    private static string? SecretTool(string? input, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo("secret-tool")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (string argument in arguments)
                start.ArgumentList.Add(argument);

            using Process? process = Process.Start(start);
            if (process is null)
                return null;

            if (input is not null)
                process.StandardInput.Write(input);
            process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(SECRET_TOOL_MS))
            {
                try { process.Kill(); } catch (InvalidOperationException) { }
                return null;
            }

            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
