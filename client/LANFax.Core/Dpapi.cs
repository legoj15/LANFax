using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LANFax.Core;

/// <summary>Who can decrypt a DPAPI blob (mirrors System.Security.Cryptography.DataProtectionScope, which is a NuGet package).</summary>
internal enum DpapiScope
{
    /// <summary>Only the Windows user who protected it. Used for the token.</summary>
    CurrentUser,
    /// <summary>Any user on this machine. Read only, to migrate a token saved by an older version.</summary>
    LocalMachine,
}

/// <summary>
/// DPAPI via crypt32 P/Invoke. (System.Security.Cryptography.ProtectedData is a NuGet package, not part of the
/// shared framework, and this project allows no packages.)
/// </summary>
internal static class Dpapi
{
    private const uint UiForbidden = 0x1;
    private const uint LocalMachine = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int cbData; public IntPtr pbData; }

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, IntPtr descr, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr descr, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr p);

    public static byte[] Protect(byte[] plain, DpapiScope scope) =>
        Run(plain, protect: true, UiForbidden | (scope == DpapiScope.LocalMachine ? LocalMachine : 0));

    /// <summary>Decrypts a blob of either scope (the blob records its own scope).</summary>
    /// <exception cref="CryptographicException">not decryptable by this user on this machine</exception>
    public static byte[] Unprotect(byte[] blob) => Run(blob, protect: false, UiForbidden);

    private static byte[] Run(byte[] input, bool protect, uint flags)
    {
        var handle = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var inb = new DataBlob { cbData = input.Length, pbData = handle.AddrOfPinnedObject() };
            DataBlob outb;
            bool ok = protect
                ? CryptProtectData(ref inb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, out outb)
                : CryptUnprotectData(ref inb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, flags, out outb);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var bytes = new byte[outb.cbData];
                Marshal.Copy(outb.pbData, bytes, 0, outb.cbData);
                return bytes;
            }
            finally { LocalFree(outb.pbData); }
        }
        finally { handle.Free(); }
    }
}
