using System.Windows;
using System.Windows.Controls;

namespace DLSSGManager;

/// <summary>
/// Views and edits the GPU display name — the string Windows and games read when they decide features.
///
/// Some games gate features such as frame generation on the reported model, which is the legitimate
/// reason this exists: the dialog shows what is currently reported, what the hardware actually is, and
/// lets the user write either. Only the registry display name (DriverDesc) is touched; the hardware id,
/// the driver and its capabilities stay exactly as they are.
///
/// The write itself happens in the caller after the dialog closes, so the result can be logged with the
/// other operations. Everything here is presentation plus validation.
/// </summary>
public partial class GpuNameDialog : Window
{
    /// <summary>Text the user left in the name box, when they chose to apply a custom name.</summary>
    public string NewName => NameBox.Text;

    /// <summary>True when the user chose to restore the hardware's real name instead.</summary>
    public bool Restore { get; private set; }

    public GpuNameDialog(string currentName, string? registryName, string? realName)
    {
        InitializeComponent();

        var shown = string.IsNullOrWhiteSpace(registryName) ? currentName : registryName;
        CurrentText.Text = shown;
        RealText.Text = string.IsNullOrWhiteSpace(realName) ? Loc.T("GpuName.NoRealName") : realName;

        // Start from the name currently reported, so a custom value survives an accidental close.
        NameBox.Text = shown;

        NoteText.Text = Loc.T("GpuName.Note");
        NoteText.Visibility = Visibility.Visible;

        NameBox.TextChanged += (_, _) => Validate();
        Validate();
    }

    /// <summary>
    /// Keeps the apply button honest while typing: a blank, oversized or control-character name is not
    /// something anyone wants written into HKLM.
    /// </summary>
    private void Validate()
    {
        var reason = Gpu.InvalidDisplayNameReason(NameBox.Text);
        ApplyButton.Content = Loc.T("GpuName.Apply");
        ApplyButton.IsEnabled = reason is null;

        if (reason is not null)
        {
            NoteText.Text = reason;
            NoteText.Visibility = Visibility.Visible;
        }
        else
        {
            NoteText.Text = Loc.T("GpuName.Note");
            NoteText.Visibility = Visibility.Visible;
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Gpu.InvalidDisplayNameReason(NameBox.Text) is not null) return;

        DialogResult = true;
        Close();
    }

    private void Restore_Click(object sender, RoutedEventArgs e)
    {
        Restore = true;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
