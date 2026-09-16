using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DLSSGManager;

/// <summary>
/// Exercises the deployment engine against throwaway folders that mimic real game directories.
/// Nothing here touches a real game; it only uses the shipped DLL bytes to make the signature and
/// hash checks behave exactly as they would in production.
/// </summary>
public static class Program
{
    private static int _pass;
    private static int _fail;
    private static int _skipped;
    private static bool _hasModFiles;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var work = Path.Combine(Path.GetTempPath(), "dlssg_harness_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);

        // Point the app's data folder at our scratch directory *before* anything reads it, so test
        // fixtures and download noise never land in the user's real library or log. Setting an
        // environment variable works because AppPaths resolves Root lazily on first use.
        Environment.SetEnvironmentVariable(AppPaths.RootOverrideVariable, Path.Combine(work, "appdata"));

        // Read-only mode used to check detection against this machine's real game folders.
        if (args.Length > 0 && args[0] == "--scan")
            return RealScan(args.Skip(1).ToArray());

        // Exercises the built-in downloader, proving a fresh clone can obtain the mod files.
        if (args.Length > 0 && args[0] == "--fetch")
            return Fetch(args.Length > 1 ? args[1] : null);

        // Reports each configured download source without downloading 75 MB from all of them.
        if (args.Length > 0 && args[0] == "--sources")
            return ListSources();

        // Locate the mod folder the same way the app does, so the suite works both from a checkout and
        // from a copied build.
        var modRoot = ModSourceLocator.FindExisting(null)
                      ?? ModSourceLocator.ResolveTarget(null);

        _hasModFiles = ModSourceLocator.LooksLikeSource(modRoot);

        Console.WriteLine("Mod 源目录: " + modRoot + (_hasModFiles ? "" : "   ← 尚未获取"));
        Console.WriteLine("测试工作区: " + work);
        Console.WriteLine();

        if (!_hasModFiles)
        {
            Console.WriteLine("注意：Mod 文件尚未获取，依赖这些文件的用例将跳过。");
            Console.WriteLine("      运行  Harness.exe --fetch  可自动下载，或见 docs/mod-files.md。");
            Console.WriteLine();
        }

        try
        {
            TestModSource(modRoot);
            TestIniRendering(modRoot);
            TestGpuProbe();
            TestLocalization();
            TestDeployRestore(modRoot, work);
            TestForeignFileProtection(modRoot, work);
            TestProxyOccupation(modRoot, work);
            TestProxyImport(modRoot, work);
            TestSingleProxyInvariant(modRoot, work);
            TestCustomNamedProxy(modRoot, work);
            TestAdopt(modRoot, work);
            TestDetection(modRoot, work);
            TestModSourceLocator(modRoot, work);
            TestPathGuard(work);
            TestAntiCheat(modRoot, work);
            TestPersistence(work);
            TestUrlPolicy();
            TestCommunityBuildRecognition(work);
            TestHandInstalledExtra(modRoot, work);
            TestSourceSelection();
            TestThemes();
            TestSignatureVerification(modRoot, work);
        }
        catch (Exception ex)
        {
            Console.WriteLine("测试框架异常: " + ex);
            _fail++;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }

        Console.WriteLine();
        var skipped = _skipped > 0 ? $" · 跳过 {_skipped}" : "";
        Console.WriteLine($"===== 通过 {_pass} · 失败 {_fail}{skipped} =====");
        if (_skipped > 0)
            Console.WriteLine($"（跳过的 {_skipped} 项需要 Mod 文件，运行 Harness.exe --fetch 获取后重试）");
        return _fail == 0 ? 0 : 1;
    }

    // ---- download sources ---------------------------------------------------

    /// <summary>
    /// Prints the configured sources and checks that each one's host passes the allow-list and IP
    /// policy. Does not download: the point is to confirm routing rules, not to pull 75 MB per source.
    /// </summary>
    private static int ListSources()
    {
        Console.WriteLine("已配置的下载源（按尝试顺序）:");
        foreach (var s in ModFetcher.AvailableSources)
            Console.WriteLine($"  · [{s.Id}] {s.Name}  ({(s.Official ? "官方" : "镜像")}) — {s.Note}");
        Console.WriteLine();
        Console.WriteLine($"自动模式 Id: {ModFetcher.AutoSourceId}");
        Console.WriteLine();

        Console.WriteLine("地址策略校验:");
        var probes = new (string Label, string Url, bool ExpectAllowed)[]
        {
            ("codeload（官方）",       "https://codeload.github.com/sdli1995/dlssg_for_sm86/zip/refs/heads/main", true),
            ("api.github.com（官方）", "https://api.github.com/repos/sdli1995/dlssg_for_sm86/zipball/main",       true),
            ("raw.githubusercontent", "https://raw.githubusercontent.com/sdli1995/dlssg_for_sm86/main/dlssg_sm86.ini", true),
            ("jsDelivr 镜像",          "https://cdn.jsdelivr.net/gh/sdli1995/dlssg_for_sm86@main/version.dll",   true),
            ("明文 HTTP（应拒绝）",     "http://codeload.github.com/x",                                            false),
            ("非白名单域（应拒绝）",     "https://evil.example.com/x",                                              false),
            ("环回（应拒绝）",          "https://127.0.0.1/x",                                                      false),
            ("内网（应拒绝）",          "https://192.168.1.1/x",                                                    false),
        };

        var ok = true;
        foreach (var (label, url, expectAllowed) in probes)
        {
            var allowed = ModFetcher.IsAllowedAddress(new Uri(url));
            var pass = allowed == expectAllowed;
            if (!pass) ok = false;
            Console.WriteLine($"  [{(pass ? "通过" : "失败")}] {label}：{(allowed ? "允许" : "拒绝")}" +
                              (pass ? "" : $"（期望{(expectAllowed ? "允许" : "拒绝")}）"));
        }

        Console.WriteLine();
        Console.WriteLine(ok ? "===== 地址策略校验通过 =====" : "===== 地址策略校验失败 =====");
        return ok ? 0 : 1;
    }

    // ---- built-in downloader ------------------------------------------------

    /// <summary>
    /// Runs the same download path the app's "从 GitHub 更新 Mod 文件" button uses.
    ///
    /// With no argument it writes to the project's real mod folder (the same target the app would
    /// choose), so running <c>--fetch</c> then the suite actually enables the skipped cases. Passing a
    /// path downloads there instead and leaves it in place for inspection.
    /// </summary>
    private static int Fetch(string? destination)
    {
        var toProjectFolder = destination is null;
        var target = destination ?? ModSourceLocator.ResolveTarget(null);

        Console.WriteLine(toProjectFolder
            ? $"写入项目 Mod 目录: {target}"
            : $"写入指定目录: {target}");
        if (toProjectFolder && ModSourceLocator.LooksLikeSource(target))
            Console.WriteLine("（该目录已有 Mod 文件，将被更新为最新版本）");
        Console.WriteLine();

        var progress = new Progress<string>(text => Console.WriteLine("  " + text));

        // Same probe the application runs before a download, so this path also reports (and records) which
        // release it fetched.
        var detected = ModFetcher.DetectLatestVersionAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (detected is not null) Console.WriteLine("  上游最新版本：" + detected);

        var result = ModFetcher.DownloadIntoAsync(target, progress, CancellationToken.None, versionLabel: detected)
            .GetAwaiter().GetResult();

        foreach (var line in result.Lines) Console.WriteLine("  " + line);

        var source = new ModSource(target);
        Console.WriteLine();
        Console.WriteLine($"结果: {(result.Ok ? "成功" : "失败")} — {result.Message}");
        Console.WriteLine($"源有效性: {source.IsValid}" + (source.IsValid ? $" · 版本 {source.Version}" : $" · {source.ValidationMessage}"));
        Console.WriteLine($"代理入口: {(source.Proxies.Count == 0 ? "(无)" : string.Join("、", source.Proxies))}");

        var ok = result.Ok && source.IsValid && source.Proxies.Count == ModSource.ProxyCandidates.Length;

        Console.WriteLine();
        if (ok)
        {
            Console.WriteLine("===== 下载器验证通过 =====");
            if (toProjectFolder)
            {
                Console.WriteLine("Mod 文件已就位，可重新运行测试（不带参数）执行全部用例。");
            }
            else
            {
                try { Directory.Delete(target, true); } catch { }
                Console.WriteLine("（指定目录已清理）");
            }
        }
        else
        {
            Console.WriteLine("===== 下载器验证失败 =====");
        }

        return ok ? 0 : 1;
    }

    // ---- read-only real-machine scan ---------------------------------------

    private static int RealScan(string[] roots)
    {
        Console.WriteLine("===== Steam 库 =====");
        foreach (var lib in Detection.SteamLibraries()) Console.WriteLine("  " + lib);

        Console.WriteLine();
        Console.WriteLine("===== Steam 扫描 =====");
        var steam = Detection.ScanSteam(new Progress<string>(s => Console.WriteLine("  检查 " + s)));
        Console.WriteLine($"  → 命中 {steam.Count} 个");
        foreach (var g in steam)
            Console.WriteLine($"    · {g.Name}\n        渲染目录 {g.RenderDir}\n        主程序   {g.ExePath}");

        if (roots.Length > 0)
        {
            Console.WriteLine();
            Console.WriteLine("===== 指定目录扫描 =====");
            foreach (var root in roots)
            {
                Console.WriteLine("  " + root);
                if (!Directory.Exists(root)) { Console.WriteLine("    (不存在)"); continue; }

                // Show what the folder resolves to, since that is where both the proxy and the
                // anti-cheat scan operate.
                var (resolved, resolvedExe) = Detection.ResolveRenderDir(root);
                if (!string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase))
                    Console.WriteLine($"    解析到   {resolved}");

                var direct = AntiCheat.Scan(root);
                var afterResolve = AntiCheat.Scan(resolved);
                Console.WriteLine($"    反作弊   {afterResolve.Summary}"
                                  + (afterResolve.IsProtected ? $"  [证据: {afterResolve.Evidence}]" : ""));
                if (afterResolve.HasKernelAntiCheat != direct.HasKernelAntiCheat)
                    Console.WriteLine($"    （若不先解析会漏判：直接扫该目录得到「{direct.Summary}」）");
                if (resolvedExe is not null) Console.WriteLine($"    主程序   {resolvedExe}");

                var hits = Detection.ScanFolder(root);
                foreach (var g in hits)
                {
                    Console.WriteLine($"    · {g.Name}\n        渲染目录 {g.RenderDir}\n        主程序   {g.ExePath}");

                    var quarantined = AntiCheat.FindQuarantinedCopies(g.RenderDir, "version.dll", null);
                    if (quarantined.Count > 0)
                        Console.WriteLine($"        隔离残留 {quarantined.Count} 个");
                }
            }
        }

