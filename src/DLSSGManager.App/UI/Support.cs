using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using DLSSGManager.Core.Models;

namespace DLSSGManager;

public partial class SessionState : ObservableObject
{
    public System.Collections.ObjectModel.ObservableCollection<GameItem> Games { get; } = new();
    [ObservableProperty] private SystemCompatibilityReport? systemReport;
    [ObservableProperty] private ReleaseCheckResult? releases;
    [ObservableProperty] private bool isBusy;
    [ObservableProperty] private string statusText = "Ready";
    [ObservableProperty] private bool progressIndeterminate = true;
    [ObservableProperty] private double progress;
    public AppSettings Settings { get; set; } = new();
    public CancellationTokenSource? Work { get; set; }
    public event EventHandler? Changed;

    public void ReplaceGames(IEnumerable<GameItem> games)
    {
        Games.Clear();
        foreach (var game in games.OrderBy(item => item.Name))
            Games.Add(game);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Notify() => Changed?.Invoke(this, EventArgs.Empty);

    partial void OnIsBusyChanged(bool value) => System.Windows.Input.CommandManager.InvalidateRequerySuggested();
    partial void OnSystemReportChanged(SystemCompatibilityReport? value) => Changed?.Invoke(this, EventArgs.Empty);
}

public partial class GameItem : ObservableObject
{
    public GameItem(GameRecord record)
    {
        Record = record;
        Refresh();
    }

    public GameRecord Record { get; private set; }
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string launcher = "";
    [ObservableProperty] private string compatibility = "";
    [ObservableProperty] private string compatibilityDetail = "";
    [ObservableProperty] private string modStatus = "";
    [ObservableProperty] private string version = "";
    [ObservableProperty] private string primaryAction = "";
    [ObservableProperty] private string? artworkPath;
    [ObservableProperty] private string initial = "?";
    [ObservableProperty] private bool updateSelected = true;

    public void Replace(GameRecord record)
    {
        Record = record;
        Refresh();
    }

    public void Refresh()
    {
        Name = Record.Name;
        Initial = string.IsNullOrWhiteSpace(Record.Name) ? "?" : Record.Name.Trim()[0].ToString().ToUpperInvariant();
        Launcher = Record.Launcher switch
        {
            LauncherKind.BattleNet => "Battle.net",
            LauncherKind.Ea => "EA",
            LauncherKind.Gog => "GOG",
            _ => Record.Launcher.ToString()
        };
        Compatibility = Record.Compatibility?.Status switch
        {
            GameCompatibilityStatus.Confirmed => "Confirmed",
            GameCompatibilityStatus.Likely => "Likely",
            GameCompatibilityStatus.Unsupported => "Unsupported",
            GameCompatibilityStatus.Blocked => "Blocked",
            _ => "Unknown"
        };
        CompatibilityDetail = Record.Compatibility?.Summary ?? "Not evaluated";
        var install = Record.Installation;
        ModStatus = install == null || install.State == ModInstallState.NotInstalled
            ? "Not installed"
            : install.State switch
            {
                ModInstallState.InstalledVerified => "Installed",
                ModInstallState.UpdateAvailable => "Update available",
                ModInstallState.ConfigurationModified => "Config modified",
                ModInstallState.Conflicting => "Conflict",
                ModInstallState.MissingFiles => "Missing files",
                ModInstallState.RepairRequired => "Repair required",
                ModInstallState.ManualIntervention => "Manual intervention",
                _ => "Not installed"
            };
        Version = string.IsNullOrWhiteSpace(install?.ReleaseTag) ? "—" : install.ReleaseTag;
        PrimaryAction = install?.State switch
        {
            ModInstallState.UpdateAvailable => "Update",
            ModInstallState.MissingFiles or ModInstallState.RepairRequired => "Repair",
            ModInstallState.InstalledVerified or ModInstallState.ConfigurationModified => "Configure",
            _ => Record.Compatibility?.Status is GameCompatibilityStatus.Blocked or GameCompatibilityStatus.Unsupported ? "Inspect" : "Install"
        };
        ArtworkPath = Record.ArtworkPath;
        UpdateSelected = Record.Compatibility?.Status is GameCompatibilityStatus.Confirmed or GameCompatibilityStatus.Likely;
    }
}

public static class ThemeService
{
    public static void Apply(string theme)
    {
        var app = Application.Current;
        if (app == null)
            return;
        var source = theme.Equals("Light", StringComparison.OrdinalIgnoreCase)
            ? "Themes/Colors.Light.xaml"
            : "Themes/Colors.Dark.xaml";
        var next = new ResourceDictionary { Source = new Uri(source, UriKind.Relative) };
        var dictionaries = app.Resources.MergedDictionaries;
        var index = dictionaries.ToList().FindIndex(item => item.Source?.OriginalString.Contains("Colors.", StringComparison.OrdinalIgnoreCase) == true);
        if (index >= 0)
            dictionaries[index] = next;
        else
            dictionaries.Insert(0, next);
    }
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString() switch
        {
            "Confirmed" or "Installed" or "Compatible" => "Brush.Ok",
            "Likely" or "Potentially compatible" or "Config modified" => "Brush.Info",
            "Action required" or "Update available" or "Repair required" or "Missing files" or "Unknown" => "Brush.Warning",
            "Unsupported" or "Blocked" or "Conflict" or "Manual intervention" => "Brush.Danger",
            _ => "Brush.Muted"
        };
        return Application.Current.TryFindResource(key) ?? System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PathToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path))
            return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var invert = parameter?.ToString() == "invert";
        var empty = value == null || (value is string text && string.IsNullOrWhiteSpace(text));
        var visible = invert ? empty : !empty;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter?.ToString() == "invert")
            flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
