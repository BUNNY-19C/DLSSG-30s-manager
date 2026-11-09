namespace DLSSGManager.Cli;

public sealed partial class Cli
{
    private (AppData Data, int? Error) LoadLibrary()
    {
        var loaded = LibraryStore.LoadWithStatus();
        if (loaded.Outcome is LibraryLoadOutcome.Quarantined or LibraryLoadOutcome.RecoveredFromBackup)
            error.WriteLine($"警告：游戏库无法读取（{loaded.Error}），已另存为 {loaded.SetAsidePath}");
        if (loaded.Outcome == LibraryLoadOutcome.ReadOnly)
        {
            error.WriteLine($"游戏库无法读取，也无法移开（{loaded.Error}）；为保护部署记录，命令行不会继续。");
            return (loaded.Data, ExitCode.Failed);
        }
        return (loaded.Data, null);
    }

    private bool SaveLibrary(AppData data)
    {
        var result = LibraryStore.Save(data);
        if (!result.Ok) error.WriteLine("保存游戏库失败：" + result.Message);
        return result.Ok;
    }

    /// <summary>A game by ID, or by name when exactly one game has it.</summary>
    private static GameEntry? FindGame(AppData data, string key)
    {
        var byId = data.Games.FirstOrDefault(g => string.Equals(g.Id, key, StringComparison.OrdinalIgnoreCase));
        if (byId is not null) return byId;
        var byName = data.Games.Where(g => string.Equals(g.Name, key, StringComparison.OrdinalIgnoreCase)).Take(2).ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    private GameEntry? RequireGame(AppData data, Arguments a)
    {
        var key = a.Required("game");
        var game = FindGame(data, key);
        if (game is null) error.WriteLine($"找不到游戏：{key}（名称重复时请用 ID，见 dlssgm list）");
        return game;
    }

    private int ListGames()
    {
        var (data, failed) = LoadLibrary();
        if (failed is { } code) return code;
        foreach (var g in data.Games)
            output.WriteLine($"{g.Id}  {g.Name}  {(g.Deployment is null ? "-" : g.Deployment.ProxyName)}  {g.RenderDir}");
        return ExitCode.Ok;
    }

    private int AddGame(Arguments a)
    {
        var (data, failed) = LoadLibrary();
        if (failed is { } code) return code;

        var dir = Path.GetFullPath(a.Required("dir"));
        if (!Directory.Exists(dir))
        {
            error.WriteLine("目录不存在：" + dir);
            return ExitCode.NotFound;
        }
        if (data.Games.Any(g => !string.IsNullOrEmpty(g.RenderDir) &&
                                string.Equals(Path.GetFullPath(g.RenderDir), dir, StringComparison.OrdinalIgnoreCase)))
        {
            error.WriteLine("游戏库里已有这个目录：" + dir);
            return ExitCode.Failed;
        }

        var game = new GameEntry { Name = a.Optional("name") ?? new DirectoryInfo(dir).Name, RenderDir = dir };
        DeploymentService.Check(game);   // adopts a recognised manual install, like the window does
        data.Games.Add(game);
        if (!SaveLibrary(data)) return ExitCode.Failed;

        output.WriteLine(game.Id);
        return ExitCode.Ok;
    }

    private int ShowStatus(Arguments a)
    {
        var (data, failed) = LoadLibrary();
        if (failed is { } code) return code;

        var key = a.Optional("game");
        List<GameEntry> games;
        if (key is null) games = data.Games.ToList();
        else if (FindGame(data, key) is { } one) games = new List<GameEntry> { one };
        else
        {
            error.WriteLine("找不到游戏：" + key);
            return ExitCode.NotFound;
        }

        var allDeployed = true;
        foreach (var game in games)
        {
            var check = DeploymentService.Evaluate(game);
            allDeployed &= check.Status == GameStatus.Deployed;
            var protection = check.Protection?.HasKernelAntiCheat == true ? "  [内核级反作弊]" : "";
            output.WriteLine($"{game.Name}\t{check.Status}\t{check.Detail}{protection}");
        }
        // For one game the exit code answers "is it deployed and intact?"; for the list it is informational.
        return key is null || allDeployed ? ExitCode.Ok : ExitCode.Failed;
    }

    private int RecoverInterrupted()
    {
        var (data, failed) = LoadLibrary();
        if (failed is { } code) return code;

        var reports = PlanRecovery.RecoverAll();
        foreach (var report in reports)
        {
            foreach (var line in report.Lines) output.WriteLine(line);
            if (data.Games.FirstOrDefault(g => g.Id == report.GameId) is { } game) game.Deployment = report.Record;
        }
        if (reports.Count > 0 && !SaveLibrary(data)) return ExitCode.Failed;

        output.WriteLine(reports.Count == 0 ? "没有中断的操作。" : Loc.T("Recovery.Summary", reports.Count));
        return reports.Any(r => r.Failed) ? ExitCode.Failed : ExitCode.Ok;
    }
}
