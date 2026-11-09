using System.Security.Cryptography;

namespace DLSSGManager.Tests;

/// <summary>
/// A fake 0.4.1 payload and a fake game folder in a temporary directory.
///
/// The payload DLLs are random bytes: they carry no signature, so every ownership decision in these
/// tests rests on the hashes the deployment records — exactly the path an unsigned or user-supplied
/// proxy takes in real use. The game folder is nested a few levels deep so the anti-cheat scan, which
/// walks three ancestors up, only ever sees directories this fixture owns.
/// </summary>
internal sealed class DeploymentFixture : IDisposable
{
    private readonly TempDir _temp = new();

    public string PayloadRoot { get; }
    public string GameDir { get; }
    public GameEntry Game { get; }

    public const string Ini041 = """
        ; dlssg_sm86 0.4.1
        [General]
        Enabled=1
        [FrameGeneration]
        Engine=DLSS
        MaxGeneratedFrames=3
        [DLSSG]
        Optimized=1
        Preset=A
        [XeSSFG]
        FlowResolution=Middle
        UseHudless=Auto
        [Runtime]
        Mode=Bundled
        [SuperResolution]
        ; Off, M-INT8, L-INT8, M-FP16, L-FP16
        Mode=Off
        [Logging]
        Level=1
        """;

    public DeploymentFixture(bool superResolution = false)
    {
        PayloadRoot = _temp.File("mod");
        GameDir = _temp.File(Path.Combine("a", "b", "c", "Game"));
        Directory.CreateDirectory(GameDir);

        WritePayload("dlssg_sm86.ini", System.Text.Encoding.UTF8.GetBytes(Ini041));
        // Not a real release label: the payload is random bytes, so no manifest may claim it.
        WritePayload(ModFetcher.VersionMarkerName, "0.4.1-test"u8.ToArray());
        WritePayload("version.dll", Random());
        foreach (var name in new[] { "winmm.dll", "dbghelp.dll", "dinput8.dll", "dxgi.dll", "d3d12.dll" })
            WritePayload(Path.Combine(ModSource.AltDirName, name), Random());
        foreach (var name in ModSource.RequiredRuntimeFiles)
            WritePayload(name, Random());
        if (superResolution)
            foreach (var name in ModSource.SuperResolutionFiles)
                WritePayload(name, Random());

        Game = new GameEntry { Name = "Test Game", RenderDir = GameDir };
    }

    public ModSource Source => new(PayloadRoot);

    public string GamePath(string relative) => Path.Combine(GameDir, relative.Replace('/', Path.DirectorySeparatorChar));

    public void WriteGameFile(string relative, byte[] bytes)
    {
        var path = GamePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void WriteGameFile(string relative, string text) =>
        WriteGameFile(relative, System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>Relative path → SHA-256 of every file under the game folder.</summary>
    public SortedDictionary<string, string> Snapshot()
    {
        var result = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(GameDir, "*", SearchOption.AllDirectories))
            result[Path.GetRelativePath(GameDir, file).Replace('\\', '/')] = DeploymentService.Sha256(file);
        return result;
    }

    /// <summary>Relative paths of every directory under the game folder.</summary>
    public string[] Directories() =>
        Directory.EnumerateDirectories(GameDir, "*", SearchOption.AllDirectories)
            .Select(d => Path.GetRelativePath(GameDir, d).Replace('\\', '/'))
            .OrderBy(d => d, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static byte[] Random(int size = 2048) => RandomNumberGenerator.GetBytes(size);

    private void WritePayload(string relative, byte[] bytes)
    {
        var path = Path.Combine(PayloadRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    public void Dispose() => _temp.Dispose();
}
