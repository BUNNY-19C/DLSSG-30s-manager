using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DLSSGManager.Tests;

/// <summary>
/// Creates an NTFS directory junction. Unlike a symbolic link this needs no administrator rights or
/// developer mode — which is exactly why an unprivileged attacker can plant one, and why the tests
/// that guard against links must use it.
/// </summary>
internal static class Junction
{
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_ALL = 0x7;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    private const uint FSCTL_SET_REPARSE_POINT = 0x000900A4;
    private const uint IO_REPARSE_TAG_MOUNT_POINT = 0xA0000003;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[] inBuffer, int inSize,
        IntPtr outBuffer, int outSize, out int returned, IntPtr overlapped);

    /// <summary>Makes <paramref name="link"/> (which must not exist) a junction to <paramref name="target"/>.</summary>
    public static void Create(string link, string target)
    {
        Directory.CreateDirectory(link);

        var substitute = Encoding.Unicode.GetBytes(@"\??\" + Path.GetFullPath(target).TrimEnd('\\'));
        var print = Encoding.Unicode.GetBytes(Path.GetFullPath(target).TrimEnd('\\'));

        // REPARSE_DATA_BUFFER, mount point variant.
        var pathBuffer = substitute.Length + 2 + print.Length + 2;
        var dataLength = 8 + pathBuffer;
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.Unicode, leaveOpen: true))
        {
            w.Write(IO_REPARSE_TAG_MOUNT_POINT);
            w.Write((ushort)dataLength);
            w.Write((ushort)0);
            w.Write((ushort)0);                              // substitute name offset
            w.Write((ushort)substitute.Length);              // substitute name length
            w.Write((ushort)(substitute.Length + 2));        // print name offset
            w.Write((ushort)print.Length);                   // print name length
            w.Write(substitute);
            w.Write((ushort)0);
            w.Write(print);
            w.Write((ushort)0);
        }
        var buffer = ms.ToArray();

        using var handle = CreateFileW(link, GENERIC_WRITE, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        if (!DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }
}
