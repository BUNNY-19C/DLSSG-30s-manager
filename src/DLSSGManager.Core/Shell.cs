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

    private const uint SEE_MASK_NOCLOSEPROCESS = 0x00000040;
    private const int SW_HIDE = 0;

    /// <summary>Prefix of every helper pipe name; the rest is 32 hex digits.</summary>
    public const string HelperPipePrefix = "DLSSGManager.Elevated.";

    /// <summary>True for exactly the pipe names <see cref="NewHelperPipeName"/> produces.</summary>
    public static bool IsHelperPipeName(string? name) =>
        name is not null &&
        name.Length == HelperPipePrefix.Length + 32 &&
        name.StartsWith(HelperPipePrefix, StringComparison.Ordinal) &&
        name.AsSpan(HelperPipePrefix.Length).IndexOfAnyExcept("0123456789abcdef") < 0;

    public static string NewHelperPipeName() => HelperPipePrefix + Guid.NewGuid().ToString("N");

    /// <summary>Command-line switch that makes the manager run as the elevated helper.</summary>
    public const string ElevatedSwitch = "--elevated";

    /// <summary>
    /// Starts this program again through the UAC prompt in helper mode, passing only the pipe name.
    ///
    /// The executable is this process's own image, and the parameters are the fixed
    /// <see cref="ElevatedSwitch"/> plus a name produced by <see cref="NewHelperPipeName"/> — a fixed
    /// prefix and hex digits — so there is nothing a command-line parser could split or interpret.
    /// Returns the process, or null when the user declined the prompt.
    /// </summary>
    public static System.Diagnostics.Process? StartElevatedHelper(string pipeName)
    {
        if (!IsHelperPipeName(pipeName)) throw new ArgumentException("Invalid helper pipe name.", nameof(pipeName));

        var exe = Environment.ProcessPath;
        if (!PathGuard.IsSafeExecutable(exe, out var reason))
        {
            AppPaths.Log($"无法启动提权助手：{reason}");
            return null;
        }

        var info = new ShellExecuteInfo
        {
            cbSize = Marshal.SizeOf<ShellExecuteInfo>(),
            fMask = SEE_MASK_FLAG_NO_UI | SEE_MASK_NOCLOSEPROCESS,
            lpVerb = "runas",
            lpFile = exe!,
            lpParameters = ElevatedSwitch + " " + pipeName,
            lpDirectory = AppContext.BaseDirectory,
            nShow = SW_HIDE,
        };

        if (!ShellExecuteExW(ref info))
        {
            var error = Marshal.GetLastWin32Error();
            AppPaths.Log(error == ERROR_CANCELLED ? "用户取消了提权" : $"启动提权助手失败（错误 {error}）");
            return null;
        }

        if (info.hProcess == IntPtr.Zero) return null;
        try
        {
            return System.Diagnostics.Process.GetProcessById(GetProcessId(info.hProcess));
        }
        finally
        {
            CloseHandle(info.hProcess);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int GetProcessId(IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

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
