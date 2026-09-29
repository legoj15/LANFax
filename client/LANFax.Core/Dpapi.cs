using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace LANFax.Core;

/// <summary>
/// Machine-scope DPAPI via crypt32 P/Invoke. (System.Security.Cryptography.ProtectedData is a NuGet package,
/// not part of the shared framework, and this project allows no packages.)
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

    public static byte[] Protect(byte[] plain) => Run(plain, protect: true);

    /// <exception cref="CryptographicException">data not decryptable on this machine</exception>
    public static byte[] Unprotect(byte[] blob) => Run(blob, protect: false);

    private static byte[] Run(byte[] input, bool protect)
    {
        var handle = GCHandle.Alloc(input, GCHandleType.Pinned);
        try
        {
            var inb = new DataBlob { cbData = input.Length, pbData = handle.AddrOfPinnedObject() };
            DataBlob outb;
            bool ok = protect
                ? CryptProtectData(ref inb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden | LocalMachine, out outb)
                : CryptUnprotectData(ref inb, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out outb);
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
