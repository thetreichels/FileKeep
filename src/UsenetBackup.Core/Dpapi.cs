using System.Runtime.InteropServices;
using System.Text;

namespace UsenetBackup.Core;

/// <summary>
/// Windows DPAPI wrapper for encrypting credentials (e.g. the NNTP password)
/// so they can be stored in the service config file without sitting in plaintext.
/// </summary>
/// <remarks>
/// Uses <c>CryptProtectData</c>/<c>CryptUnprotectData</c> with the current-user
/// scope: only the service account on this machine can decrypt. If the service
/// account changes, the stored blob becomes unreadable and the password must be
/// re-entered (the UI reports it as unset rather than failing silently).
/// Windows only; throws <see cref="PlatformNotSupportedException"/> elsewhere.
/// </remarks>
public static class Dpapi
{
    /// <summary>Encrypts <paramref name="plaintext"/> for the current user/machine. Returns a base64 blob.</summary>
    public static string Protect(string plaintext)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI credential storage requires Windows.");
        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        var blobIn = ToBlob(plainBytes);
        try
        {
            if (!CryptProtectData(ref blobIn, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                (int)CryptProtectFlags.CRYPTPROTECT_UI_FORBIDDEN, out var blobOut))
                throw new InvalidOperationException($"DPAPI protect failed (Win32 {Marshal.GetLastWin32Error()}).");
            try
            {
                byte[] cipher = new byte[blobOut.cbData];
                Marshal.Copy(blobOut.pbData, cipher, 0, blobOut.cbData);
                return Convert.ToBase64String(cipher);
            }
            finally { LocalFree(blobOut.pbData); }
        }
        finally { ZeroBlob(ref blobIn); }
    }

    /// <summary>Decrypts a base64 blob produced by <see cref="Protect"/>.</summary>
    public static string Unprotect(string protectedBase64)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("DPAPI credential storage requires Windows.");
        byte[] cipher;
        try { cipher = Convert.FromBase64String(protectedBase64); }
        catch (FormatException ex) { throw new InvalidOperationException("Stored credential is not valid base64.", ex); }
        var blobIn = ToBlob(cipher);
        try
        {
            if (!CryptUnprotectData(ref blobIn, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                (int)CryptProtectFlags.CRYPTPROTECT_UI_FORBIDDEN, out var blobOut))
                throw new InvalidOperationException(
                    $"DPAPI unprotect failed (Win32 {Marshal.GetLastWin32Error()}). " +
                    "The credential was encrypted for a different user or machine; re-enter the password.");
            try
            {
                byte[] plain = new byte[blobOut.cbData];
                Marshal.Copy(blobOut.pbData, plain, 0, blobOut.cbData);
                return Encoding.UTF8.GetString(plain);
            }
            finally { ZeroBlob(ref blobOut); }
        }
        finally { ZeroBlob(ref blobIn); }
    }

    private static DATA_BLOB ToBlob(byte[] data)
    {
        IntPtr ptr = Marshal.AllocHGlobal(data.Length);
        Marshal.Copy(data, 0, ptr, data.Length);
        return new DATA_BLOB { cbData = data.Length, pbData = ptr };
    }

    private static void ZeroBlob(ref DATA_BLOB blob)
    {
        if (blob.pbData != IntPtr.Zero)
        {
            // Best-effort wipe of the plaintext copy.
            for (int i = 0; i < blob.cbData; i++)
                Marshal.WriteByte(blob.pbData, i, 0);
            Marshal.FreeHGlobal(blob.pbData);
            blob.pbData = IntPtr.Zero;
            blob.cbData = 0;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        [MarshalAs(UnmanagedType.LPWStr)] string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        out DATA_BLOB pDataOut);

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private enum CryptProtectFlags
    {
        CRYPTPROTECT_UI_FORBIDDEN = 0x1,
    }
}
