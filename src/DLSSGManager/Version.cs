using System.Reflection;

namespace DLSSGManager;

/// <summary>
/// The version shown in the title bar and the header badge. Read from the build's
/// InformationalVersion: release builds get it from the git tag (-p:Version in the workflow), local
/// builds from the csproj Version, which is bumped in the same commit as the release.
///
/// In its own file because the console test project compiles a whitelist of source files and must
/// not pull in the WPF application class.
/// </summary>
public static class AppVersion
{
    // The SDK appends the git commit hash to InformationalVersion ("1.8.3+5bb13c5…"); the interface
    // wants the release number only, so anything from '+' on is cut.
    public static string Label { get; } = "v" + (Assembly.GetEntryAssembly()?
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? "0.0.0").Split('+')[0];
}