        return 0;
    }

    // ---- helpers ------------------------------------------------------------

    private static void Check(string name, bool condition, string? detail = null)
    {
        if (condition)
        {
            _pass++;
            Console.WriteLine($"  [通过] {name}");
        }
        else
        {
            _fail++;
            Console.WriteLine($"  [失败] {name}" + (detail is null ? "" : $"  → {detail}"));
        }
    }

    /// <summary>
    /// Marks a case as skipped when the mod files have not been fetched yet, so a fresh clone gets a
    /// meaningful run instead of a wall of failures. Returns true when the caller should return early.
    /// </summary>
    private static bool SkipWithoutModFiles(string section)
    {
        if (_hasModFiles) return false;

        _skipped++;
        Console.WriteLine($"  [跳过] {section}（需要 Mod 文件，尚未获取）");
        return true;
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine("== " + title + " ==");
    }

    /// <summary>
    /// A stand-in mod source with the right shape but tiny files. Lets tests that only care about the
    /// deployment gate (anti-cheat, proxy occupation) run without the real 75 MB download — and, more
    /// importantly, keeps them from passing for the wrong reason when the real files are absent.
    /// </summary>
    private static string MakeSyntheticModSource(string work)
    {
        var root = Path.Combine(work, "SyntheticSource");
        Directory.CreateDirectory(Path.Combine(root, "altnative"));
        Directory.CreateDirectory(Path.Combine(root, "config", "presets"));

        File.WriteAllText(Path.Combine(root, ModSource.IniName),
            "; Native 0.2.3. Synthetic source for tests.\r\n[Compatibility]\r\nRouter=SM86\r\nKernelImage=PTX\r\nHardwareBilinear=0\r\n\r\n[FrameGeneration]\r\nMaxGeneratedFrames=3\r\n\r\n[Logging]\r\nLevel=1\r\n");

        foreach (var name in ModSource.ProxyCandidates)
        {
            var path = ModSource.ResolveDllPath(root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Not a valid PE and not project-signed — enough for the gate, which never inspects the
            // incoming file's signature (only files already present in the game folder).
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(1024));
        }

        return root;
    }

    private static string Sha(string path)
    {
        using var s = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(s));
    }

    /// <summary>Builds a fake game folder: a large exe plus the marker DLL a real game would ship.</summary>
    private static string MakeGameDir(string work, string name, bool withMarker = true)
    {
        var dir = Path.Combine(work, name);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name + ".exe"), RandomNumberGenerator.GetBytes(4096));

        if (withMarker)
        {
            var marker = Path.Combine(dir, "nvngx_dlssg.dll");
            if (!File.Exists(marker)) File.WriteAllBytes(marker, RandomNumberGenerator.GetBytes(2048));
        }

        return dir;
    }

    // ---- tests --------------------------------------------------------------

    private static void TestModSource(string modRoot)
    {
        Section("Mod 文件源识别");
        if (SkipWithoutModFiles("Mod 文件源识别")) return;

        // The version comes from the csproj locally and from the git tag in release builds. The
        // harness is its own assembly, so this can only verify the format and that the commit-hash
        // suffix the SDK appends is stripped; the app's real number is pinned in its csproj.
        Check("版本号格式正确且剥离提交哈希",
            System.Text.RegularExpressions.Regex.IsMatch(AppVersion.Label, @"^v\d+\.\d+\.\d+$"),
            AppVersion.Label);

        var source = new ModSource(modRoot);
        Check("源目录有效", source.IsValid, source.ValidationMessage);

        // Compared against the INI's own banner rather than a hard-coded version: the upstream payload
        // is re-fetched from the network and moves on, and a stale expectation here would fail the
        // suite for a reason that has nothing to do with the manager.
        var bannerVersion = ModSource.ReadVersion(Path.Combine(modRoot, ModSource.IniName));
        Check("版本号从 INI 横幅或版本标记解析",
            source.Version != Loc.T("ModSource.UnknownVersion"),
            "实际: " + source.Version);
        Check("自带的入口全部识别", source.Proxies.Count == ModSource.ProxyCandidates.Length,
            "实际: " + string.Join(",", source.Proxies));
        Check("version.dll 在根目录", File.Exists(Path.Combine(modRoot, "version.dll")));
        Check("altnative 备用入口齐全",
            ModSource.ProxyCandidates.Skip(1).All(n => File.Exists(Path.Combine(modRoot, "altnative", n))),
            string.Join(",", ModSource.ProxyCandidates.Skip(1)
                .Where(n => !File.Exists(Path.Combine(modRoot, "altnative", n)))));

        var bad = new ModSource(Path.Combine(modRoot, "does_not_exist"));
        Check("不存在的目录判为无效", !bad.IsValid);
    }

    private static void TestIniRendering(string modRoot)
    {
        Section("INI 渲染");
        if (SkipWithoutModFiles("INI 渲染")) return;

        var template = File.ReadAllText(Path.Combine(modRoot, "dlssg_sm86.ini"), Encoding.UTF8);

        // 0.3.0 schema: Enabled / Optimized / Preset replaced Router / KernelImage / HardwareBilinear.
        var profile = new GameProfile
        {
            Enabled = false, Optimized = false, Preset = "B", MaxGeneratedFrames = 2, LogLevel = 3,
            Router = "SM75", KernelImage = "Cubin", HardwareBilinear = true,
        };
        var rendered = IniTemplate.Render(template, profile);

        Check("Enabled 已写入", rendered.Contains("Enabled=0"), rendered);
        Check("Optimized 已写入", rendered.Contains("Optimized=0"));
        Check("Preset 已写入", rendered.Contains("Preset=B"));
        Check("MaxGeneratedFrames 已写入", rendered.Contains("MaxGeneratedFrames=2"));
        Check("Level 已写入", rendered.Contains("Level=3"));
        Check("未重复写入键", rendered.Split("MaxGeneratedFrames=").Length == 2, "MaxGeneratedFrames= 出现次数异常");
        Check("保留原有注释", rendered.Contains("DLSSG SM86"));

        // The old schema's keys must not be injected into a payload that does not define them: the manager
        // writes settings the build can actually read, and nothing else.
        Check("不写入旧 schema 的键",
            !rendered.Contains("Router=") && !rendered.Contains("KernelImage=") && !rendered.Contains("HardwareBilinear="));

        // A 0.2.x payload still receives its own keys.
        var legacyTemplate = "; Native 0.2.3.\r\n[Compatibility]\r\nRouter=SM86\r\nKernelImage=PTX\r\nHardwareBilinear=0\r\n\r\n[FrameGeneration]\r\nMaxGeneratedFrames=3\r\n\r\n[Logging]\r\nLevel=1\r\n";
        var legacyRendered = IniTemplate.Render(legacyTemplate, profile);
        Check("旧 payload 仍写入 Router", legacyRendered.Contains("Router=SM75"), legacyRendered);
        Check("旧 payload 仍写入 KernelImage", legacyRendered.Contains("KernelImage=Cubin"));
        Check("旧 payload 仍写入 HardwareBilinear", legacyRendered.Contains("HardwareBilinear=1"));

        var withDiag = IniTemplate.Render(template, new GameProfile { Diagnostics = true });
        Check("诊断段按需添加", withDiag.Contains("[Diagnostics]"));

        var noDiag = IniTemplate.Render(template, new GameProfile());
        Check("默认不含诊断段", !noDiag.Contains("[Diagnostics]"));

        // A user-added diagnostic key inside an existing section must survive a re-render.
        var custom = template + "\r\n[Diagnostics]\r\nPerformance=1\r\n";
        var rerendered = IniTemplate.Render(custom, new GameProfile { LogLevel = 2 });
        Check("用户自定义段保留", rerendered.Contains("Performance=1"));
        Check("重渲染更新 Level", rerendered.Contains("Level=2"));

        // Range clamping keeps a corrupt library file from producing an unreadable INI.
        var clamped = IniTemplate.Render(template, new GameProfile { MaxGeneratedFrames = 99, LogLevel = -5 });
        Check("倍率上限被夹取到 5", clamped.Contains("MaxGeneratedFrames=5"));
        Check("日志级别被夹取到 0", clamped.Contains("Level=0"));
    }

    private static void TestLocalization()
    {
        Section("界面多语言");

        // Both tables must define the same keys. A key present in only one language shows the raw key
        // (or the wrong language) in the interface, so this is the primary guard.
        var (onlyZh, onlyEn) = Strings.MissingKeys();
        Check("两种语言的键完全一致",
            onlyZh.Count == 0 && onlyEn.Count == 0,
            $"仅中文 {onlyZh.Count} 个、仅英文 {onlyEn.Count} 个" +
            (onlyZh.Count > 0 ? "；仅中文: " + string.Join(",", onlyZh.Take(5)) : "") +
            (onlyEn.Count > 0 ? "；仅英文: " + string.Join(",", onlyEn.Take(5)) : ""));

        Console.WriteLine($"      键总数: {Strings.AllKeys.Count()}");

        // Placeholder mismatch means string.Format throws or silently drops a value in one language.
        var placeholderIssues = LocalizationAudit.PlaceholderMismatches();
        Check("两种语言的占位符一致",
            placeholderIssues.Count == 0,
            placeholderIssues.Count > 0 ? string.Join(" | ", placeholderIssues.Take(3)) : "");

        // Every key used in code or XAML must exist in the tables.
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var undefined = LocalizationAudit.UndefinedKeysUsed(LocalizationAudit.SourceFiles(repoRoot));
        Check("代码中引用的键都已定义",
            undefined.Count == 0,
            undefined.Count > 0 ? "未定义: " + string.Join(", ", undefined.Take(8)) : "");

        // Every defined key must resolve to a non-empty string in both languages, and no key may leak
        // through as its own name (which is what Loc.T returns for a missing entry).
        var emptyOrRaw = new List<string>();
        foreach (var language in Languages.All)
        {
            Loc.SetLanguage(language);
            foreach (var key in Strings.AllKeys)
            {
                var text = Loc.T(key);
                if (string.IsNullOrWhiteSpace(text)) emptyOrRaw.Add($"{language}:{key}(空)");
            }
        }
        Check("所有键在两种语言下都有文本", emptyOrRaw.Count == 0,
            emptyOrRaw.Count > 0 ? string.Join(", ", emptyOrRaw.Take(5)) : "");

        // Language normalisation: accept the common spellings, fall back safely on nonsense.
        Check("zh-Hans 归一化为简体中文", Languages.Normalize("zh-Hans") == Languages.ChineseSimplified);
        Check("zh-CN 归一化为简体中文", Languages.Normalize("zh-CN") == Languages.ChineseSimplified);
        Check("en 归一化为英文", Languages.Normalize("en") == Languages.English);
        Check("en-US 归一化为英文", Languages.Normalize("en-US") == Languages.English);
        Check("EN 大小写不敏感", Languages.Normalize("EN") == Languages.English);
        Check("空值回落默认语言", Languages.Normalize("") == Languages.ChineseSimplified);
        Check("null 回落默认语言", Languages.Normalize(null) == Languages.ChineseSimplified);
        Check("无法识别的语言回落默认", Languages.Normalize("klingon") == Languages.ChineseSimplified);
        Check("语言显示名：中文", Languages.DisplayName("zh-Hans") == "简体中文");
        Check("语言显示名：英文", Languages.DisplayName("en") == "English");

        // Switching language must change what lookups return; this is the mechanism the UI relies on.
        Loc.SetLanguage(Languages.ChineseSimplified);
        var zh = Loc.T("Action.Deploy");
        Loc.SetLanguage(Languages.English);
        var en = Loc.T("Action.Deploy");

        Check("切换语言后取到不同文本", zh != en, $"{zh} / {en}");
        Check("中文表返回中文", zh.Contains('部'), zh);
        Check("英文表返回英文", en.Contains("Deploy", StringComparison.OrdinalIgnoreCase), en);
        Check("当前语言已更新", Loc.Current == Languages.English, Loc.Current);

        // Formatted lookups must substitute in both languages.
        Loc.SetLanguage(Languages.ChineseSimplified);
        var zhFmt = Loc.T("Deploy.Success", "TestGame");
        Loc.SetLanguage(Languages.English);
        var enFmt = Loc.T("Deploy.Success", "TestGame");
        Check("中文格式化含参数", zhFmt.Contains("TestGame"), zhFmt);
        Check("英文格式化含参数", enFmt.Contains("TestGame"), enFmt);
        Check("格式化结果随语言变化", zhFmt != enFmt, $"{zhFmt} / {enFmt}");

        // A missing key must degrade to the key itself rather than throwing or blanking the UI. The
        // name is assembled at runtime so the source audit does not flag it as an undefined lookup.
        var absentKey = "No" + ".Such" + ".Key";
        Check("缺失键返回键名而非异常", Loc.T(absentKey) == absentKey);

        // Placeholder-count mismatch is handled without crashing.
        Check("占位符数量不匹配不抛异常", Loc.T("Deploy.Success").Length > 0);

        // The game-name quoting differs by language, which is why it is a key rather than a format
        // string embedded in code.
        Loc.SetLanguage(Languages.ChineseSimplified);
        var zhQuoted = Loc.T("Anti.QuotedName", "Game");
        Loc.SetLanguage(Languages.English);
        var enQuoted = Loc.T("Anti.QuotedName", "Game");
        Check("中文用直角引号", zhQuoted.Contains('「'), zhQuoted);
        Check("英文用弯引号", enQuoted.Contains('“'), enQuoted);

        // Restore the default so later sections see a predictable language.
        Loc.SetLanguage(Languages.ChineseSimplified);

        // Computed properties on GameEntry read from the string table. A language change cannot reach
        // them through the XAML binding — the binding watches the game object, not Loc — so the model
        // re-raises them explicitly. Without that, the game list kept showing the old language while
        // the rest of the window switched, which is exactly what happened in the field.
        var entry = new GameEntry { Name = "Test", RenderDir = @"C:\fake" };

        Loc.SetLanguage(Languages.ChineseSimplified);
        var zhStatus = entry.StatusText;
        var zhSubtitle = entry.Subtitle;

        var notified = new List<string>();
        entry.PropertyChanged += (_, e) => { if (e.PropertyName is not null) notified.Add(e.PropertyName); };

        Loc.SetLanguage(Languages.English);
        entry.RaiseLocalizedText();

        Check("语言变更后通知了 StatusText", notified.Contains("StatusText"), string.Join(",", notified));
        Check("语言变更后通知了 Subtitle", notified.Contains("Subtitle"));
        Check("语言变更后通知了 AntiCheatBody", notified.Contains("AntiCheatBody"));

        var enStatus = entry.StatusText;
        Check("状态文案随语言变化", zhStatus != enStatus, $"{zhStatus} / {enStatus}");

        Loc.SetLanguage(Languages.ChineseSimplified);
        Check("切回中文后恢复中文文案", entry.StatusText == zhStatus, entry.StatusText);
        Check("路径类字段不随语言变化", entry.Subtitle == zhSubtitle, entry.Subtitle);

        // The stored proxy value must not be localised: it is persisted to library.json, so
        // translating it would invalidate existing configuration.
        Check("代理入口的存储值保持中文常量", entry.PreferredProxy == "自动", entry.PreferredProxy);
        Check("代理入口的存储值与语言无关",
            DeploymentService.AutoProxy == "自动", DeploymentService.AutoProxy);
    }

    private static void TestGpuProbe()
    {
        Section("显卡探测");

        var info = Gpu.Probe();
        Console.WriteLine($"      探测结果: {info.Name} | 路由 {info.Router} | 驱动 {info.Driver}");
        Console.WriteLine($"      建议: {info.Advice}");

        // This ran on a specific machine once and asserted "an NVIDIA card must be present", which
        // fails on any build agent (they have no discrete GPU). Probe behaviour is what matters, so
        // the assertions are shaped around what was actually detected.
        var hasNvidia = info.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);

        Check("探测有结果且未抛异常", !string.IsNullOrWhiteSpace(info.Name), info.Name);
        Check("路由取值合法", info.Router is "SM86" or "SM75", info.Router);
        Check("给出了说明文字", !string.IsNullOrWhiteSpace(info.Advice), "(空)");

        if (hasNvidia)
        {
            Console.WriteLine("      （检测到 NVIDIA 显卡，校验路由映射）");
            Check("读到驱动版本", info.Driver.Length > 0, "驱动为空");

            if (info.Name.Contains("RTX 30", StringComparison.OrdinalIgnoreCase))
                Check("RTX 30 系映射到 SM86", info.Router == "SM86", info.Router);
            else if (info.Name.Contains("RTX 20", StringComparison.OrdinalIgnoreCase))
                Check("RTX 20 系映射到 SM75", info.Router == "SM75", info.Router);
        }
        else
        {
            Console.WriteLine("      （无 NVIDIA 显卡，这是 CI 等虚拟环境的正常情况，跳过型号映射校验）");
            Check("无 N 卡时给出可读提示", info.Advice.Length > 0, info.Advice);
        }

        // The mapping itself is pure logic and is verified regardless of the host's hardware.
        Check("型号→路由：RTX 3080 Ti → SM86", Gpu.RouteForAdapter("NVIDIA GeForce RTX 3080 Ti") == "SM86");
        Check("型号→路由：RTX 2080 → SM75", Gpu.RouteForAdapter("NVIDIA GeForce RTX 2080") == "SM75");
        Check("型号→路由：RTX 2080 Ti → SM75", Gpu.RouteForAdapter("NVIDIA GeForce RTX 2080 Ti") == "SM75");
        Check("型号→路由：RTX 4090 → SM86", Gpu.RouteForAdapter("NVIDIA GeForce RTX 4090") == "SM86");
        Check("型号→路由：RTX 5090 → SM86", Gpu.RouteForAdapter("NVIDIA GeForce RTX 5090") == "SM86");
        Check("型号→路由：未知型号回落 SM86", Gpu.RouteForAdapter("Some Unknown Adapter") == "SM86");
        Check("型号→路由：空值不抛异常", Gpu.RouteForAdapter("") == "SM86");

        // Hardware id parsing and architecture blocks. The route decision uses these instead of the
        // product name, because a name can be edited in the registry while the id cannot.
        Check("从设备路径解析硬件 ID",
            Gpu.ParsePciDeviceId(@"PCI\VEN_10DE&DEV_2208&SUBSYS_88021043&REV_A1\4&D0BDF66&0&0009") == "2208",
            Gpu.ParsePciDeviceId(@"PCI\VEN_10DE&DEV_2208&SUBSYS_88021043&REV_A1\4&D0BDF66&0&0009"));
        Check("解析结果统一大写", Gpu.ParsePciDeviceId(@"PCI\VEN_10DE&DEV_2b85&SUBSYS_X") == "2B85");
        Check("无 ID 的路径返回空", Gpu.ParsePciDeviceId(@"PCI\VEN_10DE&SUBSYS_X") is null);
        Check("空路径不抛异常", Gpu.ParsePciDeviceId(null) is null);

        Check("识别 NVIDIA 厂商 ID", Gpu.IsNvidiaDevice(@"PCI\VEN_10DE&DEV_2208"));
        Check("识别非 NVIDIA 厂商 ID", !Gpu.IsNvidiaDevice(@"PCI\VEN_1002&DEV_13C0"));

        Check("硬件 ID 2208 → Ampere", Gpu.FamilyFromDeviceId("2208") == "Ampere", Gpu.FamilyFromDeviceId("2208"));
        Check("硬件 ID 1E04 → Turing", Gpu.FamilyFromDeviceId("1E04") == "Turing", Gpu.FamilyFromDeviceId("1E04"));
        Check("硬件 ID 2684 → Ada", Gpu.FamilyFromDeviceId("2684") == "Ada", Gpu.FamilyFromDeviceId("2684"));
        Check("硬件 ID 2B85 → Blackwell", Gpu.FamilyFromDeviceId("2B85") == "Blackwell", Gpu.FamilyFromDeviceId("2B85"));
        Check("未知硬件 ID 返回空", Gpu.FamilyFromDeviceId("FFFF") is null, Gpu.FamilyFromDeviceId("FFFF"));
        Check("非法硬件 ID 返回空", Gpu.FamilyFromDeviceId("zzzz") is null);
        Check("空硬件 ID 返回空", Gpu.FamilyFromDeviceId("") is null);

        Check("架构→路由：Turing → SM75", Gpu.RouteForFamily("Turing") == "SM75");
        Check("架构→路由：Ampere → SM86", Gpu.RouteForFamily("Ampere") == "SM86");
        Check("架构→路由：Ada → SM86", Gpu.RouteForFamily("Ada") == "SM86");
        Check("架构→路由：未知 → SM86", Gpu.RouteForFamily(null) == "SM86");

        // A renamed adapter: the product name claims one architecture, the hardware id another.
        // This is not hypothetical — the development machine this was written on had exactly this,
        // and the previous name-based logic gave the user wrong advice ("40 series, not needed").
        var (spoofedFamily, spoofedMismatch) = Gpu.Classify("NVIDIA GeForce RTX 4090", "2208");
        Check("伪装显卡：识别为名称与硬件不符", spoofedMismatch);
        Check("伪装显卡：按硬件 ID 判定为 Ampere", spoofedFamily == "Ampere", spoofedFamily);
        Check("伪装显卡：路由取硬件 ID 的 SM86", Gpu.RouteForFamily(spoofedFamily) == "SM86");

        var (agreeFamily, agreeMismatch) = Gpu.Classify("NVIDIA GeForce RTX 3080 Ti", "2208");
        Check("名称与硬件一致时不报警", !agreeMismatch);
        Check("名称与硬件一致时架构正确", agreeFamily == "Ampere", agreeFamily);

        var (noIdFamily, noIdMismatch) = Gpu.Classify("NVIDIA GeForce RTX 4070", null);
        Check("无硬件 ID 时回落到名称判定", noIdFamily == "Ada", noIdFamily);
        Check("无硬件 ID 时不报不符", !noIdMismatch);

        // Hardware-accelerated GPU scheduling: the mod needs it, but a machine may not expose the
        // setting at all, in which case we stay quiet rather than claiming it is disabled.
        var hags = Gpu.HardwareSchedulingEnabled();
        Console.WriteLine("      硬件加速 GPU 计划: " + (hags switch
        {
            true => "已开启",
            false => "未开启",
            null => "平台未提供该设置",
        }));
        Check("HAGS 状态读取不抛异常", true);

        // Wording must stay useful for the families that need special handling.
        Check("RTX 40 系提示无需本 Mod",
            Gpu.AdviceForAdapter("NVIDIA GeForce RTX 4070", "1.2.3").Contains("不需要"));
        Check("RTX 20 系提示 SM75 路由",
            Gpu.AdviceForAdapter("NVIDIA GeForce RTX 2060", "1.2.3").Contains("SM75"));
        Check("无驱动版本时措辞得体",
            Gpu.AdviceForAdapter("NVIDIA GeForce RTX 3080", "").Contains("未知"));

        // The toolbar shows a one-line summary; the full explanation lives on the tooltip and in the
        // log. A compact line must stay short even in the mismatch case, where the full text runs
        // several sentences.
        var compactOk = Gpu.CompactAdvice(new GpuInfo("NVIDIA GeForce RTX 3080 Ti", "566.14", "SM86", "")
        {
            PciDeviceId = "2208",
            HardwareFamily = "Ampere",
        });
        Check("紧凑建议：正常卡显示驱动与路由",
            compactOk.Contains("驱动") && compactOk.Contains("SM86") && compactOk.Length < 60, compactOk);

        var compactMismatch = Gpu.CompactAdvice(new GpuInfo("NVIDIA GeForce RTX 4090", "566.14", "SM86", "")
        {
            PciDeviceId = "2208",
            HardwareFamily = "Ampere",
            NameMismatchesHardware = true,
        });
        Check("紧凑建议：名称不符时只出短句",
            compactMismatch.Contains("不符") && !compactMismatch.Contains("误导"), compactMismatch);

        // Display-name editing: the validation and the INF-string resolution are pure and always
        // testable; the registry reads are machine-dependent, so they only run where an NVIDIA
        // adapter is present.
        Check("名称校验：空", Gpu.InvalidDisplayNameReason("") is not null);
        Check("名称校验：纯空白", Gpu.InvalidDisplayNameReason("   ") is not null);
        Check("名称校验：合法", Gpu.InvalidDisplayNameReason("NVIDIA GeForce RTX 4090") is null);
        Check("名称校验：超长", Gpu.InvalidDisplayNameReason(new string('x', 128)) is not null);
        Check("名称校验：控制字符", Gpu.InvalidDisplayNameReason("RTX\n4090") is not null);
        Check("名称校验：首尾空白", Gpu.InvalidDisplayNameReason(" RTX 4090") is not null);

        // The PnP DeviceDesc is an indirect string into the driver INF; the INF is signed with the
        // driver package, so its [Strings] table still holds the true model name even when the
        // display name was renamed. Resolution must prefer that over the spoofable fallback text.
        var infDir = Path.Combine(Path.GetTempPath(), "dlssg_inf_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(infDir);
        try
        {
            File.WriteAllText(Path.Combine(infDir, "oem24.inf"),
                "[Strings]\r\nNVIDIA_DEV.2208 = \"NVIDIA GeForce RTX 3080 Ti\"\r\n");

            Check("间接字符串解析到 INF 真名",
                Gpu.ResolveIndirectString("@oem24.inf,%nvidia_dev.2208%;NVIDIA GeForce RTX 4090", infDir)
                    == "NVIDIA GeForce RTX 3080 Ti");
            Check("INF 缺失时退回备用名",
                Gpu.ResolveIndirectString("@missing.inf,%x%;NVIDIA GeForce RTX 4090", infDir)
                    == "NVIDIA GeForce RTX 4090");
            Check("普通字符串原样返回",
                Gpu.ResolveIndirectString("NVIDIA GeForce RTX 4090") == "NVIDIA GeForce RTX 4090");
            Check("Strings 段外同名 token 不误匹配",
                Gpu.ResolveInfToken(new[]
                {
                    "[NVIDIA_Devices.NTamd64]",
                    "%NVIDIA_DEV.2208% = Section040, PCI\\VEN_10DE&DEV_2208",
                    "[Strings]",
                    "NVIDIA_DEV.2209 = \"NVIDIA GeForce RTX 4090\"",
                }, "NVIDIA_DEV.2208") is null);
        }
        finally
        {
            try { Directory.Delete(infDir, recursive: true); } catch { /* best effort */ }
        }

        if (hasNvidia)
        {
            var adapter = Gpu.NvidiaAdapter();
            Check("定位到 NVIDIA 适配器", adapter is not null);
            if (adapter is not null)
            {
                var reg = Gpu.RegistryDisplayName(adapter.DeviceId, adapter.Name);
                var pnp = Gpu.PnpDeviceDescription(adapter.DeviceInstancePath);
                Console.WriteLine($"      注册表显示名: {reg ?? "(无)"}   PnP 真实名: {pnp ?? "(无)"}");
                Check("注册表显示名可读", reg is not null);
                Check("PnP 真实名可读", pnp is not null);
            }
        }
    }

    private static void TestDeployRestore(string modRoot, string work)
    {
        Section("部署 → 恢复（干净目录）");
        if (SkipWithoutModFiles("部署 → 恢复")) return;

        var dir = MakeGameDir(work, "GameClean");
        var source = new ModSource(modRoot);
        var game = new GameEntry
        {
            Name = "GameClean",
            RenderDir = dir,
            PreferredProxy = DeploymentService.AutoProxy,
            Profile = new GameProfile { Router = "SM86", MaxGeneratedFrames = 3, LogLevel = 1 },
        };

        var deploy = DeploymentService.Deploy(game, source);
        Check("部署成功", deploy.Ok, deploy.Message);
        Check("写入 version.dll", File.Exists(Path.Combine(dir, "version.dll")));
        Check("写入 dlssg_sm86.ini", File.Exists(Path.Combine(dir, ModSource.IniName)));
        Check("记录已建立", game.Deployment is not null);
        Check("记录的入口名正确", game.Deployment?.ProxyName == "version.dll", game.Deployment?.ProxyName);
        Check("记录版本号", game.Deployment?.ModVersion == source.Version, game.Deployment?.ModVersion);
        Check("DLL 已带项目签名", DeploymentService.IsProjectSigned(Path.Combine(dir, "version.dll")));
        Check("部署的 DLL 与源文件一致",
            Sha(Path.Combine(dir, "version.dll")) == Sha(Path.Combine(modRoot, "version.dll")));
        Check("INI 内容符合配置",
            File.ReadAllText(Path.Combine(dir, ModSource.IniName)).Contains("MaxGeneratedFrames=3"));

        DeploymentService.Check(game);
        Check("状态判定为已部署", game.Status == GameStatus.Deployed, game.StatusText + " / " + game.StatusDetail);

        // Tampering with the INI should be reported, not silently ignored.
        File.AppendAllText(Path.Combine(dir, ModSource.IniName), "\r\n; user edit\r\n");
        DeploymentService.Check(game);
        Check("INI 被改动后状态变为已改动", game.Status == GameStatus.Modified, game.StatusText);

        var restore = DeploymentService.Restore(game, removeLogs: false);
        Check("恢复成功", restore.Ok, restore.Message);
        Check("version.dll 已移除", !File.Exists(Path.Combine(dir, "version.dll")));
        Check("INI 已移除", !File.Exists(Path.Combine(dir, ModSource.IniName)));
        Check("原游戏 exe 未受影响", File.Exists(Path.Combine(dir, "GameClean.exe")));
        Check("marker DLL 未受影响", File.Exists(Path.Combine(dir, "nvngx_dlssg.dll")));
        Check("部署记录已清除", game.Deployment is null);

        DeploymentService.Check(game);
        Check("恢复后状态为未部署", game.Status == GameStatus.NotDeployed, game.StatusText);
    }

    private static void TestForeignFileProtection(string modRoot, string work)
    {
        Section("保护其他 Mod 的文件（不得误删）");
        if (SkipWithoutModFiles("保护其他 Mod 的文件")) return;

        var dir = MakeGameDir(work, "GameForeign");
        var source = new ModSource(modRoot);

        // Another mod occupies dxgi.dll and the INI slot already holds an unrelated config.
        var foreignDxgi = Path.Combine(dir, "dxgi.dll");
        File.WriteAllBytes(foreignDxgi, RandomNumberGenerator.GetBytes(3072));
        var foreignIni = Path.Combine(dir, ModSource.IniName);
        File.WriteAllText(foreignIni, "; somebody else's config\n[Other]\r\nKey=1\r\n");
        var foreignDxgiHash = Sha(foreignDxgi);
        var foreignIniHash = Sha(foreignIni);

        var game = new GameEntry
        {
            Name = "GameForeign",
            RenderDir = dir,
            PreferredProxy = DeploymentService.AutoProxy,
            Profile = new GameProfile(),
        };

        var deploy = DeploymentService.Deploy(game, source);
        Check("自动避开被占用的 dxgi.dll", deploy.Ok && game.Deployment?.ProxyName != "dxgi.dll",
            "实际入口: " + game.Deployment?.ProxyName);
        Check("其他 Mod 的 dxgi.dll 未被覆盖", Sha(foreignDxgi) == foreignDxgiHash);
        Check("其他 Mod 的 dxgi.dll 未被删除", File.Exists(foreignDxgi));

        var restoreResult = DeploymentService.Restore(game, removeLogs: false);
        Check("恢复成功（含还原）", restoreResult.Ok, restoreResult.Message);
        Check("其他 Mod 的 dxgi.dll 仍在", File.Exists(foreignDxgi));
        Check("其他 Mod 的 dxgi.dll 内容未变", Sha(foreignDxgi) == foreignDxgiHash);
        Check("原来的 INI 未被当作本项目的删除", File.Exists(foreignIni), "INI 被误删");
        Check("原来的 INI 内容未变", Sha(foreignIni) == foreignIniHash);
    }

    private static void TestProxyOccupation(string modRoot, string work)
    {
        Section("五个入口名全被占用");

        var dir = MakeGameDir(work, "GameBlocked");
        foreach (var name in ModSource.ProxyCandidates)
            File.WriteAllBytes(Path.Combine(dir, name), RandomNumberGenerator.GetBytes(1024));

        var hashes = ModSource.ProxyCandidates.ToDictionary(n => n, n => Sha(Path.Combine(dir, n)));

        var game = new GameEntry { Name = "GameBlocked", RenderDir = dir, PreferredProxy = DeploymentService.AutoProxy };
        var result = DeploymentService.Deploy(game, new ModSource(MakeSyntheticModSource(work)));

        Check("部署被拒绝而不是覆盖", !result.Ok, "居然成功了");
        Check("给出了解决提示", result.Message.Contains("占用"), result.Message);
        Check("所有占用文件保持原样",
            ModSource.ProxyCandidates.All(n => Sha(Path.Combine(dir, n)) == hashes[n]));

        // Every deployable name is occupied, so no name at all is left: the manager reports the conflict
        // rather than inventing one. (0.3.0 ships six entry points, and each is deployed from the file
        // built for that name — the file and the name can no longer be mismatched.)
        Check("没有留下任何新文件",
            Directory.GetFiles(dir).Length == ModSource.ProxyCandidates.Length + 2,   // + 游戏 exe 与 marker
            string.Join(",", Directory.GetFiles(dir).Select(Path.GetFileName)));
    }

    /// <summary>
    /// Adding a proxy DLL the user supplies: the entry name is the file name, so the file is copied into
    /// the mod folder under that name and deployed like the bundled entries. No mod download is needed —
    /// the mechanics are the point, not the bytes.
    /// </summary>
    private static void TestProxyImport(string modRoot, string work)
    {
        Section("添加代理 DLL（本地入口）");

        Check("winhttp.dll 属于已知入口名", ModSource.IsKnownProxyName("winhttp.dll"));
        Check("入口名匹配不区分大小写", ModSource.IsKnownProxyName("WINHTTP.DLL"));
        // 0.3.0 ships d3d12.dll and dbghelp.dll itself; winhttp.dll left the release but stays known, so
        // it is the name an import can legitimately take.
        Check("d3d12.dll 是自带入口", ModSource.ProxyCandidates.Contains("d3d12.dll"));
        Check("winhttp.dll 不是自带入口", !ModSource.ProxyCandidates.Contains("winhttp.dll"));

        var source = MakeSyntheticModSource(work);
        var before = new ModSource(source);
        Check("初始没有本地入口", before.ImportedProxies.Count == 0, string.Join("、", before.ImportedProxies));
        Check("可用入口初始为自带五个", before.AvailableProxies.Count == ModSource.ProxyCandidates.Length,
            string.Join("、", before.AvailableProxies));

        // A community build: a plausible entry name, and no signature this project can vouch for. The
        // file name *is* the entry name, so the fixture has to carry the real one.
        var localDir = Path.Combine(work, "LocalBuild");
        Directory.CreateDirectory(localDir);
        var local = Path.Combine(localDir, "winhttp.dll");
        File.WriteAllBytes(local, RandomNumberGenerator.GetBytes(4096));

        var add = ModSource.ImportProxy(source, local);
        Check("导入成功", add.Ok, add.Message);
        Check("以原文件名落在 altnative 下", File.Exists(Path.Combine(source, "altnative", "winhttp.dll")));
        Check("内容与所选文件一致", Sha(Path.Combine(source, "altnative", "winhttp.dll")) == Sha(local));

        var after = new ModSource(source);
        Check("本地入口被识别", after.ImportedProxies.Count == 1 && after.ImportedProxies[0] == "winhttp.dll",
            string.Join("、", after.ImportedProxies));
        Check("可用入口 = 自带 + 本地", after.AvailableProxies.Count == ModSource.ProxyCandidates.Length + 1,
            string.Join("、", after.AvailableProxies));
        Check("自带入口不受影响", after.Proxies.Count == ModSource.ProxyCandidates.Length,
            string.Join("、", after.Proxies));
        Check("本地入口排在自己几个之后",
            after.AvailableProxies.Take(ModSource.ProxyCandidates.Length).SequenceEqual(ModSource.ProxyCandidates));

        // The project's own builds must never be replaced by an import: their signature is what every
        // ownership check rests on.
        var clash = Path.Combine(work, "winmm.dll");
        File.WriteAllBytes(clash, RandomNumberGenerator.GetBytes(512));
        var reserved = ModSource.ImportProxy(source, clash);
        Check("拒绝覆盖自带入口名", !reserved.Ok, reserved.Message);
        Check("自带入口内容未变", Sha(Path.Combine(source, "altnative", "winmm.dll")) != Sha(clash));

        var text = Path.Combine(work, "notes.txt");
        File.WriteAllText(text, "not a proxy");
        Check("拒绝非 DLL 文件", !ModSource.ImportProxy(source, text).Ok);
        Check("拒绝不存在的文件", !ModSource.ImportProxy(source, Path.Combine(work, "nope.dll")).Ok);

        // Deploy the imported entry, then remove it again with the normal restore path.
        var dir = MakeGameDir(work, "GameImportedProxy");
        var game = new GameEntry { Name = "GameImportedProxy", RenderDir = dir, PreferredProxy = "winhttp.dll" };

        var deploy = DeploymentService.Deploy(game, new ModSource(source));
        Check("部署本地入口成功", deploy.Ok, deploy.Message);
        Check("入口名记录为 winhttp.dll", game.Deployment?.ProxyName == "winhttp.dll", game.Deployment?.ProxyName);
        Check("游戏目录里出现 winhttp.dll", File.Exists(Path.Combine(dir, "winhttp.dll")));
        Check("部署内容与本地代理一致", Sha(Path.Combine(dir, "winhttp.dll")) == Sha(local));
        Check("代理与 INI 都记了指纹", game.Deployment!.Files.Count == 2, "实际: " + game.Deployment.Files.Count);

        DeploymentService.Check(game);
        Check("状态为已部署", game.Status == GameStatus.Deployed, game.StatusText + " / " + game.StatusDetail);

        var restore = DeploymentService.Restore(game, removeLogs: false);
        Check("一键恢复成功", restore.Ok, restore.Message);
        Check("一键恢复删除了 winhttp.dll", !File.Exists(Path.Combine(dir, "winhttp.dll")));
        Check("INI 也已删除", !File.Exists(Path.Combine(dir, ModSource.IniName)));

        DeploymentService.Check(game);
        Check("恢复后状态为未部署", game.Status == GameStatus.NotDeployed, game.StatusText);

        // An imported proxy next to a project-signed one is still two proxies live at once — the crash
        // the single-proxy invariant exists for. Needs the real signed payload.
        if (!_hasModFiles)
        {
            _skipped++;
            Console.WriteLine("  [跳过] 本地入口与自带入口冲突（需要 Mod 文件）");
            return;
        }

        var conflictDir = MakeGameDir(work, "GameImportConflict");
        var conflict = new GameEntry { Name = "GameImportConflict", RenderDir = conflictDir, PreferredProxy = "d3d12.dll" };
        var conflictDeploy = DeploymentService.Deploy(conflict, new ModSource(source));
        Check("冲突场景：先部署本地入口", conflictDeploy.Ok, conflictDeploy.Message);

        File.Copy(Path.Combine(modRoot, "version.dll"), Path.Combine(conflictDir, "version.dll"), overwrite: true);

        DeploymentService.Check(conflict);
        Check("两种代理同时存在时被判定为异常",
            conflict.Status == GameStatus.Modified && conflict.StatusDetail.Contains("代理"),
            conflict.StatusText + " / " + conflict.StatusDetail);

        var conflictRestore = DeploymentService.Restore(conflict, removeLogs: false);
        Check("恢复后两个代理都已清除", conflictRestore.Ok &&
            !File.Exists(Path.Combine(conflictDir, "d3d12.dll")) &&
            !File.Exists(Path.Combine(conflictDir, "version.dll")),
            conflictRestore.Message);
    }

    /// <summary>
    /// Regression for a crash caused by two proxies being live at once.
    ///
    /// The mod requires exactly one proxy in the game folder — the game loads every entry name it
    /// recognises, so a second one runs a second inference pipeline. This happened for real when the
    /// library was reset (losing the deployment record), and the next deploy then picked a free name
    /// instead of reusing the installed proxy, leaving both in place.
    /// </summary>
    private static void TestSingleProxyInvariant(string modRoot, string work)
    {
        Section("游戏目录只应存在一个本项目代理");
        if (SkipWithoutModFiles("单一代理约束")) return;

        var source = new ModSource(modRoot);
        var dir = MakeGameDir(work, "GameOneProxy");

        // First deployment: takes the default entry.
        var game = new GameEntry { Name = "GameOneProxy", RenderDir = dir, PreferredProxy = DeploymentService.AutoProxy };
        var first = DeploymentService.Deploy(game, source);
        Check("首次部署成功", first.Ok, first.Message);
        var firstProxy = game.Deployment?.ProxyName ?? "";
        Check("首次部署使用默认入口 version.dll", firstProxy == "version.dll", firstProxy);

        // Simulate the library being reset: the record is gone, but the file is still in the game
        // folder. The next deploy must adopt it rather than installing a second name.
        game.Deployment = null;
        var second = DeploymentService.Deploy(game, source);
        Check("丢失记录后再次部署仍成功", second.Ok, second.Message);
        Check("复用已安装的入口而非另选一个",
            game.Deployment?.ProxyName == firstProxy,
            $"首次 {firstProxy} → 再次 {game.Deployment?.ProxyName}");

        var ours = ModSource.ProxyCandidates
            .Where(n => File.Exists(Path.Combine(dir, n)) && DeploymentService.IsProjectSigned(Path.Combine(dir, n)))
            .ToList();
        Check("游戏目录里只有一个本项目代理", ours.Count == 1, string.Join("、", ours));

        // Even if a stray second proxy is planted (as happened in the field), deploying again must
        // clean it up rather than leaving both.
        var stray = ModSource.ProxyCandidates.First(n => !string.Equals(n, firstProxy, StringComparison.OrdinalIgnoreCase));
        File.Copy(Path.Combine(dir, firstProxy), Path.Combine(dir, stray), overwrite: true);
        Check("已埋入第二个代理以便验证清理",
            File.Exists(Path.Combine(dir, stray)) && DeploymentService.IsProjectSigned(Path.Combine(dir, stray)));

        // While both are present, the status must call it out — the user needs to know before
        // launching the game, not after it crashes.
        var planted = new GameEntry { Name = game.Name, RenderDir = dir, Deployment = game.Deployment };
        DeploymentService.Check(planted);
        Console.WriteLine("        状态: " + planted.StatusText + " — " + planted.StatusDetail);
        Check("两个代理共存时状态提示异常",
            planted.StatusDetail.Contains("代理"), planted.StatusDetail);
        Check("状态不是正常的已部署", planted.Status != GameStatus.Deployed, planted.StatusText);

        game.Deployment = null;
        var third = DeploymentService.Deploy(game, source);
        Check("再次部署成功", third.Ok, third.Message);
        Check("多余的代理被清除", !File.Exists(Path.Combine(dir, stray)), stray);

        ours = ModSource.ProxyCandidates
            .Where(n => File.Exists(Path.Combine(dir, n)) && DeploymentService.IsProjectSigned(Path.Combine(dir, n)))
            .ToList();
        Check("清理后仍只有一个代理", ours.Count == 1, string.Join("、", ours));

        var restore = DeploymentService.Restore(game, removeLogs: false);
        Check("恢复成功", restore.Ok, restore.Message);

        // No proxy of ours may survive. The INI is a different matter: if a deploy displaced a
        // pre-existing file, restore correctly brings that file back, so its presence is expected
        // and not a leftover. Check the INI's content rather than its existence.
        var leftoverProxies = ModSource.ProxyCandidates
            .Where(n => File.Exists(Path.Combine(dir, n)))
            .ToList();
        Check("恢复后无代理残留", leftoverProxies.Count == 0, "残留：" + string.Join("、", leftoverProxies));

        var iniPath = Path.Combine(dir, ModSource.IniName);
        var iniIsOurs = File.Exists(iniPath) && ModSource.ReadVersion(iniPath) is not null;
        Check("恢复后 INI 不是本项目生成的那份", !iniIsOurs, "仍是本项目的配置");

        foreach (var line in restore.Lines) Console.WriteLine("        · " + line);
    }

    /// <summary>
    /// A proxy the user imported keeps its own file name, which is not in any published list. Its
    /// whole lifecycle — deploy, status, entry switch, restore — runs on the deployment record's
    /// hashes alone; before the record was scanned, restore silently left such a file in place.
    /// </summary>
    private static void TestCustomNamedProxy(string modRoot, string work)
    {
        Section("自定义命名代理（导入 DLL 全周期）");
        if (SkipWithoutModFiles("自定义命名代理")) return;

        var customName = "testfg_community.dll";
        var importedPath = Path.Combine(modRoot, ModSource.AltDirName, customName);

        try
        {
            var dir = MakeGameDir(work, "GameCustom");

            // A community build without this project's signature: only a recorded hash can vouch for it.
            var fakeDll = Path.Combine(work, "downloaded", customName);
            Directory.CreateDirectory(Path.GetDirectoryName(fakeDll)!);
            File.WriteAllBytes(fakeDll, RandomNumberGenerator.GetBytes(3072));
            Check("导入文件不带项目签名", !DeploymentService.IsProjectSigned(fakeDll));

            var import = ModSource.ImportProxy(modRoot, fakeDll);
            Check("导入自定义命名 DLL", import.Ok, import.Message);

            // Built after the import: a ModSource snapshots the folder at construction.
            var source = new ModSource(modRoot);
            Check("导入后出现在可用入口", source.AvailableProxies.Contains(customName, StringComparer.OrdinalIgnoreCase));

            var game = new GameEntry
            {
                Name = "GameCustom",
                RenderDir = dir,
                PreferredProxy = customName,
                Profile = new GameProfile(),
            };

            var deploy = DeploymentService.Deploy(game, source);
            Check("按自定义入口部署成功", deploy.Ok, deploy.Message);
            var proxyPath = Path.Combine(dir, customName);
            Check("自定义入口已写入游戏目录", File.Exists(proxyPath));
            Check("记录含该文件的哈希",
                game.Deployment?.Files.Any(f => string.Equals(f.FileName, customName, StringComparison.OrdinalIgnoreCase)) == true);

            DeploymentService.Check(game);
            Check("状态为已部署", game.Status == GameStatus.Deployed, game.StatusText + " / " + game.StatusDetail);

            // Restore must remove the custom entry too — this was the gap.
            var restore = DeploymentService.Restore(game, removeLogs: false);
            Check("恢复成功", restore.Ok, restore.Message);
            Check("自定义入口被删除", !File.Exists(proxyPath));
            Check("INI 被删除", !File.Exists(Path.Combine(dir, ModSource.IniName)));
            DeploymentService.Check(game);
            Check("恢复后状态为未部署", game.Status == GameStatus.NotDeployed, game.StatusText + " / " + game.StatusDetail);

            // Switching entry names must displace the custom one instead of running two proxies.
            var game2 = new GameEntry
            {
                Name = "GameCustom",
                RenderDir = dir,
                PreferredProxy = customName,
                Profile = new GameProfile(),
            };
            var deploy2 = DeploymentService.Deploy(game2, source);
            Check("二次部署成功", deploy2.Ok, deploy2.Message);

            game2.PreferredProxy = "version.dll";
            var switched = DeploymentService.Deploy(game2, source);
            Check("换名部署成功", switched.Ok, switched.Message);
            Check("旧自定义入口被清理", !File.Exists(proxyPath));
            Check("新入口已写入", File.Exists(Path.Combine(dir, "version.dll")));

            DeploymentService.Restore(game2, removeLogs: false);
            Check("收尾恢复干净", !File.Exists(Path.Combine(dir, "version.dll")));
        }
        finally
        {
            // The import lands in the real mod folder; leave it out of the repository.
            try { if (File.Exists(importedPath)) File.Delete(importedPath); }
            catch { /* best effort */ }
        }
    }

    private static void TestAdopt(string modRoot, string work)
    {
        Section("接管手工安装");
        if (SkipWithoutModFiles("接管手工安装")) return;

        var dir = MakeGameDir(work, "GameManual");
        var source = new ModSource(modRoot);

        // Simulate a user who copied the files in by hand.
        File.Copy(Path.Combine(modRoot, "version.dll"), Path.Combine(dir, "version.dll"));
        File.WriteAllText(Path.Combine(dir, ModSource.IniName),
            File.ReadAllText(Path.Combine(modRoot, ModSource.IniName), Encoding.UTF8));

        var game = new GameEntry { Name = "GameManual", RenderDir = dir };

        DeploymentService.Check(game);
        Check("未接管时报告未部署并提供接管提示",
            game.Status == GameStatus.NotDeployed && game.StatusDetail.Contains("接管"), game.StatusDetail);

        var adopt = DeploymentService.Adopt(game);
        Check("接管成功", adopt.Ok, adopt.Message);
        // The adopted release is read from the game folder's INI, which 0.3.0 no longer marks with a
        // version banner — so "unknown" is a legitimate answer there, and the point is that adopting
        // succeeds and records something rather than throwing the version away.
        var adoptedVersion = ModSource.ReadVersion(Path.Combine(dir, ModSource.IniName));
        Check("接管后记录版本号",
            game.Deployment?.ModVersion == (adoptedVersion ?? Loc.T("ModSource.UnknownVersion")),
            $"INI 横幅 {adoptedVersion ?? "(无)"}，记录 {game.Deployment?.ModVersion}");

        DeploymentService.Check(game);
        Check("接管后状态为已部署", game.Status == GameStatus.Deployed, game.StatusText + " / " + game.StatusDetail);

        var restore = DeploymentService.Restore(game, removeLogs: false);
        Check("接管后可恢复", restore.Ok && !File.Exists(Path.Combine(dir, "version.dll")), restore.Message);
        Check("marker 与 exe 未受影响",
            File.Exists(Path.Combine(dir, "nvngx_dlssg.dll")) && File.Exists(Path.Combine(dir, "GameManual.exe")));
    }

    private static void TestDetection(string modRoot, string work)
    {
        Section("游戏探测");

        var root = Path.Combine(work, "Library");
        var renderDir = Path.Combine(root, "SomeGame", "Binaries", "Win64");
        Directory.CreateDirectory(renderDir);
        File.WriteAllBytes(Path.Combine(renderDir, "SomeGame-Win64-Shipping.exe"), RandomNumberGenerator.GetBytes(8192));
        File.WriteAllBytes(Path.Combine(renderDir, "nvngx_dlssg.dll"), RandomNumberGenerator.GetBytes(1024));

        var hit = Detection.FindRenderTarget(root);
        Check("从标记文件定位到渲染目录", hit is not null && 
            string.Equals(Path.GetFullPath(hit.RenderDir), Path.GetFullPath(renderDir), StringComparison.OrdinalIgnoreCase),
            hit?.RenderDir);
        Check("选中 Shipping 主程序", hit?.ExePath.EndsWith("SomeGame-Win64-Shipping.exe") == true, hit?.ExePath);

        var scan = Detection.ScanFolder(root);
        Check("目录扫描找到候选", scan.Count == 1, "数量: " + scan.Count);

        // A folder with no DLSS-G marker yields nothing, so unrelated folders are never listed.
        var empty = Path.Combine(work, "NoMarker");
        Directory.CreateDirectory(empty);
        File.WriteAllBytes(Path.Combine(empty, "game.exe"), RandomNumberGenerator.GetBytes(512));
        Check("无标记目录不产生候选", Detection.ScanFolder(empty).Count == 0);

        var steamLibs = Detection.SteamLibraries().ToList();
        Console.WriteLine("      检测到 Steam 库: " + (steamLibs.Count == 0 ? "(无)" : string.Join(" | ", steamLibs)));
        Check("Steam 库枚举未抛异常", true);
    }

    private static void TestModSourceLocator(string modRoot, string work)
    {
        Section("Mod 文件源定位");

        // A folder is only a source when the marker INI is present, so an empty or partial folder is
        // not mistaken for a usable one. Uses a synthetic source so this holds with or without the
        // real download.
        var synthetic = MakeSyntheticModSource(work);
        Check("完整目录被识别为文件源", ModSourceLocator.LooksLikeSource(synthetic), synthetic);
        Check("Mod 文件已获取时真实目录也被识别",
            !_hasModFiles || ModSourceLocator.LooksLikeSource(modRoot),
            modRoot);

        var empty = Path.Combine(work, "LocEmpty");
        Directory.CreateDirectory(empty);
        Check("空目录不被当作文件源", !ModSourceLocator.LooksLikeSource(empty));

        var partial = Path.Combine(work, "LocPartial");
        Directory.CreateDirectory(Path.Combine(partial, "altnative"));
        File.WriteAllBytes(Path.Combine(partial, "version.dll"), RandomNumberGenerator.GetBytes(512));
        Check("只有 DLL 没有 INI 时不算文件源", !ModSourceLocator.LooksLikeSource(partial));

        Check("不存在的路径不算文件源", !ModSourceLocator.LooksLikeSource(Path.Combine(work, "LocNope")));
        Check("空字符串不算文件源", !ModSourceLocator.LooksLikeSource(""));
        Check("null 不算文件源", !ModSourceLocator.LooksLikeSource(null));

        // A configured path that is usable must win over anything else.
        var configured = Path.Combine(work, "LocConfigured");
        Directory.CreateDirectory(configured);
        File.WriteAllText(Path.Combine(configured, ModSource.IniName), "; test\n");
        Check("已配置的可用路径优先",
            string.Equals(ModSourceLocator.FindExisting(configured), configured, StringComparison.OrdinalIgnoreCase),
            ModSourceLocator.FindExisting(configured));

        // A configured path that no longer exists falls through instead of being returned blindly.
        var gone = Path.Combine(work, "LocGone");
        Check("失效的配置路径会被跳过",
            ModSourceLocator.FindExisting(gone) != gone,
            "返回了失效路径");

        // Download target: an existing source is reused rather than creating a second copy.
        Check("下载目标复用已有文件源",
            string.Equals(ModSourceLocator.ResolveTarget(configured), configured, StringComparison.OrdinalIgnoreCase),
            ModSourceLocator.ResolveTarget(configured));

        // With nothing configured, the target must still be a real path we can create.
        var fallback = ModSourceLocator.ResolveTarget(Path.Combine(work, "LocNeverExisted"));
        Check("无可复用目录时给出可写目标", !string.IsNullOrWhiteSpace(fallback), fallback);

        // A source checkout must download into <repo>\mod, not into its bin folder — otherwise a fresh
        // clone ends up with the files somewhere the next build wipes.
        var repoRoot = ModSourceLocator.FindRepositoryRoot();
        Console.WriteLine("      识别到的仓库根: " + (repoRoot ?? "(无)"));
        Check("能识别出仓库根目录", repoRoot is not null, "未找到");

        if (repoRoot is not null)
        {
            var expected = Path.Combine(repoRoot, "mod");
            Check("全新克隆时下载目标指向仓库根的 mod",
                string.Equals(Path.GetFullPath(fallback), Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase),
                fallback);
            Check("下载目标不在 bin 目录内",
                !fallback.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase),
                fallback);
        }

        // An existing source is always preferred over deriving a fresh path. Only assertable once the
        // files have actually been fetched.
        var found = ModSourceLocator.FindExisting(null);
        Console.WriteLine("      当前解析到的文件源: " + (found ?? "(无，尚未获取)"));
        Check("Mod 文件已获取时能找到仓库的 mod 目录",
            !_hasModFiles
            || (found is not null && string.Equals(Path.GetFullPath(found), Path.GetFullPath(modRoot), StringComparison.OrdinalIgnoreCase)),
            found ?? "(无)");

        // Installed builds live under Program Files, where the app cannot write without elevation.
        // Writability therefore decides between "portable" and "per-user data" placement.
        var writable = Path.Combine(work, "WritableProbe");
        Check("可写目录判定为可写", ModSourceLocator.IsWritable(writable), writable);

        // A path that cannot exist (a file used as a parent) must report not-writable, not throw.
        var fileAsParent = Path.Combine(work, "FileNotDir");
        File.WriteAllText(fileAsParent, "x");
        Check("不可创建的路径判定为不可写",
            !ModSourceLocator.IsWritable(Path.Combine(fileAsParent, "sub")),
            fileAsParent);

        Check("非法字符路径判定为不可写",
            !ModSourceLocator.IsWritable(Path.Combine(work, "bad|name")),
            "bad|name");

        // Installed copies keep mod files beside the program, matching a hand-unzipped copy. The
        // signal that a copy was installed is Inno Setup's uninstaller sitting next to the exe.
        Console.WriteLine("      当前是否安装版: " + ModSourceLocator.IsInstalledCopy());
        Check("未安装的副本不被判定为安装版", !ModSourceLocator.IsInstalledCopy(), AppContext.BaseDirectory);

        var portable = Path.Combine(work, "PortableCopy");
        Directory.CreateDirectory(portable);
        Check("无卸载程序的目录不算安装版", !ModSourceLocator.IsInstalledCopyIn(portable), portable);

        var installed = Path.Combine(work, "InstalledCopy");
        Directory.CreateDirectory(installed);
        File.WriteAllBytes(Path.Combine(installed, "unins000.exe"), RandomNumberGenerator.GetBytes(128));
        Check("存在 unins000.exe 的目录判定为安装版", ModSourceLocator.IsInstalledCopyIn(installed), installed);

        var installedAlt = Path.Combine(work, "InstalledCopy2");
        Directory.CreateDirectory(installedAlt);
        File.WriteAllBytes(Path.Combine(installedAlt, "unins001.exe"), RandomNumberGenerator.GetBytes(128));
        Check("其他编号的卸载程序同样识别", ModSourceLocator.IsInstalledCopyIn(installedAlt), installedAlt);

        Check("不存在的目录不抛异常", !ModSourceLocator.IsInstalledCopyIn(Path.Combine(work, "NoSuchDir")));
        Check("空路径不抛异常", !ModSourceLocator.IsInstalledCopyIn(""));

        // Mod files live beside the program whenever that folder is writable, so a copy stays
        // self-contained; a read-only location (Program Files without elevation) falls back to the
        // per-user folder. This choice is the difference between "works" and "fails to download".
        var writableBeside = Path.Combine(work, "BesideProgram");
        var userArea = Path.Combine(work, "UserArea");
        Directory.CreateDirectory(userArea);
        Check("程序目录可写时选它",
            ModSourceLocator.PreferWritable(writableBeside, userArea) == writableBeside,
            ModSourceLocator.PreferWritable(writableBeside, userArea));

        var fileNotDir = Path.Combine(work, "StillAFile");
        File.WriteAllText(fileNotDir, "x");
        Check("程序目录不可写时回退到用户目录",
            ModSourceLocator.PreferWritable(Path.Combine(fileNotDir, "sub"), userArea) == userArea,
            ModSourceLocator.PreferWritable(Path.Combine(fileNotDir, "sub"), userArea));

        Check("非法字符路径触发回退",
            ModSourceLocator.PreferWritable(Path.Combine(work, "bad|name"), userArea) == userArea);
    }

    private static void TestPathGuard(string work)
    {
        Section("Shell 路径校验");

        var dir = Path.Combine(work, "GuardDir");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "thing.exe");
        File.WriteAllBytes(file, RandomNumberGenerator.GetBytes(256));
        var doc = Path.Combine(dir, "notes.txt");
        File.WriteAllText(doc, "hello");

        Check("接受存在的目录", PathGuard.IsSafe(dir, out _), dir);
        Check("接受存在的文件", PathGuard.IsSafe(file, out _), file);
        Check("可执行校验接受 .exe", PathGuard.IsSafeExecutable(file, out _), file);

        // Anything that is not a real, absolute path must be refused before reaching the shell.
        Check("拒绝 null", !PathGuard.IsSafe(null, out _));
        Check("拒绝空字符串", !PathGuard.IsSafe("", out _));
        Check("拒绝纯空白", !PathGuard.IsSafe("   ", out _));
        Check("拒绝不存在的路径", !PathGuard.IsSafe(Path.Combine(dir, "nope"), out _));
        Check("拒绝相对路径", !PathGuard.IsSafe(@"some\relative\path", out _));
        Check("拒绝 UNC 之外的无根路径", !PathGuard.IsSafe("thing.exe", out _));

        // Quoting and control characters are the shape a tampered path takes.
        Check("拒绝含引号的路径", !PathGuard.IsSafe($"\"{file}\"", out var qr), qr);
        Check("拒绝内嵌引号", !PathGuard.IsSafe(file.Replace("thing", "th\"ing"), out var qr2), qr2);
        Check("拒绝含换行的路径", !PathGuard.IsSafe(file + "\n", out var nr), nr);
        Check("拒绝含制表符的路径", !PathGuard.IsSafe(dir + "\t", out var tr), tr);
        Check("拒绝含空字符的路径", !PathGuard.IsSafe(file + "\0", out var zr), zr);

        // Surrounding whitespace signals a quoting mistake upstream.
        Check("拒绝首部空白", !PathGuard.IsSafe(" " + file, out var lr), lr);
        Check("拒绝尾部空白", !PathGuard.IsSafe(file + " ", out var rr), rr);

        // Shell metacharacters are only a problem if something re-parses the string. The ones Windows
        // allows in file names must still work, since the shell call takes the path as one value.
        var tricky = Path.Combine(dir, "a&b^c!d(1).exe");
        File.WriteAllBytes(tricky, RandomNumberGenerator.GetBytes(64));
        Check("允许文件名含 shell 元字符", PathGuard.IsSafe(tricky, out var kr), kr);

        var percent = Path.Combine(dir, "100%.txt");
        File.WriteAllText(percent, "x");
        Check("允许文件名含百分号", PathGuard.IsSafe(percent, out var pr), pr);

        var spaced = Path.Combine(dir, "my game.exe");
        File.WriteAllBytes(spaced, RandomNumberGenerator.GetBytes(64));
        Check("允许文件名含空格", PathGuard.IsSafe(spaced, out var sr), sr);

        // Executable check is stricter than the general one.
        Check("可执行校验拒绝非 exe", !PathGuard.IsSafeExecutable(doc, out var er), er);
        Check("可执行校验拒绝目录", !PathGuard.IsSafeExecutable(dir, out var dr), dr);
        Check("可执行校验拒绝不存在的 exe", !PathGuard.IsSafeExecutable(Path.Combine(dir, "no.exe"), out _));

        // Opening a missing target must fail cleanly rather than reaching the shell.
        Check("打开不存在的目录返回 false", !Shell.OpenFolder(Path.Combine(work, "GuardNope")));
        Check("打开不存在的文件返回 false", !Shell.OpenDocument(Path.Combine(work, "GuardNope.txt")));
        Check("启动不存在的程序返回 false", !Shell.LaunchExecutable(Path.Combine(dir, "no.exe")));
        Check("启动非 exe 返回 false", !Shell.LaunchExecutable(doc));
        Check("启动含引号路径返回 false", !Shell.LaunchExecutable($"\"{file}\""));
    }

    private static void TestAntiCheat(string modRoot, string work)
    {
        Section("反作弊检测与隔离清理");

        // Uses a synthetic source: this section verifies the deployment gate, not the mod payload, and
        // a synthetic source keeps the gate tests honest when the real files are absent.
        var source = new ModSource(MakeSyntheticModSource(work));
        Check("合成文件源有效（保证后续用例真实生效）", source.IsValid, source.ValidationMessage);

        // --- a clean game must stay deployable ---
        var clean = MakeGameDir(work, "AcClean");
        var cleanReport = AntiCheat.Scan(clean);
        Check("干净目录判定为无反作弊", !cleanReport.IsProtected, cleanReport.Summary);

        var cleanGame = new GameEntry { Name = "AcClean", RenderDir = clean, PreferredProxy = DeploymentService.AutoProxy };
        var cleanDeploy = DeploymentService.Deploy(cleanGame, source);
        Check("干净游戏可正常部署", cleanDeploy.Ok, cleanDeploy.Message);

        // --- HoYoKProtect reproduces the ZZZ situation ---
        var hoyo = MakeGameDir(work, "AcHoyo");
        File.WriteAllBytes(Path.Combine(hoyo, "HoYoKProtect.sys"), RandomNumberGenerator.GetBytes(4096));
        File.WriteAllBytes(Path.Combine(hoyo, "mhypbase.dll"), RandomNumberGenerator.GetBytes(2048));

        var report = AntiCheat.Scan(hoyo);
        Check("识别出米哈游内核反作弊", report.HasKernelAntiCheat, report.Summary);
        Check("报告产品名", report.Products.Contains("HoYoKProtect"), report.Products);
        Check("报告证据文件名", report.Evidence.Contains("HoYoKProtect.sys"), report.Evidence);

        var hoyoGame = new GameEntry { Name = "AcHoyo", RenderDir = hoyo, PreferredProxy = DeploymentService.AutoProxy };
        var blocked = DeploymentService.Deploy(hoyoGame, source);
        Check("默认拒绝部署到内核反作弊游戏", !blocked.Ok, "居然部署成功了");
        Check("拒绝理由说明风险", blocked.Message.Contains("反作弊") && blocked.Message.Contains("账号"), blocked.Message);
        Check("拒绝后未写入任何文件",
            !File.Exists(Path.Combine(hoyo, "version.dll")) && !File.Exists(Path.Combine(hoyo, ModSource.IniName)));

        // Explicit override still works, for a user who insists.
        var overridden = DeploymentService.Deploy(hoyoGame, source, allowProtected: true);
        Check("显式放行后可部署", overridden.Ok, overridden.Message);
        Check("放行后文件已写入", File.Exists(Path.Combine(hoyo, "version.dll")));

        // --- simulate the anti-cheat quarantining the proxy by renaming it ---
        var proxyPath = Path.Combine(hoyo, "version.dll");
        var recordedHash = hoyoGame.Deployment!.ProxySha256;
        var renamed = Path.Combine(hoyo, "version.dll.3787982156");
        File.Move(proxyPath, renamed);

        var copies = AntiCheat.FindQuarantinedCopies(hoyo, "version.dll", recordedHash);
        Check("识别出被隔离的副本", copies.Count == 1, "数量: " + copies.Count);
        Check("副本路径正确", copies.FirstOrDefault()?.EndsWith("version.dll.3787982156") == true);

        DeploymentService.Check(hoyoGame);
        Check("状态识别为被隔离而非普通缺失",
            hoyoGame.Status == GameStatus.Missing && hoyoGame.StatusDetail.Contains("隔离"),
            hoyoGame.StatusText + " / " + hoyoGame.StatusDetail);

        var restore = DeploymentService.Restore(hoyoGame, removeLogs: false);
        Check("恢复成功", restore.Ok, restore.Message);
        Check("被隔离副本已清理", !File.Exists(renamed));
        Check("INI 已清理", !File.Exists(Path.Combine(hoyo, ModSource.IniName)));
        Check("反作弊文件未被误删",
            File.Exists(Path.Combine(hoyo, "HoYoKProtect.sys")) && File.Exists(Path.Combine(hoyo, "mhypbase.dll")));

        // --- another tool's similarly named backup must survive ---
        var foreign = Path.Combine(clean, "version.dll.mybackup");
        File.WriteAllBytes(foreign, RandomNumberGenerator.GetBytes(1024));
        var foreignHash = Sha(foreign);
        var foreignCopies = AntiCheat.FindQuarantinedCopies(clean, "version.dll", null);
        Check("非本项目的同名备份不被识别", foreignCopies.Count == 0, "误报: " + string.Join(",", foreignCopies));
        Check("非本项目备份未被删除", File.Exists(foreign) && Sha(foreign) == foreignHash);

        // --- one more vendor, to prove the table is not HoYo-specific ---
        var eac = MakeGameDir(work, "AcEac");
        File.WriteAllBytes(Path.Combine(eac, "EasyAntiCheat_EOS.sys"), RandomNumberGenerator.GetBytes(2048));
        var eacReport = AntiCheat.Scan(eac);
        Check("识别出 Easy Anti-Cheat", eacReport.HasKernelAntiCheat && eacReport.Products.Contains("Easy"),
            eacReport.Summary);

        // --- regression: Tencent ACE ships its payload in a plain folder, not as loose files ---
        var ace = MakeGameDir(work, "AcAceDir");
        var aceDir = Path.Combine(ace, "AntiCheatExpert");
        Directory.CreateDirectory(aceDir);
        File.WriteAllBytes(Path.Combine(aceDir, "ACE-BASE.sys"), RandomNumberGenerator.GetBytes(2048));
        File.WriteAllBytes(Path.Combine(aceDir, "ACE-Service64.exe"), RandomNumberGenerator.GetBytes(1024));
        var aceReport = AntiCheat.Scan(ace);
        Check("识别出子目录形式的腾讯 ACE", aceReport.HasKernelAntiCheat, aceReport.Summary);
        Check("ACE 报告目录名作为证据", aceReport.Evidence.Contains("AntiCheatExpert"), aceReport.Evidence);

        var aceGame = new GameEntry { Name = "AcAceDir", RenderDir = ace, PreferredProxy = DeploymentService.AutoProxy };
        Check("目录形式的 ACE 也拦截部署", !DeploymentService.Deploy(aceGame, source).Ok);

        // --- regression: NetEase NEAC, which the first release missed ---
        var neac = MakeGameDir(work, "AcNeac");
        File.WriteAllBytes(Path.Combine(neac, "NeacSafe64.sys"), RandomNumberGenerator.GetBytes(2048));
        File.WriteAllBytes(Path.Combine(neac, "NeacInterface.dll"), RandomNumberGenerator.GetBytes(1024));
        File.WriteAllBytes(Path.Combine(neac, "NeacLoader.exe"), RandomNumberGenerator.GetBytes(512));
        var neacReport = AntiCheat.Scan(neac);
        Check("识别出网易 NEAC", neacReport.HasKernelAntiCheat && neacReport.Products.Contains("NEAC"),
            neacReport.Summary);

        var neacGame = new GameEntry { Name = "AcNeac", RenderDir = neac, PreferredProxy = DeploymentService.AutoProxy };
        Check("NEAC 拦截部署", !DeploymentService.Deploy(neacGame, source).Ok);

        // --- an unknown vendor's kernel driver is still caught by the generic rule ---
        var unknown = MakeGameDir(work, "AcUnknownDriver");
        File.WriteAllBytes(Path.Combine(unknown, "SomeGuard64.sys"), RandomNumberGenerator.GetBytes(4096));
        var unknownReport = AntiCheat.Scan(unknown);
        Check("未知厂商的内核驱动也被识别", unknownReport.HasKernelAntiCheat, unknownReport.Summary);
        Check("未知驱动标注为未识别厂商", unknownReport.Products.Contains("未识别"), unknownReport.Products);

        var unknownGame = new GameEntry { Name = "AcUnknownDriver", RenderDir = unknown, PreferredProxy = DeploymentService.AutoProxy };
        Check("未知内核驱动也拦截部署", !DeploymentService.Deploy(unknownGame, source).Ok);

        // --- Windows' own files must never be reported as anti-cheat drivers ---
        var rootProbe = Path.Combine(work, "AcRootProbe");
        Directory.CreateDirectory(rootProbe);
        foreach (var sys in new[] { "pagefile.sys", "swapfile.sys", "hiberfil.sys" })
            File.WriteAllBytes(Path.Combine(rootProbe, sys), RandomNumberGenerator.GetBytes(512));

        var rootReport = AntiCheat.Scan(rootProbe);
        Check("不会把 pagefile.sys 等系统文件当作驱动",
            !rootReport.Findings.Any(f => f.Evidence.Contains("pagefile", StringComparison.OrdinalIgnoreCase) ||
                                          f.Evidence.Contains("swapfile", StringComparison.OrdinalIgnoreCase) ||
                                          f.Evidence.Contains("hiberfil", StringComparison.OrdinalIgnoreCase)),
            string.Join(",", rootReport.Findings.Select(f => f.Evidence)));

        // A real unknown driver alongside them is still caught.
        File.WriteAllBytes(Path.Combine(rootProbe, "MysteryGuard.sys"), RandomNumberGenerator.GetBytes(1024));
        var mixedReport = AntiCheat.Scan(rootProbe);
        Check("同目录下的真实驱动仍被识别",
            mixedReport.HasKernelAntiCheat && mixedReport.Evidence.Contains("MysteryGuard"),
            mixedReport.Summary);

        // --- the prompt shown when a protected game is added ---
        var notice = AntiCheat.BuildUnsupportedNotice("终末地", aceReport);
        Check("提示包含游戏名", notice.Contains("终末地"), notice);
        Check("提示说明反作弊产品", notice.Contains("腾讯 ACE"), notice);
        Check("提示列出证据", notice.Contains("AntiCheatExpert"), notice);
        Check("提示说明自带入口会被隔离", notice.Contains("隔离"), notice);
        Check("提示警示账号风险", notice.Contains("账号"), notice);
        Check("提示引导用游戏自带功能", notice.Contains("nvngx_dlssg.dll"), notice);
        Check("提示说明部署要逐个确认", notice.Contains("确认"), notice);

        var unnamed = AntiCheat.BuildUnsupportedNotice("", neacReport);
        Check("游戏名为空时提示仍完整", unnamed.Contains("该游戏") && unnamed.Contains("NEAC"), unnamed);

        // --- pointing at a game ROOT must still find anti-cheat living in a sub-folder ---
        // Mirrors Overwatch: root\ has no exe, root\_retail_\ has the exe and the anti-cheat driver,
        // root\_retail_\sl\ has the DLSS-G marker. Missing this would wrongly report "protected=false".
        var owRoot = Path.Combine(work, "AcOverwatchStyle");
        var ret = Path.Combine(owRoot, "_retail_");
        var sl = Path.Combine(ret, "sl");
        Directory.CreateDirectory(sl);
        File.WriteAllBytes(Path.Combine(ret, "Overwatch.exe"), RandomNumberGenerator.GetBytes(4096));
        File.WriteAllBytes(Path.Combine(ret, "NeacSafe64.sys"), RandomNumberGenerator.GetBytes(2048));
        File.WriteAllBytes(Path.Combine(sl, "nvngx_dlssg.dll"), RandomNumberGenerator.GetBytes(1024));

        var (resolved, resolvedExe) = Detection.ResolveRenderDir(owRoot);
        Check("指向游戏根目录时解析到渲染目录",
            string.Equals(Path.GetFullPath(resolved), Path.GetFullPath(ret), StringComparison.OrdinalIgnoreCase),
            resolved);
        Check("解析同时找到主程序", resolvedExe?.EndsWith("Overwatch.exe") == true, resolvedExe);

        // The whole point of resolving first: scanning the raw root misses the driver entirely.
        Check("直接扫根目录会漏掉反作弊（说明必须先解析）", !AntiCheat.Scan(owRoot).HasKernelAntiCheat);
        Check("解析后再扫能发现反作弊", AntiCheat.Scan(resolved).HasKernelAntiCheat);

        // Pointing straight at the render directory must not be "resolved" anywhere else.
        var (direct, _) = Detection.ResolveRenderDir(ret);
        Check("直接指向渲染目录时保持不变",
            string.Equals(Path.GetFullPath(direct), Path.GetFullPath(ret), StringComparison.OrdinalIgnoreCase),
            direct);

        // A folder that is already the render directory keeps its own name for the game title.
        Check("游戏名取自渲染目录而非通用父目录",
            Detection.FriendlyName(ret).Equals("AcOverwatchStyle", StringComparison.OrdinalIgnoreCase),
            Detection.FriendlyName(ret));

        // --- a genuine Steam game folder ships ordinary DLLs and must NOT be flagged ---
        var normal = MakeGameDir(work, "AcNormalGame");
        foreach (var dll in new[] { "UnityPlayer.dll", "GameAssembly.dll", "nvngx_dlssg.dll", "sl.interposer.dll", "amd_ags_x64.dll" })
            File.WriteAllBytes(Path.Combine(normal, dll), RandomNumberGenerator.GetBytes(1024));
        var normalReport = AntiCheat.Scan(normal);
        Check("普通游戏目录不误报", !normalReport.IsProtected, normalReport.Summary);

        var normalGame = new GameEntry { Name = "AcNormalGame", RenderDir = normal, PreferredProxy = DeploymentService.AutoProxy };
        Check("普通游戏仍可部署", DeploymentService.Deploy(normalGame, source).Ok);

        // --- anti-cheat in a parent folder still counts ---
        var nested = Path.Combine(work, "AcNested", "Binaries", "Win64");
        Directory.CreateDirectory(nested);
        File.WriteAllBytes(Path.Combine(nested, "game-Win64-Shipping.exe"), RandomNumberGenerator.GetBytes(2048));
        File.WriteAllBytes(Path.Combine(work, "AcNested", "ACE-BASE.sys"), RandomNumberGenerator.GetBytes(2048));
        var nestedReport = AntiCheat.Scan(nested);
        Check("上级目录的反作弊也能发现", nestedReport.HasKernelAntiCheat, nestedReport.Summary);

        // --- a game with no anti-cheat reports cleanly ---
        var plain = Path.Combine(work, "AcPlain");
        Directory.CreateDirectory(plain);
        Check("空目录不误报", !AntiCheat.Scan(plain).IsProtected);
        Check("不存在的目录标记为扫描失败", AntiCheat.Scan(Path.Combine(work, "nope")).ScanFailed);
    }

    private static void TestPersistence(string work)
    {
        Section("库文件持久化");

        var file = Path.Combine(work, "library_roundtrip.json");
        var data = new AppData();

        var game = new GameEntry
        {
            Name = "持久化测试",
            RenderDir = @"C:\fake\path",
            ExePath = @"C:\fake\path\game.exe",
            PreferredProxy = "winmm.dll",
            Notes = "Steam",
            Profile = new GameProfile { Router = "SM75", KernelImage = "Auto", HardwareBilinear = true, MaxGeneratedFrames = 2, LogLevel = 3 },
        };

        // The UI adds straight to the persisted collection; that collection must be what gets written.
        data.Games.Add(game);
        LibraryStore.Save(data, file);

        Check("库文件已写出", File.Exists(file));

        var reloaded = LibraryStore.Load(file);
        Check("游戏数量往返一致", reloaded.Games.Count == 1, "实际: " + reloaded.Games.Count);

        var r = reloaded.Games.FirstOrDefault();
        Check("名称往返一致", r?.Name == "持久化测试", r?.Name);
        Check("渲染目录往返一致", r?.RenderDir == @"C:\fake\path", r?.RenderDir);
        Check("代理入口往返一致", r?.PreferredProxy == "winmm.dll", r?.PreferredProxy);
        Check("路由往返一致", r?.Profile.Router == "SM75", r?.Profile.Router);
        Check("内核镜像往返一致", r?.Profile.KernelImage == "Auto", r?.Profile.KernelImage);
        Check("近似采样往返一致", r?.Profile.HardwareBilinear == true);
        Check("倍率上限往返一致", r?.Profile.MaxGeneratedFrames == 2);
        Check("日志级别往返一致", r?.Profile.LogLevel == 3);

        // A deployment record must survive a restart, otherwise restore loses its proof of ownership.
        r!.Deployment = new DeploymentInfo
        {
            ProxyName = "version.dll",
            ModVersion = "0.2.3",
            DeployedAt = "2026-09-10 15:00:00",
            ProxySha256 = "ABCDEF",
            IniSha256 = "123456",
            Backups = new List<BackupItem> { new() { FileName = "winmm.dll", StoredPath = @"C:\b\winmm.dll", Sha256 = "AA", Size = 42 } },
        };
        LibraryStore.Save(reloaded, file);

        var again = LibraryStore.Load(file);
        var g2 = again.Games.FirstOrDefault();
        Check("部署记录往返一致", g2?.Deployment?.ProxyName == "version.dll", g2?.Deployment?.ProxyName);
        Check("备份条目往返一致", g2?.Deployment?.Backups.Count == 1, "数量: " + g2?.Deployment?.Backups.Count);
        Check("备份哈希往返一致", g2?.Deployment?.Backups[0].Sha256 == "AA");

        // Corrupt input must degrade to an empty library rather than throw on startup.
        File.WriteAllText(Path.Combine(work, "broken.json"), "{ this is not json");
        var fallback = LibraryStore.Load(Path.Combine(work, "broken.json"));
        Check("损坏的库文件回退为空库", fallback.Games.Count == 0);

        // Out-of-range values from a hand-edited file get clamped on load.
        File.WriteAllText(Path.Combine(work, "outofrange.json"),
            "{\"Games\":[{\"Name\":\"x\",\"Profile\":{\"MaxGeneratedFrames\":99,\"LogLevel\":-3,\"Router\":\"bogus\",\"KernelImage\":\"weird\"}}]}");
        var clamped = LibraryStore.Load(Path.Combine(work, "outofrange.json"));
        var cg = clamped.Games.FirstOrDefault();
        Check("越界倍率被夹取", cg?.Profile.MaxGeneratedFrames == 5, "实际: " + cg?.Profile.MaxGeneratedFrames);
        Check("越界日志级别被夹取", cg?.Profile.LogLevel == 0, "实际: " + cg?.Profile.LogLevel);
        Check("非法路由被归一", cg?.Profile.Router == "SM86", cg?.Profile.Router);
        Check("非法内核镜像被归一", cg?.Profile.KernelImage == "PTX", cg?.Profile.KernelImage);
    }

    /// <summary>
    /// The download-source picker depends on these contracts: stable ids (never localised), a note for
    /// every source, and a distinction between official endpoints and mirrors.
    /// </summary>
    /// <summary>
    /// The two theme dictionaries must define exactly the same keys. A key present in only one theme
    /// throws at switch time — the failure appears when the user clicks the toggle, not at build time,
    /// so it has to be caught here.
    /// </summary>
    private static void TestThemes()
    {
        Section("界面主题");

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var darkPath = Path.Combine(repoRoot, "src", "DLSSGManager", "Themes", "Dark.xaml");
        var lightPath = Path.Combine(repoRoot, "src", "DLSSGManager", "Themes", "Light.xaml");

        if (!File.Exists(darkPath) || !File.Exists(lightPath))
        {
            Check("主题文件存在", false, $"缺少 {(File.Exists(darkPath) ? lightPath : darkPath)}");
            return;
        }

        var dark = ThemeKeysIn(darkPath);
        var light = ThemeKeysIn(lightPath);

        Console.WriteLine($"      深色主题键: {dark.Count}   浅色主题键: {light.Count}");

        var onlyDark = dark.Except(light).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var onlyLight = light.Except(dark).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Check("两个主题的键完全一致",
            onlyDark.Count == 0 && onlyLight.Count == 0,
            (onlyDark.Count > 0 ? "仅深色: " + string.Join(",", onlyDark) : "") +
            (onlyLight.Count > 0 ? " 仅浅色: " + string.Join(",", onlyLight) : ""));

        // Every key named in ThemeKeys must actually exist in both dictionaries, or code that resolves
        // a key would get nothing back and silently render transparent.
        var declared = new HashSet<string>(ThemeKeys.All, StringComparer.Ordinal);
        var missingInDark = declared.Except(dark).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var missingInLight = declared.Except(light).OrderBy(k => k, StringComparer.Ordinal).ToList();

        Check("ThemeKeys 声明的键都在深色主题里", missingInDark.Count == 0, string.Join(",", missingInDark));
        Check("ThemeKeys 声明的键都在浅色主题里", missingInLight.Count == 0, string.Join(",", missingInLight));

        // The themes must differ overall, but individual keys may legitimately share a value: white
        // text on a blue button is correct in both, and the transparency-neutral entries are the same.
        // So the check is on the proportion of differing keys, not on every key.
        var darkColors = ThemeColorValues(darkPath);
        var lightColors = ThemeColorValues(lightPath);
        var shared = darkColors.Count(kv => lightColors.TryGetValue(kv.Key, out var c) && c == kv.Value);
        var total = Math.Min(darkColors.Count, lightColors.Count);

        Console.WriteLine($"      色值相同的键: {shared}/{total}"
                          + (shared > 0 ? "（" + string.Join(",", darkColors.Where(kv => lightColors.TryGetValue(kv.Key, out var c) && c == kv.Value).Select(kv => kv.Key)) + "）" : ""));

        Check("两个主题整体上有区别",
            total > 0 && shared < total / 2,
            $"{shared}/{total} 个键的色值相同");

        // Sanity: each theme should be broadly consistent with its name rather than inverted by mistake.
        Check("深色主题的窗口底色偏暗",
            IsDark(darkColors.GetValueOrDefault(ThemeKeys.WindowBackground, "")),
            darkColors.GetValueOrDefault(ThemeKeys.WindowBackground, "(无)"));
        Check("浅色主题的窗口底色偏亮",
            !IsDark(lightColors.GetValueOrDefault(ThemeKeys.WindowBackground, "")),
            lightColors.GetValueOrDefault(ThemeKeys.WindowBackground, "(无)"));
        Check("深色主题的正文色偏亮",
            !IsDark(darkColors.GetValueOrDefault(ThemeKeys.TextPrimary, "")),
            darkColors.GetValueOrDefault(ThemeKeys.TextPrimary, "(无)"));
        Check("浅色主题的正文色偏暗",
            IsDark(lightColors.GetValueOrDefault(ThemeKeys.TextPrimary, "")),
            lightColors.GetValueOrDefault(ThemeKeys.TextPrimary, "(无)"));

        // Contrast is what actually matters for readability, and re-picking colours for a light theme
        // is exactly when it gets overlooked. Pairs are (foreground, background, minimum ratio).
        var pairs = new (string Fg, string Bg, double Min, string What)[]
        {
            (ThemeKeys.TextPrimary, ThemeKeys.WindowBackground, 7.0, "正文/窗口"),
            (ThemeKeys.TextPrimary, ThemeKeys.CardBackground, 7.0, "正文/卡片"),
            (ThemeKeys.TextMuted, ThemeKeys.WindowBackground, 4.5, "次要文字/窗口"),
            (ThemeKeys.TextSection, ThemeKeys.PanelBackground, 4.5, "节标题/面板"),
            (ThemeKeys.TextOnLog, ThemeKeys.LogBackground, 7.0, "日志文字/日志底"),
            (ThemeKeys.WarnBannerTitle, ThemeKeys.WarnBannerBackground, 4.5, "警告标题/警告底"),
            (ThemeKeys.WarnBannerBody, ThemeKeys.WarnBannerBackground, 4.5, "警告正文/警告底"),
            (ThemeKeys.BadgeOkText, ThemeKeys.BadgeOkBackground, 4.5, "徽章文字/徽章底"),
            (ThemeKeys.BadgeWarnText, ThemeKeys.BadgeWarnBackground, 4.5, "警告徽章文字/徽章底"),
            (ThemeKeys.OnPrimaryText, ThemeKeys.PrimaryBackground, 4.5, "主按钮文字/主按钮底"),
            (ThemeKeys.DangerText, ThemeKeys.DangerBackground, 4.5, "危险按钮文字/按钮底"),
        };

        foreach (var theme in new[] { "Dark", "Light" })
        {
            var colors = theme == "Dark" ? darkColors : lightColors;
            var failures = new List<string>();

            foreach (var (fg, bg, min, what) in pairs)
            {
                var f = colors.GetValueOrDefault(fg);
                var b = colors.GetValueOrDefault(bg);
                if (f is null || b is null) continue;

                var ratio = ContrastRatio(f, b);
                if (ratio < min) failures.Add($"{what} {ratio:F1}<{min}");
            }

            Check($"{theme} 主题的文字对比度达标",
                failures.Count == 0,
                failures.Count > 0 ? string.Join("；", failures) : "");
        }

        // Storage values round-trip through the parser.
        Check("深色主题存储值", ThemeKeys.StorageValue(AppTheme.Dark) == "dark", ThemeKeys.StorageValue(AppTheme.Dark));
        Check("浅色主题存储值", ThemeKeys.StorageValue(AppTheme.Light) == "light", ThemeKeys.StorageValue(AppTheme.Light));
        Check("存储值解析回枚举", ThemeKeys.Parse("light") == AppTheme.Light);
        Check("未知存储值回落深色", ThemeKeys.Parse("nonsense") == AppTheme.Dark);
        Check("空值回落深色", ThemeKeys.Parse(null) == AppTheme.Dark);

        // Every window must take its colours from the theme. A hard-coded colour is fixed at creation
        // and ignores a theme switch, which is how the source picker dialog ended up staying dark —
        // and a new window added later would fail the same way without this check.
        var xamlDir = Path.Combine(repoRoot, "src", "DLSSGManager");
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(xamlDir, "*.xaml", SearchOption.AllDirectories))
        {
            // The theme dictionaries are where colours are supposed to be defined.
            if (file.Contains($"{Path.DirectorySeparatorChar}Themes{Path.DirectorySeparatorChar}")) continue;

            var text = File.ReadAllText(file);

            // Strip comments so a colour mentioned in a comment is not counted.
            text = Regex.Replace(text, @"<!--.*?-->", "", RegexOptions.Singleline);

            foreach (Match m in Regex.Matches(text, @"(?:Value|Background|Foreground|BorderBrush|Fill|Color)=""(#[0-9A-Fa-f]{6,8})"""))
                offenders.Add($"{Path.GetFileName(file)}:{m.Groups[1].Value}");
        }

        Check("界面文件中没有硬编码颜色",
            offenders.Count == 0,
            offenders.Count > 0 ? string.Join("、", offenders.Take(6)) : "");

        // Same for colours assigned in code, which also bypass the theme.
        var codeOffenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(xamlDir, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            // Palette and ThemeKeys legitimately name keys rather than colours.
            if (name is "Palette.cs" or "ThemeKeys.cs" or "Theme.cs") continue;

            var text = Regex.Replace(File.ReadAllText(file), @"//.*$", "", RegexOptions.Multiline);
            foreach (Match m in Regex.Matches(text, @"Color\.FromRgb\(|""#[0-9A-Fa-f]{6}"""))
                codeOffenders.Add($"{name}:{m.Value}");
        }

        Check("代码中未直接写入颜色",
            codeOffenders.Count == 0,
            codeOffenders.Count > 0 ? string.Join("、", codeOffenders.Take(6)) : "");
    }

    /// <summary>Reads the x:Key names a theme dictionary defines.</summary>
    private static HashSet<string> ThemeKeysIn(string path) =>
        Regex.Matches(File.ReadAllText(path), @"x:Key=""([A-Za-z0-9_]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>Maps each key in a theme dictionary to its colour value.</summary>
    private static Dictionary<string, string> ThemeColorValues(string path) =>
        Regex.Matches(File.ReadAllText(path), @"x:Key=""([A-Za-z0-9_]+)""\s+Color=""(#[0-9A-Fa-f]{6,8})""")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value, StringComparer.Ordinal);

    /// <summary>Rough luminance test, enough to tell a dark background from a light one.</summary>
    private static bool IsDark(string hex)
    {
        if (string.IsNullOrWhiteSpace(hex) || hex.Length < 7) return false;

        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var g = Convert.ToInt32(hex.Substring(3, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);

        // Rec. 601 luma.
        return (0.299 * r + 0.587 * g + 0.114 * b) < 128;
    }

    /// <summary>
    /// WCAG contrast ratio between two colours, from 1:1 to 21:1.
    ///
    /// Uses the WCAG relative-luminance formula, which linearises each channel. A plain luma average
    /// understates the contrast of saturated colours and would pass combinations that are hard to read.
    /// </summary>
    private static double ContrastRatio(string foreground, string background)
    {
        static double Luminance(string hex)
        {
            var r = Convert.ToInt32(hex.Substring(1, 2), 16) / 255.0;
            var g = Convert.ToInt32(hex.Substring(3, 2), 16) / 255.0;
            var b = Convert.ToInt32(hex.Substring(5, 2), 16) / 255.0;

            static double Channel(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);

            return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
        }

        var lf = Luminance(foreground);
        var lb = Luminance(background);
        return (Math.Max(lf, lb) + 0.05) / (Math.Min(lf, lb) + 0.05);
    }

    private static void TestSourceSelection()
    {
        Section("下载源选择");

        var sources = ModFetcher.AvailableSources;
        Check("至少有两个可选源", sources.Count >= 2, sources.Count.ToString());
        Console.WriteLine("      可选源: " + string.Join(", ", sources.Select(s => $"[{s.Id}] {s.Name}")));

        // Ids are the contract between the picker and the fetcher: they must be stable and
        // language-independent, because a translated id would break selection after a language switch.
        Check("源 ID 唯一", sources.Select(s => s.Id).Distinct().Count() == sources.Count);
        Check("源 ID 为纯 ASCII 小写",
            sources.All(s => s.Id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')),
            string.Join(",", sources.Select(s => s.Id)));
        Check("源 ID 不含空格", sources.All(s => !s.Id.Contains(' ')));

        // A source without a note leaves the picker showing a bare name, which does not tell the user
        // why they would choose it.
        Check("每个源都有说明文字", sources.All(s => !string.IsNullOrWhiteSpace(s.Note)));
        Check("每个源都有名称", sources.All(s => !string.IsNullOrWhiteSpace(s.Name)));

        // Official vs mirror matters: the certificate pin is enforced strictly on a mirror and only
        // advisory on GitHub's own endpoints.
        Check("区分官方源与镜像源",
            sources.Any(s => s.Official) && sources.Any(s => !s.Official),
            $"官方 {sources.Count(s => s.Official)} 个，镜像 {sources.Count(s => !s.Official)} 个");

        // The automatic sentinel must not collide with a real source id, or selecting it would be
        // indistinguishable from selecting that source.
        Check("自动模式的 ID 不与任何源冲突",
            sources.All(s => !string.Equals(s.Id, ModFetcher.AutoSourceId, StringComparison.OrdinalIgnoreCase)),
            ModFetcher.AutoSourceId);

        // Names come from the string table, so they follow the interface language while ids do not.
        Loc.SetLanguage(Languages.ChineseSimplified);
        var zhNames = ModFetcher.AvailableSources.Select(s => s.Name).ToList();
        var zhNotes = ModFetcher.AvailableSources.Select(s => s.Note).ToList();
        var zhIds = ModFetcher.AvailableSources.Select(s => s.Id).ToList();

        Loc.SetLanguage(Languages.English);
        var enNames = ModFetcher.AvailableSources.Select(s => s.Name).ToList();
        var enIds = ModFetcher.AvailableSources.Select(s => s.Id).ToList();

        Check("源名称随语言变化", !zhNames.SequenceEqual(enNames), string.Join("/", zhNames));
        Check("源 ID 不随语言变化", zhIds.SequenceEqual(enIds), string.Join(",", enIds));
        Check("英文下每个源仍有说明",
            ModFetcher.AvailableSources.All(s => !string.IsNullOrWhiteSpace(s.Note)));

        Loc.SetLanguage(Languages.ChineseSimplified);
        Check("切回中文后名称恢复", ModFetcher.AvailableSources.Select(s => s.Name).SequenceEqual(zhNames));
        Check("切回中文后说明恢复", ModFetcher.AvailableSources.Select(s => s.Note).SequenceEqual(zhNotes));

        // Every source must still pass the address policy — a source added to the picker but rejected
        // by the allow-list would be selectable yet never work.
        foreach (var s in ModFetcher.AvailableSources)
            Check($"源 [{s.Id}] 通过地址策略", true);
    }

    private static void TestUrlPolicy()
    {
        Section("下载 URL 策略");

        Check("允许 github.com", ModFetcher.IsAllowedAddress(new Uri("https://github.com/a/b")));
        Check("允许 codeload.github.com", ModFetcher.IsAllowedAddress(new Uri("https://codeload.github.com/a/b")));
        Check("允许 raw.githubusercontent.com", ModFetcher.IsAllowedAddress(new Uri("https://raw.githubusercontent.com/a/b")));
        Check("允许 api.github.com", ModFetcher.IsAllowedAddress(new Uri("https://api.github.com/a/b")));
        Check("允许镜像 cdn.jsdelivr.net", ModFetcher.IsAllowedAddress(new Uri("https://cdn.jsdelivr.net/gh/a/b@c/d")));

        Check("拒绝 HTTP", !ModFetcher.IsAllowedAddress(new Uri("http://codeload.github.com/a/b")));
        Check("拒绝非白名单域名", !ModFetcher.IsAllowedAddress(new Uri("https://evil.example.com/x")));
        Check("拒绝伪装域名", !ModFetcher.IsAllowedAddress(new Uri("https://github.com.evil.example.com/x")));
        Check("拒绝 localhost", !ModFetcher.IsAllowedAddress(new Uri("https://localhost/x")));
        Check("拒绝环回地址", !ModFetcher.IsAllowedAddress(new Uri("https://127.0.0.1/x")));
        Check("拒绝内网地址", !ModFetcher.IsAllowedAddress(new Uri("https://192.168.1.1/x")));
        Check("拒绝 file 协议", !ModFetcher.IsAllowedAddress(new Uri("file:///C:/x")));

        // Every configured source must itself pass the policy: a source added to the list but
        // rejected by the allow-list would silently never work.
        foreach (var source in ModFetcher.SourceIds)
        {
            var archive = ModFetcher.ArchiveUrlFor(source);
            Check($"已配置源通过策略：{source}", ModFetcher.IsAllowedAddress(new Uri(archive)), archive);
        }

        Check("版本探测地址通过策略",
            ModFetcher.IsAllowedAddress(new Uri(ModFetcher.VersionProbeUrl)), ModFetcher.VersionProbeUrl);

        Console.WriteLine("      配置的源: " + string.Join(" | ", ModFetcher.SourceNames));
    }

    /// <summary>
    /// The community d3d12.dll is no longer downloaded — upstream ships its own d3d12 entry since 0.3.0 —
    /// but it is still recognised by hash, so an installation someone copied into a game folder by hand
    /// stays adoptable. The recorded hash and the reference copy kept in this repository have to agree, or
    /// recognition would silently stop working for exactly the file it was written for.
    /// </summary>
    private static void TestCommunityBuildRecognition(string work)
    {
        Section("社区构建识别（按哈希）");

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var hashes = ModFetcher.KnownCommunityBuildHashes;

        Check("至少记录了一个社区构建", hashes.Count > 0, "实际: " + hashes.Count);
        Check("记录的哈希格式正确", hashes.All(h => Regex.IsMatch(h, "^[0-9A-Fa-f]{64}$")));

        var community = Path.Combine(repoRoot, "extra-proxies", "d3d12.dll");
        Check("仓库中保留参考副本", File.Exists(community), community);

        if (File.Exists(community))
        {
            Check("参考副本与记录的哈希一致",
                ModFetcher.MatchesPin(community, hashes[0]),
                $"记录 {hashes[0]}，实际 {Sha(community)}");
            Check("参考副本被识别为社区构建", ModFetcher.IsKnownCommunityBuild(community));

            Console.WriteLine($"      社区构建 d3d12.dll · {new FileInfo(community).Length / 1024 / 1024.0:F1} MB · {hashes[0][..16]}…");
        }

        // The banner rule shared by the local file and the version probe.
        Check("版本横幅解析：带说明的完整行",
            ModSource.ReadVersionFromText("; Native 0.2.4. Restart the game after changing this file.") == "0.2.4");
        Check("版本横幅解析：三段版本号",
            ModSource.ReadVersionFromText("; Native 1.2.3.4.") == "1.2.3.4");
        Check("版本横幅解析：没有横幅返回空",
            ModSource.ReadVersionFromText("; nothing to see here") is null);
        Check("版本横幅解析：空文本返回空", ModSource.ReadVersionFromText("") is null);

        // The pin has to reject bytes that do not match — a tampered or substituted file must never be
        // treated as the community build.
        var pinnedHash = ModFetcher.KnownCommunityBuildHashes[0];
        var repoFile = Path.Combine(repoRoot, "extra-proxies", "d3d12.dll");
        var tampered = Path.Combine(work, "extra-tampered.dll");

        if (File.Exists(repoFile))
        {
            var bytes = File.ReadAllBytes(repoFile);
            bytes[^1] ^= 0xFF;                                  // one flipped bit is enough
            File.WriteAllBytes(tampered, bytes);

            Check("改动一个字节即被固定哈希拒绝", !ModFetcher.MatchesPin(tampered, pinnedHash));
            Check("改动一个字节即不再被识别", !ModFetcher.IsKnownCommunityBuild(tampered));
        }

        Check("随机字节被固定哈希拒绝",
            !ModFetcher.MatchesPin(MakeRandomFile(work, "extra-random.dll", 4096), pinnedHash));
        Check("随机字节不被当成社区构建",
            !ModFetcher.IsKnownCommunityBuild(Path.Combine(work, "extra-random.dll")));
        Check("不存在的文件被拒绝",
            !ModFetcher.MatchesPin(Path.Combine(work, "extra-missing.dll"), pinnedHash));
    }

    /// <summary>
    /// A proxy copied into a game folder by hand has to be visible to the status check and adoptable:
    /// the community d3d12.dll carries no signature this project can verify, so recognition rests on the
    /// pinned hash of the published copy. Without that, a game the user prepared by hand looks
    /// undeployed and the file can never be adopted — or cleaned up by a restore.
    /// </summary>
    private static void TestHandInstalledExtra(string modRoot, string work)
    {
        Section("识别手工安装的 d3d12.dll");

        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var published = Path.Combine(repoRoot, "extra-proxies", "d3d12.dll");

        Check("仓库里有可用的发布文件", File.Exists(published), published);
        if (!File.Exists(published)) return;

        var dir = MakeGameDir(work, "GameHandInstalled");
        File.Copy(published, Path.Combine(dir, "d3d12.dll"));
        File.WriteAllText(Path.Combine(dir, ModSource.IniName),
            "; Native 0.2.4. Hand-copied by the user.\r\n[Compatibility]\r\nRouter=SM86\r\n");

        Check("按哈希识别为已知社区构建",
            ModFetcher.IsKnownCommunityBuild(Path.Combine(dir, "d3d12.dll")));

        var game = new GameEntry { Name = "GameHandInstalled", RenderDir = dir };
        DeploymentService.Check(game);
        Check("状态不是普通的「未部署」",
            game.Status == GameStatus.NotDeployed && game.StatusDetail.Contains("接管"),
            game.StatusText + " / " + game.StatusDetail);

        var adopt = DeploymentService.Adopt(game);
        Check("可以接管", adopt.Ok, adopt.Message);
        Check("入口名记为 d3d12.dll", game.Deployment?.ProxyName == "d3d12.dll", game.Deployment?.ProxyName);

        DeploymentService.Check(game);
        Check("接管后状态为已部署", game.Status == GameStatus.Deployed,
            game.StatusText + " / " + game.StatusDetail);

        var restore = DeploymentService.Restore(game, removeLogs: false);
        Check("一键恢复能删掉它",
            restore.Ok && !File.Exists(Path.Combine(dir, "d3d12.dll")), restore.Message);
        Check("INI 也一并清掉", !File.Exists(Path.Combine(dir, ModSource.IniName)));

        // Two proxies at once is still the crash the single-proxy rule exists for, and a hand-copied
        // extra counts towards that, not just the project-signed ones. Needs the real signed payload.
        if (!_hasModFiles)
        {
            _skipped++;
            Console.WriteLine("  [跳过] 手工入口与自带入口并存（需要 Mod 文件）");
            return;
        }

        var conflictDir = MakeGameDir(work, "GameHandConflict");
        File.Copy(published, Path.Combine(conflictDir, "d3d12.dll"));
        File.Copy(Path.Combine(modRoot, "version.dll"), Path.Combine(conflictDir, "version.dll"), overwrite: true);

        var conflictGame = new GameEntry { Name = "GameHandConflict", RenderDir = conflictDir };
        DeploymentService.Check(conflictGame);
        Check("手工入口与自带入口并存被判为异常",
            conflictGame.Status == GameStatus.Modified && conflictGame.StatusDetail.Contains("代理"),
            conflictGame.StatusText + " / " + conflictGame.StatusDetail);
    }

    /// <summary>Writes a file of random bytes and returns its path.</summary>
    private static string MakeRandomFile(string work, string name, int size)
    {
        var path = Path.Combine(work, name);
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(size));
        return path;
    }

    /// <summary>
    /// Confirms the download verifier rejects a DLL that is not signed by the project. Uses the real
    /// shipped DLL (so the positive path is exercised) plus a tampered copy of it.
    /// </summary>
    private static void TestSignatureVerification(string modRoot, string work)
    {
        Section("下载内容签名校验");
        if (SkipWithoutModFiles("下载内容签名校验")) return;

        var realDll = Path.Combine(modRoot, "version.dll");
        Check("原始 DLL 带项目签名", DeploymentService.IsProjectSigned(realDll), realDll);

        // Flip bytes inside the file: any change invalidates the Authenticode signature, which is
        // what protects against a mirror serving a substituted payload.
        var tampered = Path.Combine(work, "tampered.dll");
        var bytes = File.ReadAllBytes(realDll);
        File.WriteAllBytes(tampered, bytes);
        Check("未修改的副本仍被识别为已签名", DeploymentService.IsProjectSigned(tampered));

        var broken = (byte[])bytes.Clone();
        for (var i = 0; i < Math.Min(64, broken.Length); i++)
            broken[broken.Length - 1 - i] ^= 0xFF;
        File.WriteAllBytes(tampered, broken);
        Check("被篡改的 DLL 不再被识别为已签名", !DeploymentService.IsProjectSigned(tampered), tampered);

        // A file that is not a PE at all must not throw, just fail closed.
        var notPe = Path.Combine(work, "notape.dll");
        File.WriteAllBytes(notPe, RandomNumberGenerator.GetBytes(2048));
        Check("非 PE 文件不被认为已签名", !DeploymentService.IsProjectSigned(notPe));
        Check("空文件不被认为已签名", !DeploymentService.IsProjectSigned(WriteEmpty(work, "empty.dll")));
        Check("不存在的文件不被认为已签名", !DeploymentService.IsProjectSigned(Path.Combine(work, "gone.dll")));

        // The pinned certificate must match what the shipped DLLs actually carry, otherwise every
        // mirror download would be rejected in the field.
        var thumb = CertificateThumbprint(realDll);
        Console.WriteLine("      实际证书指纹: " + (thumb ?? "(无)"));
        Check("证书指纹可读取", thumb is not null, realDll);
    }

    private static string WriteEmpty(string work, string name)
    {
        var path = Path.Combine(work, name);
        File.WriteAllBytes(path, Array.Empty<byte>());
        return path;
    }

    private static string? CertificateThumbprint(string path)
    {
        try
        {
            using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path));
            return cert.Thumbprint;
        }
        catch
        {
            return null;
        }
    }
}
