namespace DLSSGManager.Cli;

/// <summary>
/// Command dispatch. Each command takes parsed arguments, writes to the given writers and returns an
/// exit code; <see cref="Program"/> only wires them to the console, so tests call this directly.
/// </summary>
public sealed partial class Cli(TextWriter output, TextWriter error)
{
    public const string Usage = """
        dlssgm — DLSSG 30 系管理器命令行

        用法:
          dlssgm list                                  列出游戏库
          dlssgm add --dir <渲染目录> [--name <名称>]   添加游戏
          dlssgm status [--game <名称|ID>]              检查部署状态（只读）
          dlssgm deploy --game <名称|ID> [--mod <目录>] [--allow-anticheat]
          dlssgm restore --game <名称|ID> [--remove-logs]
          dlssgm fetch [--mod <目录>] [--source <ID|auto>] [--package any|full|base]
          dlssgm import --zip <压缩包> [--mod <目录>]
          dlssgm verify [--mod <目录>]                  按版本清单核对本地 Mod 文件
          dlssgm recover                               处理中断的部署/恢复

        数据目录：%APPDATA%\DLSSGManager，可用环境变量 DLSSGMANAGER_HOME 覆盖。
        退出码：0 成功，1 失败，2 用法错误，3 未找到，4 需要管理员权限，5 被反作弊拦截。
        """;

    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            output.WriteLine(Usage);
            return args.Length == 0 ? ExitCode.Usage : ExitCode.Ok;
        }

        try
        {
            var parsed = Arguments.Parse(args.Skip(1));
            return args[0] switch
            {
                "list" => ListGames(),
                "add" => AddGame(parsed),
                "status" => ShowStatus(parsed),
                "deploy" => DeployGame(parsed),
                "restore" => RestoreGame(parsed),
                "fetch" => await FetchAsync(parsed, ct),
                "import" => await ImportAsync(parsed, ct),
                "verify" => VerifyPayload(parsed),
                "recover" => RecoverInterrupted(),
                _ => UnknownCommand(args[0]),
            };
        }
        catch (ArgumentException ex)
        {
            error.WriteLine(ex.Message);
            return ExitCode.Usage;
        }
    }

    private int UnknownCommand(string name)
    {
        error.WriteLine("未知命令：" + name);
        error.WriteLine(Usage);
        return ExitCode.Usage;
    }
}
