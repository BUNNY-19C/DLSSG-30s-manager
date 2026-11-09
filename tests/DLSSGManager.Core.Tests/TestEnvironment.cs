using System.Runtime.CompilerServices;

namespace DLSSGManager.Tests;

/// <summary>
/// Points the app data folder at a throwaway directory before any test touches <see cref="AppPaths"/>,
/// so tests never read or write the real %APPDATA%\DLSSGManager.
/// </summary>
internal static class TestEnvironment
{
    public static string DataRoot { get; } =
        Path.Combine(Path.GetTempPath(), "DLSSGManager.Tests", Guid.NewGuid().ToString("N"));

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(DataRoot);
        Environment.SetEnvironmentVariable(AppPaths.RootOverrideVariable, DataRoot);
    }
}

/// <summary>A temporary directory removed when the test finishes.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } =
        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DLSSGManager.Tests", "t-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string relative) => System.IO.Path.Combine(Path, relative);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch { /* best effort; the OS temp cleaner gets the rest */ }
    }
}
