using System.Text;
using DLSSGManager;
using DLSSGManager.Cli;

Console.OutputEncoding = Encoding.UTF8;
Loc.SetLanguage(Environment.GetEnvironmentVariable("DLSSGMANAGER_LANG") ?? System.Globalization.CultureInfo.CurrentUICulture.Name);

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // First Ctrl+C cancels a download cleanly; the journal covers anything interrupted harder.
    e.Cancel = true;
    cancel.Cancel();
};

try
{
    return await new Cli(Console.Out, Console.Error).RunAsync(args, cancel.Token);
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("已取消。");
    return ExitCode.Failed;
}
