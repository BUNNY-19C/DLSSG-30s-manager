using System.IO;
using System.Runtime.InteropServices;

namespace DLSSGManager;

/// <summary>
/// Validates a path before it is handed to the Windows shell.
///
/// This is a guard, not a sanitizer: paths reaching <see cref="Shell"/> come from the user's own
/// library file, their file dialogs, and folders discovered on their disks. The checks below make
/// the trust boundary explicit and reject anything malformed before an OS call sees it, so a
/// strange folder name can never turn into something other than "a path that exists".
/// </summary>
public static class PathGuard
{
    /// <summary>Upper bound from the Windows path limit; anything longer is not a real path.</summary>
    private const int MaxPathLength = 32767;

    /// <summary>
    /// True when the path is a well-formed absolute path to an existing file or directory.
    /// </summary>
    public static bool IsSafe(string? path, out string reason)
    {
        reason = "";

        if (string.IsNullOrWhiteSpace(path))
        {
            reason = Loc.T("Path.Empty");
            return false;
        }

        if (path.Length > MaxPathLength)
        {
            reason = Loc.T("Path.TooLong");
            return false;
        }

        // Leading/trailing whitespace usually means a quoting mistake upstream.
        if (!string.Equals(path, path.Trim(), StringComparison.Ordinal))
        {
            reason = Loc.T("Path.Whitespace");
            return false;
        }

        // Control characters cannot appear in a legitimate Windows path. Quotes are rejected too:
        // every consumer here passes a single path, so a quote is always a symptom of tampering.
        foreach (var c in path)
        {
            if (char.IsControl(c))
            {
                reason = Loc.T("Path.ControlChar");
                return false;
            }

            if (c == '"')
            {
                reason = Loc.T("Path.Quote");
                return false;
            }
        }

        if (!Path.IsPathFullyQualified(path))
        {
            reason = Loc.T("Path.NotAbsolute");
            return false;
        }

        // Let Path.GetFullPath normalize and reject anything malformed; it throws on invalid forms.
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex)
        {
            reason = Loc.T("Path.Invalid", ex.Message);
            return false;
        }

        if (!Directory.Exists(full) && !File.Exists(full))
        {
            reason = Loc.T("Path.Missing");
            return false;
        }

        return true;
    }

    /// <summary>Same as <see cref="IsSafe"/> but additionally requires an <c>.exe</c> target.</summary>
    public static bool IsSafeExecutable(string? path, out string reason)
    {
        if (!IsSafe(path, out reason)) return false;

        if (!File.Exists(path))
        {
            reason = Loc.T("Path.NotFile");
            return false;
        }

        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            reason = Loc.T("Path.NotExecutable");
            return false;
        }

        return true;
    }
}

/// <summary>
/// Opens folders, documents and executables through the Windows shell.
///
/// Uses <c>ShellExecuteExW</c>, the documented API for "open this with its default handler". It takes
/// the target as a single, already-validated file path plus a verb — there is no command line string
/// and no argument vector to build, so there is nothing for a shell to re-parse. Every entry point
/// validates its path with <see cref="PathGuard"/> first and reports failure by returning false.
/// </summary>
public static class Shell
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellExecuteInfo
    {
        public int cbSize;
        public uint fMask;
        public IntPtr hwnd;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpVerb;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpFile;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpParameters;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpDirectory;
        public int nShow;
        public IntPtr hInstApp;
        public IntPtr lpIDList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpClass;
        public IntPtr hkeyClass;
        public uint dwHotKey;
        public IntPtr hIcon;
        public IntPtr hProcess;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShellExecuteExW(ref ShellExecuteInfo info);

    /// <summary>Suppress the shell's own error dialogs; we report failures through the UI instead.</summary>
    private const uint SEE_MASK_FLAG_NO_UI = 0x00000400;

    private const int SW_SHOWNORMAL = 1;

    /// <summary>User dismissed the UAC prompt.</summary>
    private const int ERROR_CANCELLED = 1223;

    private static bool Execute(string verb, string file, string? workingDirectory = null)
    {
        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SEE_MASK_FLAG_NO_UI,
            hwnd = IntPtr.Zero,
            lpVerb = verb,
            lpFile = file,
            lpParameters = null,          // Deliberately unused: callers never pass arguments.
            lpDirectory = workingDirectory,
            nShow = SW_SHOWNORMAL,
        };

        if (ShellExecuteExW(ref info)) return true;

        var error = Marshal.GetLastWin32Error();
        if (error == ERROR_CANCELLED)
        {
            AppPaths.Log($"用户取消了操作（{verb}）：{file}");
        }
        else
        {
            AppPaths.Log($"ShellExecute 失败（{verb}，错误 {error}）：{file}");
        }

        return false;
    }

    /// <summary>Shows a folder in File Explorer.</summary>
    public static bool OpenFolder(string? path)
    {
        if (!PathGuard.IsSafe(path, out var reason))
        {
            AppPaths.Log($"拒绝打开目录：{reason}");
            return false;
        }

        return Execute("open", path!);
    }

    /// <summary>Opens a file with whatever application the user has associated with it.</summary>
    public static bool OpenDocument(string? path)
    {
        if (!PathGuard.IsSafe(path, out var reason))
        {
            AppPaths.Log($"拒绝打开文件：{reason}");
            return false;
        }

        return Execute("open", path!);
    }

    /// <summary>Starts the game, so its own manifest and launcher requirements behave as on a double-click.</summary>
    public static bool LaunchExecutable(string? path)
    {
        if (!PathGuard.IsSafeExecutable(path, out var reason))
        {
            AppPaths.Log($"拒绝启动：{reason}");
            return false;
        }

        return Execute("open", path!, Path.GetDirectoryName(path!));
    }

    /// <summary>
    /// Restarts the manager through the UAC prompt so a write-protected game folder can be written.
    /// The target is always this process's own image, never anything user-supplied.
    /// </summary>
    public static bool RelaunchElevated()
    {
        var exe = Environment.ProcessPath;
        if (!PathGuard.IsSafeExecutable(exe, out var reason))
        {
            AppPaths.Log($"无法提权重启：{reason}");
            return false;
        }

        return Execute("runas", exe!);
    }
}
