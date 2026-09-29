using System.Windows.Controls;
using System.Windows.Threading;

namespace DLSSGManager;

/// <summary>
/// Progress sink that writes to the output pane and the on-disk manager log.
///
/// Writes are marshalled to the UI thread when needed. A control touched from a pool thread throws
/// on a non-UI thread, which <c>Application.DispatcherUnhandledException</c> cannot intercept — the
/// process would simply die. Since this class is the app's main narration channel, it is worth
/// making it safe regardless of which thread calls it.
/// </summary>
public sealed class OutputLog
{
    private readonly TextBox _box;
    private readonly TextBlock _summary;
    private readonly Dispatcher _dispatcher;

    public OutputLog(TextBox box, TextBlock summary)
    {
        _box = box;
        _summary = summary;
        _dispatcher = box.Dispatcher;
    }

    public void Write(string text)
    {
        // Always recorded, whatever thread we are on.
        AppPaths.Log(text);

        if (_dispatcher.CheckAccess()) Append(text);
        else _dispatcher.BeginInvoke(() => Append(text));
    }

    private void Append(string text)
    {
        _box.AppendText(text);
        _box.AppendText(Environment.NewLine);
        _box.ScrollToEnd();
        // Keep a single readable message visible even when the full output is collapsed.
        var lastLine = text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (lastLine is not null) _summary.Text = lastLine.Trim();
    }

    public void Clear()
    {
        if (_dispatcher.CheckAccess()) ClearOutput();
        else _dispatcher.BeginInvoke(ClearOutput);
    }

    private void ClearOutput()
    {
        _box.Clear();
        _summary.Text = "";
    }

    /// <summary>Reports the outcome of an operation, prefixing a tick or cross.</summary>
    public void Result(bool ok, string message) => Write((ok ? "✓ " : "✗ ") + message);

    /// <summary>Writes the indented detail lines an operation collected.</summary>
    public void Details(IEnumerable<string> lines)
    {
        foreach (var line in lines) Write("  " + line);
    }
}
