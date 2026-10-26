using System.Windows;
using System.Windows.Controls;
using DLSSGManager.Core.Models;
using Microsoft.Win32;

namespace DLSSGManager;

public interface IUserPrompt
{
    void Info(string title, string message);
    void Error(string title, string message);
    bool Confirm(string title, string message);
    InstallChoice? ConfirmInstall(InstallPreview preview);
    string? PickFolder();
    string? PickFile(string filter);
    string? PickExecutable(string initialDirectory);
    string? SaveFile(string filter, string name);
    void ShowText(string title, string text);
    bool EditConfiguration(ConfigEditRequest request);
}

public sealed class InstallPreview
{
    public string GameName { get; init; } = "";
    public string Executable { get; init; } = "";
    public string Release { get; init; } = "";
    public string Compatibility { get; init; } = "";
    public string Manifest { get; init; } = "";
    public string Signature { get; init; } = "";
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<DllConflict> Conflicts { get; init; } = [];
    public IReadOnlyList<string> ExtraProxies { get; init; } = [];
    public IReadOnlyList<string> ProxyOptions { get; init; } = [];
    public string DefaultProxy { get; init; } = "version.dll";
    public bool NeedsAcknowledgement { get; init; }
}

public sealed class InstallChoice
{
    public required string Proxy { get; init; }
    public bool OverwriteConflicts { get; init; }
    public bool RemoveExtraProxies { get; init; }
}

public sealed class ConfigEditRequest
{
    public required string Title { get; init; }
    public required string Release { get; init; }
    public required Core.Abstractions.GameConfig Config { get; init; }
    public required Core.Abstractions.ConfigCapabilities Capabilities { get; init; }
    public required Core.Abstractions.IConfigurationService Service { get; init; }
    public string Preset { get; init; } = "Recommended";
}

public sealed class UserPrompt : IUserPrompt
{
    public void Info(string title, string message) => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    public void Error(string title, string message) => MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    public bool Confirm(string title, string message) => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public InstallChoice? ConfirmInstall(InstallPreview preview)
    {
        var window = new InstallWindow(preview) { Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true ? window.Choice : null;
    }

    public string? PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Select a game folder" };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    public string? PickFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickExecutable(string initialDirectory)
    {
        var dialog = new OpenFileDialog
        {
            Filter = "Executable|*.exe",
            InitialDirectory = initialDirectory,
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveFile(string filter, string name)
    {
        var dialog = new SaveFileDialog { Filter = filter, FileName = name };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public void ShowText(string title, string text)
    {
        var box = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Margin = new Thickness(12)
        };
        var window = new Window
        {
            Title = title,
            Width = 860,
            Height = 560,
            Content = box,
            Background = System.Windows.Media.Brushes.Transparent
        };
        window.SetResourceReference(Window.BackgroundProperty, "Brush.Bg");
        box.SetResourceReference(Control.ForegroundProperty, "Brush.Text");
        box.SetResourceReference(Control.BackgroundProperty, "Brush.Input");
        window.Show();
    }

    public bool EditConfiguration(ConfigEditRequest request)
    {
        var window = new ConfigureWindow(request) { Owner = Application.Current?.MainWindow };
        return window.ShowDialog() == true;
    }
}
