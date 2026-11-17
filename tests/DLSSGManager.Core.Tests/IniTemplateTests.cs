namespace DLSSGManager.Tests;

public class IniTemplateTests
{
    private const string Template041 = """
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

    /// <summary>
    /// The 0.4.2 template: same sections as 0.4.1 plus <c>[Logging] Directory</c>, and advanced keys
    /// (<c>FrameTiming</c> is one) that are not in the shipped file at all.
    /// </summary>
    private const string Template042 = """
        ; DLSSG SM86 - frame generation for Ampere (RTX 30) and newer.
        [General]
        ; 1 = frame generation on
        Enabled=1
        [FrameGeneration]
        Engine=DLSS
        MaxGeneratedFrames=3
        [DLSSG]
        Optimized=1
        Preset=A
        [XeSSFG]
        FlowResolution=Middle
        ; Auto = same as 0 (default)
        UseHudless=Auto
        [Logging]
        Level=1
        Directory=dlssg_sm86\logs
        [Runtime]
        Mode=Bundled
        [SuperResolution]
        ; Off, M-INT8, L-INT8, M-FP16, L-FP16
        Mode=Off
        """;

    private static Dictionary<string, string> Parse(string ini)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('[')) { section = line.Trim('[', ']'); continue; }
            var eq = line.IndexOf('=');
            if (eq <= 0 || line.StartsWith(';')) continue;
            result[$"{section}.{line[..eq]}"] = line[(eq + 1)..];
        }
        return result;
    }

    [Fact]
    public void Writes_each_key_into_its_own_section()
    {
        var profile = new GameProfile
        {
            Enabled = false, MaxGeneratedFrames = 4, OptimizedTier = 2, Preset = "C",
            SuperResolutionMode = "L-INT8", LogLevel = 3, FrameTiming = true,
        };

        var ini = Parse(IniTemplate.Render(Template041, profile));

        Assert.Equal("0", ini["General.Enabled"]);
        Assert.Equal("4", ini["FrameGeneration.MaxGeneratedFrames"]);
        Assert.Equal("2", ini["DLSSG.Optimized"]);
        Assert.Equal("C", ini["DLSSG.Preset"]);
        Assert.Equal("Bundled", ini["Runtime.Mode"]);          // upstream value kept
        Assert.Equal("L-INT8", ini["SuperResolution.Mode"]);
        Assert.Equal("3", ini["Logging.Level"]);
        Assert.Equal("1", ini["Logging.FrameTiming"]);
    }

    [Fact]
    public void Legacy_auto_preset_is_written_as_A_on_041()
    {
        var ini = Parse(IniTemplate.Render(Template041, new GameProfile { Preset = "Auto" }));
        Assert.Equal("A", ini["DLSSG.Preset"]);
    }

    [Fact]
    public void Xess_uses_its_own_frame_count()
    {
        var profile = new GameProfile { Engine = "XeSS", MaxGeneratedFrames = 5, XeSSMaxGeneratedFrames = 3 };
        var ini = Parse(IniTemplate.Render(Template041, profile));
        Assert.Equal("3", ini["FrameGeneration.MaxGeneratedFrames"]);
    }

    [Fact]
    public void Legacy_super_resolution_on_must_be_resolved_before_041()
    {
        Assert.Throws<InvalidOperationException>(() =>
            IniTemplate.Render(Template041, new GameProfile { SuperResolutionMode = "On" }));
    }

    [Fact]
    public void Keys_absent_from_the_template_are_not_added()
    {
        var text = IniTemplate.Render(Template041, new GameProfile());
        Assert.DoesNotContain("Router=", text);
        Assert.DoesNotContain("KernelImage=", text);
    }

    [Fact]
    public void The_042_log_directory_key_is_kept_and_the_advanced_timing_key_added()
    {
        var ini = Parse(IniTemplate.Render(Template042, new GameProfile { FrameTiming = true }));

        // New in 0.4.2: the log folder. The manager never chooses it, so upstream's value stays.
        Assert.Equal(@"dlssg_sm86\logs", ini["Logging.Directory"]);
        // FrameTiming is an advanced key the shipped INI leaves out; the manager adds it to [Logging].
        Assert.Equal("1", ini["Logging.FrameTiming"]);
        Assert.Equal("1", ini["Logging.Level"]);
    }

    [Fact]
    public void Hudless_values_are_written_verbatim_on_042()
    {
        // 0.4.2 made Auto behave like 0; all three still round-trip, and nothing rewrites Auto to 1.
        foreach (var mode in new[] { "Auto", "1", "0" })
        {
            var ini = Parse(IniTemplate.Render(Template042, new GameProfile { Engine = "XeSS", HudlessMode = mode }));
            Assert.Equal(mode, ini["XeSSFG.UseHudless"]);
        }
    }

    [Fact]
    public void Unlisted_advanced_keys_the_user_added_survive_a_render()
    {
        var edited = Template042.Replace("Preset=A", "Preset=A\nMvecJittered=1");

        var text = IniTemplate.Render(edited, new GameProfile { Preset = "B" });

        Assert.Contains("MvecJittered=1", text);
        Assert.Contains("Preset=B", text);
    }
}
