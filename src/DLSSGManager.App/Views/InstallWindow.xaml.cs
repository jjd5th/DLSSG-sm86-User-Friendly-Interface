using System.Windows;

namespace DLSSGManager;

public partial class InstallWindow : Window
{
    private readonly InstallPreview _preview;

    public InstallWindow(InstallPreview preview)
    {
        _preview = preview;
        InitializeComponent();
        Heading.Text = preview.GameName + " · " + preview.Release;
        Body.Text = preview.Executable + Environment.NewLine + Environment.NewLine + preview.Compatibility + Environment.NewLine + Environment.NewLine + preview.Manifest + Environment.NewLine + preview.Signature;
        foreach (var proxy in preview.ProxyOptions.Distinct(StringComparer.OrdinalIgnoreCase))
            ProxyBox.Items.Add(proxy);
        if (ProxyBox.Items.Count == 0)
            ProxyBox.Items.Add(preview.DefaultProxy);
        ProxyBox.SelectedItem = preview.DefaultProxy;
        if (ProxyBox.SelectedItem == null && ProxyBox.Items.Count > 0)
            ProxyBox.SelectedIndex = 0;
        OverwriteBox.Visibility = preview.Conflicts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RemoveBox.Visibility = preview.ExtraProxies.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AckBox.Visibility = preview.NeedsAcknowledgement ? Visibility.Visible : Visibility.Collapsed;
        var lines = preview.Warnings.ToList();
        if (preview.Conflicts.Count > 0)
            lines.Add("Existing files: " + string.Join(", ", preview.Conflicts.Select(conflict => conflict.RelativePath + " (" + conflict.Reason + ")")));
        if (preview.ExtraProxies.Count > 0)
            lines.Add("Other proxy DLLs already in the folder: " + string.Join(", ", preview.ExtraProxies));
        Warnings.Text = string.Join(Environment.NewLine + Environment.NewLine, lines);
        UpdateOk();
    }

    public InstallChoice? Choice { get; private set; }

    private void OnAck(object sender, RoutedEventArgs e) => UpdateOk();

    private void UpdateOk() => OkButton.IsEnabled = !_preview.NeedsAcknowledgement || AckBox.IsChecked == true;

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (OverwriteBox.Visibility == Visibility.Visible && OverwriteBox.IsChecked != true)
        {
            MessageBox.Show(this, "Approve the backup and replace step, or cancel.", "DLSSG Manager");
            return;
        }
        Choice = new InstallChoice
        {
            Proxy = ProxyBox.SelectedItem?.ToString() ?? _preview.DefaultProxy,
            OverwriteConflicts = OverwriteBox.IsChecked == true,
            RemoveExtraProxies = RemoveBox.IsChecked == true
        };
        DialogResult = true;
    }
}
