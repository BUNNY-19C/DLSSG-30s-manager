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
}
