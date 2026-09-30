using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LANFax.Core;

/// <summary>
/// A file held open exclusively (read + delete access, no sharing) from the moment it is claimed until it has been
/// renamed away by the same handle. While held, nobody else can open it: the spooler's next job gets a sharing
/// violation and retries on its own instead of overwriting it. No delete-on-close: if the process dies while holding
/// it, the handle closes and the file stays where it was.
/// </summary>
public sealed class ClaimedFile : IDisposable
{
    private const uint GenericRead = 0x80000000;
    private const uint Delete = 0x00010000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const int FileRenameInfo = 3;
    private const int ErrorFileNotFound = 2, ErrorPathNotFound = 3, ErrorAccessDenied = 5, ErrorSharingViolation = 32;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle h, int infoClass, IntPtr info, uint size);

    private readonly SafeFileHandle _handle;
    private FileStream? _stream;

    private ClaimedFile(SafeFileHandle h, string path) { _handle = h; Path = path; }

    /// <summary>Where the file is now (the claimed path, then the rename target).</summary>
    public string Path { get; private set; }

    /// <summary>
    /// Opens <paramref name="path"/> exclusively, or returns null when it is missing or another handle is open on it
    /// (sharing violation; access denied also covers a file pending deletion). Other failures throw.
    /// </summary>
    public static ClaimedFile? TryOpen(string path) => TryOpen(path, out _);

    /// <inheritdoc cref="TryOpen(string)"/>
    /// <param name="accessDenied">Set when the open failed with access denied: brief for a file pending deletion,
    /// lasting when the ACL keeps this user out (the caller decides when that is an error).</param>
    public static ClaimedFile? TryOpen(string path, out bool accessDenied)
    {
        accessDenied = false;
        var h = CreateFile(path, GenericRead | Delete, 0, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (!h.IsInvalid) return new ClaimedFile(h, path);
        int err = Marshal.GetLastWin32Error();
        h.Dispose();
        accessDenied = err == ErrorAccessDenied;
        if (err is ErrorFileNotFound or ErrorPathNotFound or ErrorSharingViolation or ErrorAccessDenied) return null;
        throw new IOException($"cannot open {path}: {new Win32Exception(err).Message}", HResultFromWin32(err));
    }

    /// <summary>A read stream over the held handle; disposing it is left to <see cref="Dispose"/>.</summary>
    public FileStream Stream => _stream ??= new FileStream(_handle, FileAccess.Read, 1, isAsync: false);

    /// <summary>
    /// Renames the file to <paramref name="dest"/> (same volume, must not exist) through the held handle: the file is
    /// never unlocked between the checks and the move. Throws <see cref="IOException"/> on failure; the file then stays
    /// where it was, still held.
    /// </summary>
    public void RenameTo(string dest)
    {
        var full = System.IO.Path.GetFullPath(dest);
        // NtSetInformationFile takes an NT path; \??\ plus a normal absolute path is one.
        var nt = full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\??\UNC\" + full[2..] : @"\??\" + full;
        var name = Encoding.Unicode.GetBytes(nt);
        // FILE_RENAME_INFO: union{BOOLEAN ReplaceIfExists; DWORD Flags} (4), pad (4 on x64), HANDLE RootDirectory,
        // DWORD FileNameLength, WCHAR FileName[].
        int nameOffset = IntPtr.Size == 8 ? 20 : 12;
        int size = nameOffset + name.Length + 2;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            var zero = new byte[size];
            Marshal.Copy(zero, 0, buf, size);
            Marshal.WriteInt32(buf, 0, 0); // do not replace an existing file
            Marshal.WriteIntPtr(buf, IntPtr.Size == 8 ? 8 : 4, IntPtr.Zero);
            Marshal.WriteInt32(buf, nameOffset - 4, name.Length);
            Marshal.Copy(name, 0, buf + nameOffset, name.Length);
            if (!SetFileInformationByHandle(_handle, FileRenameInfo, buf, (uint)size))
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException($"cannot move {Path} to {dest}: {new Win32Exception(err).Message}", HResultFromWin32(err));
            }
            Path = full;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static int HResultFromWin32(int err) => err <= 0 ? err : unchecked((int)(0x80070000 | (uint)(err & 0xFFFF)));

    public void Dispose()
    {
        if (_stream != null) _stream.Dispose();
        else _handle.Dispose();
    }
}
