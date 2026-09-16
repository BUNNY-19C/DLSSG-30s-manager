using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DLSSGManager;

/// <summary>Detected GPU. Identity is taken from the hardware id where available.</summary>
public sealed record GpuInfo(string Name, string Driver, string Router, string Advice)
{
    /// <summary>PCI device id, e.g. "2208". Null when it could not be read.</summary>
    public string? PciDeviceId { get; init; }

    /// <summary>Architecture derived from the hardware id, e.g. "Ampere".</summary>
    public string? HardwareFamily { get; init; }

    /// <summary>The product name claims a different architecture than the hardware id.</summary>
    public bool NameMismatchesHardware { get; init; }
}

/// <summary>An active display adapter, with the id Windows reports for the physical device.</summary>
public sealed record DisplayAdapter(string Name, string DeviceInstancePath, string? DeviceId);

/// <summary>
/// Display adapter probing through the Win32 display API and the driver registry key. No child
/// process is started for this; it only reads what Windows already knows.
///
/// The architecture is decided from the PCI device id rather than the product name. A name can be
/// edited in the registry (and on this project's development machine, it had been), while the
/// device id is bound to the physical part. Getting this wrong matters: the SM75/SM86 route and the
/// "this GPU does not need the mod" advice both depend on the architecture.
/// </summary>
public static class Gpu
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);

    private const int AttachedToDesktop = 0x00000001;

    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>The NVIDIA adapter in use, if any — same preference <see cref="Probe"/> applies.</summary>
    public static DisplayAdapter? NvidiaAdapter()
    {
        var adapters = Adapters();

        // Prefer the vendor id: a renamed adapter would not necessarily keep "NVIDIA" in its name.
        return adapters.FirstOrDefault(a => a.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
               ?? adapters.FirstOrDefault(a => IsNvidiaDevice(a.DeviceInstancePath));
    }

    /// <summary>
    /// Sanity rules for a display name about to be written into the registry. Returns null when the
    /// name is usable, otherwise a reason in the active language.
    /// </summary>
    public static string? InvalidDisplayNameReason(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return Loc.T("GpuName.Empty");
        if (name!.Length > 127) return Loc.T("GpuName.TooLong");
        if (name.Any(char.IsControl)) return Loc.T("GpuName.ControlChar");
        if (name != name.Trim()) return Loc.T("GpuName.Trim");
        return null;
    }

    /// <summary>Adapters currently attached to the desktop, with their hardware ids.</summary>
    public static List<DisplayAdapter> Adapters()
    {
        var result = new List<DisplayAdapter>();
        var dd = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };

        for (uint i = 0; EnumDisplayDevicesW(null, i, ref dd, 0); i++)
        {
            dd.cb = Marshal.SizeOf<DisplayDevice>();
            if ((dd.StateFlags & AttachedToDesktop) == 0) continue;

            var name = dd.DeviceString ?? "";
            if (name.Length == 0) continue;
            if (name.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
            if (result.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))) continue;

            var path = dd.DeviceID ?? "";
            result.Add(new DisplayAdapter(name, path, ParsePciDeviceId(path)));
        }

        return result;
    }

    public static List<string> AdapterNames() => Adapters().Select(a => a.Name).ToList();

    /// <summary>
    /// Extracts the device id from a device instance path such as
    /// <c>PCI\VEN_10DE&amp;DEV_2208&amp;SUBSYS_...</c>. Null when the path carries no id.
    /// </summary>
    public static string? ParsePciDeviceId(string? deviceInstancePath)
    {
        var m = Regex.Match(deviceInstancePath ?? "", @"DEV_([0-9A-Fa-f]{4})");
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>True when the device path identifies an NVIDIA adapter.</summary>
    public static bool IsNvidiaDevice(string? deviceInstancePath) =>
        (deviceInstancePath ?? "").Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Architecture from the PCI device id.
    ///
    /// NVIDIA assigns ids in contiguous blocks per architecture, so a range test distinguishes
    /// Turing from Ampere from Ada — which is all the route decision needs. Ids outside the known
    /// blocks return null rather than a guess.
    /// </summary>
    public static string? FamilyFromDeviceId(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        if (!ushort.TryParse(deviceId, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
            return null;

        return id switch
        {
            >= 0x1E00 and <= 0x1FFF => "Turing",     // TU10x / TU11x
            >= 0x2180 and <= 0x21FF => "Turing",     // TU117
            >= 0x2200 and <= 0x22FF => "Ampere",     // GA102
            >= 0x2480 and <= 0x25FF => "Ampere",     // GA104 / GA106 / GA107
            >= 0x2680 and <= 0x28FF => "Ada",        // AD102 … AD107
            >= 0x2B80 and <= 0x2CFF => "Blackwell",
            _ => null,
        };
    }

    /// <summary>
    /// Architecture implied by a product name. Used only to cross-check the hardware id, never as
    /// the deciding signal.
    /// </summary>
    public static string? FamilyFromName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Regex.IsMatch(name, @"RTX\s*50\d0")) return "Blackwell";
        if (Regex.IsMatch(name, @"RTX\s*40\d0")) return "Ada";
        if (Regex.IsMatch(name, @"RTX\s*30\d0")) return "Ampere";
        if (Regex.IsMatch(name, @"RTX\s*20\d0")) return "Turing";
        return null;
    }

    /// <summary>
    /// Resolves the architecture to act on, preferring the hardware id, and reports whether the
    /// product name disagrees with it.
    /// </summary>
    public static (string? Family, bool NameMismatch) Classify(string? name, string? deviceId)
    {
        var byHardware = FamilyFromDeviceId(deviceId);
        var byName = FamilyFromName(name);

        var mismatch = byHardware is not null
                       && byName is not null
                       && !string.Equals(byHardware, byName, StringComparison.Ordinal);

        return (byHardware ?? byName, mismatch);
    }

    /// <summary>The mod route for an architecture. Turing is the only family needing the older one.</summary>
    public static string RouteForFamily(string? family) =>
        string.Equals(family, "Turing", StringComparison.Ordinal) ? "SM75" : "SM86";

    public static string RouteForAdapter(string adapterName) => RouteForFamily(FamilyFromName(adapterName));

    /// <summary>
    /// The display-class subkey describing the adapter, matched by hardware id first and by the current
    /// description second — the same policy as <see cref="DriverVersion"/>, so a renamed card is still
    /// found.
    /// </summary>
    private static string? DisplayClassSubKey(string? deviceId, string? currentName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (key is null) return null;

            var subs = key.GetSubKeyNames()
                .Where(s => s.Length == 4 && s.All(char.IsDigit))
                .ToList();

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                foreach (var sub in subs)
                {
                    using var dev = key.OpenSubKey(sub);
                    var match = dev?.GetValue("MatchingDeviceId") as string ?? "";
                    if (match.Contains($"DEV_{deviceId}", StringComparison.OrdinalIgnoreCase)) return sub;
                }
            }

            foreach (var sub in subs)
            {
                using var dev = key.OpenSubKey(sub);
                var desc = dev?.GetValue("DriverDesc") as string ?? "";
                if (desc.Length > 0 && currentName is not null &&
                    desc.Contains(currentName, StringComparison.OrdinalIgnoreCase))
                    return sub;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("定位显卡注册表项失败: " + ex.Message);
        }

        return null;
    }

    /// <summary>The display name Windows and games currently read (the DriverDesc value).</summary>
    public static string? RegistryDisplayName(string? deviceId, string? currentName)
    {
        var sub = DisplayClassSubKey(deviceId, currentName);
        if (sub is null) return null;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Path.Combine(DisplayClassKey, sub));
            return ResolveIndirectString(key?.GetValue("DriverDesc") as string);
        }
        catch (Exception ex)
        {
            AppPaths.Log("读取显卡显示名称失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The adapter's own PnP description, bound to the physical device rather than to what Windows
    /// shows.
    ///
    /// <see cref="DisplayAdapter.DeviceInstancePath"/> stops at the hardware id, while the values
    /// live in the per-instance subkey under it, so the container is searched for the first instance
    /// that carries a description. The stored <c>DeviceDesc</c> is usually an indirect string
    /// pointing at the driver INF; resolving it through the INF's <c>[Strings]</c> table yields the
    /// card's true model name, which is what "restore" writes back.
    /// </summary>
    public static string? PnpDeviceDescription(string? deviceInstancePath)
    {
        if (string.IsNullOrWhiteSpace(deviceInstancePath)) return null;

        try
        {
            using var container = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\" + deviceInstancePath);
            if (container is null) return null;

            var desc = container.GetValue("DeviceDesc") as string;
            if (string.IsNullOrWhiteSpace(desc))
            {
                foreach (var instanceName in container.GetSubKeyNames())
                {
                    using var instance = container.OpenSubKey(instanceName);
                    var candidate = instance?.GetValue("DeviceDesc") as string;
                    if (!string.IsNullOrWhiteSpace(candidate)) { desc = candidate; break; }
                }
            }

            return ResolveIndirectString(desc);
        }
        catch (Exception ex)
        {
            AppPaths.Log("读取显卡真实名称失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// Resolves an INF indirect string of the form <c>@oem24.inf,%nvidia_dev.2208%;NVIDIA GeForce
    /// RTX 4090</c>. The token resolves through the referenced INF's <c>[Strings]</c> table, which is
    /// signed with the driver package and therefore not something a rename tool can rewrite — so it
    /// carries the card's true model name. The text after the last semicolon is only a fallback for
    /// when the INF cannot be found, and is the part a spoofing tool can replace; it is returned only
    /// as a last resort. A value without the <c>@</c> prefix is returned unchanged.
    /// </summary>
    public static string? ResolveIndirectString(string? value, string? infDir = null)
    {
        if (string.IsNullOrWhiteSpace(value) || !value!.StartsWith('@')) return value;

        var semicolon = value.IndexOf(';');
        if (semicolon < 0) return value;

        var head = value[1..semicolon];
        var fallback = value[(semicolon + 1)..];

        var comma = head.IndexOf(',');
        if (comma <= 0) return fallback;

        var infName = head[..comma];
        var token = head[(comma + 1)..].Trim('%');

        try
        {
            var infPath = Path.Combine(infDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF"), infName);
            if (!File.Exists(infPath)) return fallback;

            var resolved = ResolveInfToken(File.ReadLines(infPath), token);
            return resolved ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Looks up a token in an INF's <c>[Strings]</c> section, e.g. <c>NVIDIA_DEV.2208 = "NVIDIA
    /// GeForce RTX 3080 Ti"</c>. Only the Strings section is searched — the same token also appears
    /// as <c>%KEY%</c> on section-mapping lines, which must not match. Tokens are case-insensitive.
    /// </summary>
    public static string? ResolveInfToken(IEnumerable<string> infLines, string token)
    {
        var inStrings = false;

        foreach (var rawLine in infLines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith('['))
            {
                inStrings = line.Equals("[Strings]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inStrings) continue;

            var equals = line.IndexOf('=');
            if (equals <= 0) continue;

            var key = line[..equals].Trim().Trim('%');
            if (!string.Equals(key, token, StringComparison.OrdinalIgnoreCase)) continue;

            var value = line[(equals + 1)..].Trim().Trim('"');
            return value.Length > 0 ? value : null;
        }

        return null;
    }

    /// <summary>
    /// Writes a display name into the display-class registry key (DriverDesc) — the value Windows and
    /// games read. Needs elevation (HKLM). Returns null on success, otherwise a message.
    /// </summary>
    public static string? WriteRegistryDisplayName(string? deviceId, string? currentName, string newName)
    {
        var sub = DisplayClassSubKey(deviceId, currentName);
        if (sub is null) return Loc.T("GpuName.NoKey");

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(Path.Combine(DisplayClassKey, sub), writable: true);
            if (key is null) return Loc.T("GpuName.NoKey");

            key.SetValue("DriverDesc", newName, RegistryValueKind.String);
            return null;
        }
        catch (Exception ex)
        {
            return Loc.T("GpuName.WriteFailed", ex.Message);
        }
    }

    /// <summary>
    /// Driver version for an adapter, read from the display class registry key. Matched on the
    /// hardware id first: the product name in that key can be edited, and on a machine where it has
    /// been, a name match would silently return nothing.
    /// </summary>
    public static string DriverVersion(string adapterName, string? deviceId = null)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (key is null) return "";

            var subs = key.GetSubKeyNames()
                .Where(s => s.Length == 4 && s.All(char.IsDigit))
                .ToList();

            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                foreach (var sub in subs)
                {
                    using var dev = key.OpenSubKey(sub);
                    var match = dev?.GetValue("MatchingDeviceId") as string ?? "";
                    if (match.Contains($"DEV_{deviceId}", StringComparison.OrdinalIgnoreCase))
                        return dev?.GetValue("DriverVersion") as string ?? "";
                }
            }

            foreach (var sub in subs)
            {
                using var dev = key.OpenSubKey(sub);
                var desc = dev?.GetValue("DriverDesc") as string ?? "";
                if (desc.Length > 0 && desc.Contains(adapterName, StringComparison.OrdinalIgnoreCase))
                    return dev?.GetValue("DriverVersion") as string ?? "";
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("读取驱动版本失败: " + ex.Message);
        }

        return "";
    }

    /// <summary>
    /// Whether Windows Hardware-Accelerated GPU Scheduling is on.
    ///
    /// <c>HwSchMode</c>: 1 = off, 2 = on. A missing value means the platform or driver does not
    /// expose the setting at all, which is reported as null so the caller can stay quiet instead of
    /// claiming it is off.
    /// </summary>
    public static bool? HardwareSchedulingEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers");
            var raw = key?.GetValue("HwSchMode");
            if (raw is null) return null;

            return Convert.ToInt32(raw) == 2;
        }
        catch (Exception ex)
        {
            AppPaths.Log("读取硬件加速 GPU 计划状态失败: " + ex.Message);
            return null;
        }
    }

    /// <summary>Advice built from a name alone, used by tests and as a fallback.</summary>
    public static string AdviceForAdapter(string adapterName, string driver) =>
        BuildAdvice(adapterName, driver, FamilyFromName(adapterName), false, null);

    /// <summary>
    /// Rebuilds the advice text for an already-probed adapter.
    ///
    /// The text is produced from code, so it does not follow the interface language on its own.
    /// Re-rendering after a language change needs the same inputs the probe had, which the record
    /// carries.
    /// </summary>
    public static string AdviceFor(GpuInfo info) =>
        BuildAdvice(
            info.Name,
            info.Driver,
            info.HardwareFamily ?? FamilyFromName(info.Name),
            info.NameMismatchesHardware,
            info.PciDeviceId);

    /// <summary>
    /// One-line advice for the toolbar. The full text above explains itself over four or five lines,
    /// which buried the GPU row; the toolbar carries only the driver version and the single fact that
    /// matters, and <see cref="MainWindow"/> puts the full text on the tooltip and in the log.
    /// </summary>
    public static string CompactAdvice(GpuInfo info) =>
        BuildCompactAdvice(
            info.Driver,
            info.HardwareFamily ?? FamilyFromName(info.Name),
            info.NameMismatchesHardware);

    private static string BuildCompactAdvice(string driver, string? family, bool mismatch)
    {
        var parts = new List<string>
        {
            driver.Length > 0 ? Loc.T("Gpu.Driver", driver) : Loc.T("Gpu.DriverUnknown"),
        };

        if (mismatch)
            parts.Add(Loc.T("Gpu.MismatchShort"));
        else if (family is not null)
            parts.Add(Loc.T("Gpu.RouteShort", RouteForFamily(family)));

        return string.Join(" · ", parts);
    }

    public static GpuInfo Probe()
    {
        var nvidia = NvidiaAdapter();

        if (nvidia is null)
        {
            var adapters = Adapters();
            return new GpuInfo(
                adapters.Count > 0 ? string.Join(" / ", adapters.Select(a => a.Name)) : Loc.T("Gpu.NotFound"),
                "",
                "SM86",
                adapters.Count == 0
                    ? Loc.T("Gpu.NoAdapter")
                    : Loc.T("Gpu.NotFoundAdvice"));
        }

        var driver = DriverVersion(nvidia.Name, nvidia.DeviceId);
        var (family, mismatch) = Classify(nvidia.Name, nvidia.DeviceId);

        return new GpuInfo(
            nvidia.Name,
            driver,
            RouteForFamily(family),
            BuildAdvice(nvidia.Name, driver, family, mismatch, nvidia.DeviceId))
        {
            PciDeviceId = nvidia.DeviceId,
            HardwareFamily = FamilyFromDeviceId(nvidia.DeviceId),
            NameMismatchesHardware = mismatch,
        };
    }

    private static string BuildAdvice(string name, string driver, string? family, bool mismatch, string? deviceId)
    {
        var parts = new List<string>
        {
            driver.Length > 0 ? Loc.T("Gpu.Driver", driver) : Loc.T("Gpu.DriverUnknown"),
        };

        if (mismatch)
        {
            var actual = FamilyFromDeviceId(deviceId);
            var series = actual switch
            {
                "Ampere" => Loc.T("Gpu.SeriesAmpere"),
                "Turing" => Loc.T("Gpu.SeriesTuring"),
                "Ada" => Loc.T("Gpu.SeriesAda"),
                "Blackwell" => Loc.T("Gpu.SeriesBlackwell"),
                _ => "",
            };

            parts.Add(Loc.T("Gpu.Mismatch", name, deviceId, actual, series, RouteForFamily(actual)));
        }

        parts.Add(family switch
        {
            "Ada" or "Blackwell" => Loc.T("Gpu.AdviceAda"),
            "Ampere" => Loc.T("Gpu.AdviceAmpere"),
            "Turing" => Loc.T("Gpu.AdviceTuring"),
            _ => Loc.T("Gpu.AdviceUnknown"),
        });

        return string.Join(" ", parts);
    }
}
