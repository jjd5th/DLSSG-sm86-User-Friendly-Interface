using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DLSSGManager.Core.Configuration;
using DLSSGManager.Core.Models;

namespace DLSSGManager.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly LibraryCoordinator _coordinator;

    public MainViewModel(LibraryCoordinator coordinator)
    {
        _coordinator = coordinator;
        Dashboard = new DashboardViewModel(coordinator, this);
        Library = new LibraryViewModel(coordinator, this);
        Updates = new UpdatesViewModel(coordinator);
        Settings = new SettingsViewModel(coordinator);
        Diagnostics = new DiagnosticsViewModel(coordinator);
        Detail = new GameDetailViewModel(coordinator, this);
        CurrentView = Dashboard;
        coordinator.Session.Changed += (_, _) =>
        {
            Dashboard.Refresh();
            Updates.Refresh();
            Detail.Refresh();
            Library.RefreshFilter();
        };
    }

    public SessionState Session => _coordinator.Session;
    public DashboardViewModel Dashboard { get; }
    public LibraryViewModel Library { get; }
    public UpdatesViewModel Updates { get; }
    public SettingsViewModel Settings { get; }
    public DiagnosticsViewModel Diagnostics { get; }
    public GameDetailViewModel Detail { get; }
    [ObservableProperty] private object currentView;
    [ObservableProperty] private string section = "Dashboard";

    public Task InitializeAsync() => _coordinator.InitializeAsync();

    [RelayCommand]
    private void Navigate(string section)
    {
        Section = section;
        CurrentView = section switch
        {
            "Library" => Library,
            "Updates" => Updates,
            "Settings" => Settings,
            "Diagnostics" => Diagnostics,
            _ => Dashboard
        };
        if (section == "Diagnostics")
            Diagnostics.Refresh();
        if (section == "Settings")
            Settings.Load();
    }

    public void OpenGame(GameItem item)
    {
        Detail.Game = item;
        Section = "Library";
        CurrentView = Detail;
    }
}

public partial class DashboardViewModel : ObservableObject
{
    private readonly LibraryCoordinator _coordinator;
    private readonly MainViewModel _main;

    public DashboardViewModel(LibraryCoordinator coordinator, MainViewModel main)
    {
        _coordinator = coordinator;
        _main = main;
        coordinator.Session.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(SessionState.IsBusy))
                OnPropertyChanged(nameof(ScanLabel));
        };
    }

    public SessionState Session => _coordinator.Session;
    public int Total => Session.Games.Count(game => !game.Record.Excluded);
    public int Compatible => Session.Games.Count(game => game.Record.Compatibility?.Status is GameCompatibilityStatus.Confirmed or GameCompatibilityStatus.Likely && !game.Record.Excluded);
    public int Modded => Session.Games.Count(game => game.Record.Installation?.State is not null and not ModInstallState.NotInstalled);
    public int Updates => Session.Games.Count(game => game.Record.Installation?.State == ModInstallState.UpdateAvailable);
    public string Gpu
    {
        get
        {
            var gpu = Session.SystemReport?.PrimaryGpu;
            if (gpu == null)
                return "Detecting GPU…";
            return gpu.Name.StartsWith(gpu.VendorName, StringComparison.OrdinalIgnoreCase)
                ? gpu.Name
                : gpu.VendorName + " " + gpu.Name;
        }
    }
    public string GpuDetail => Session.SystemReport?.PrimaryGpu == null
        ? ""
        : $"{Session.SystemReport.PrimaryGpu.Architecture} · {Session.SystemReport.PrimaryGpu.ComputeCapability} · {Session.SystemReport.PrimaryGpu.ShaderModel} · {Session.SystemReport.PrimaryGpu.MemoryText}";
    public string SystemStatus => Session.SystemReport?.Summary ?? "Checking system";
    public string Driver => Session.SystemReport?.NvidiaDriverVersion ?? "Not detected";
    public string Hags => Session.SystemReport?.HagsEnabled switch { true => "On", false => "Off", _ => "Unknown" };
    public string InstalledRelease => Session.Games.Select(game => game.Record.Installation?.ReleaseTag).FirstOrDefault(tag => !string.IsNullOrWhiteSpace(tag)) ?? "None";
    public string LatestRelease => Session.Releases?.Selected?.Tag ?? "Not checked";
    public string Reasons => Session.SystemReport == null ? "" : string.Join(Environment.NewLine, Session.SystemReport.Reasons);
    public bool HagsOff => Session.SystemReport?.HagsEnabled == false;
    public string ScanLabel => Session.IsBusy && Session.Work != null ? "Cancel scan" : "Scan games";

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (Session.IsBusy)
        {
            Session.Work?.Cancel();
            return;
        }
        Session.Work = new CancellationTokenSource();
        await _coordinator.ScanAsync(Session.Work.Token);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task CheckAsync() => _coordinator.CheckUpdatesAsync(CancellationToken.None);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UpdateAllAsync()
    {
        var games = Session.Games.Where(game => game.Record.Installation?.State == ModInstallState.UpdateAvailable &&
                                                game.Record.Compatibility?.Status is GameCompatibilityStatus.Confirmed or GameCompatibilityStatus.Likely);
        await _coordinator.UpdateSelectedAsync(games);
    }

    [RelayCommand]
    private void OpenSettings() => _main.NavigateCommand.Execute("Settings");

    [RelayCommand]
    private void HagsHelp() => _coordinator.ShowHagsHelp();

    private bool CanRun() => !Session.IsBusy;
}

public partial class LibraryViewModel : ObservableObject
{
    private readonly LibraryCoordinator _coordinator;
    private readonly MainViewModel _main;

    public LibraryViewModel(LibraryCoordinator coordinator, MainViewModel main)
    {
        _coordinator = coordinator;
        _main = main;
        var source = new CollectionViewSource { Source = coordinator.Session.Games };
        View = source.View;
        View.Filter = Filter;
        Filters = ["All games", "Compatible", "Potentially compatible", "Unknown", "Mod installed", "Updates available", "Unsupported"];
        SelectedFilter = Filters[0];
    }

    public ICollectionView View { get; }
    public string[] Filters { get; }
    [ObservableProperty] private string search = "";
    [ObservableProperty] private string selectedFilter;
    [ObservableProperty] private GameItem? selected;

    partial void OnSearchChanged(string value) => View.Refresh();
    partial void OnSelectedFilterChanged(string value) => View.Refresh();

    public void RefreshFilter() => View.Refresh();

    private bool Filter(object value)
    {
        if (value is not GameItem item || item.Record.Excluded)
            return false;
        if (!string.IsNullOrWhiteSpace(Search) && !item.Name.Contains(Search, StringComparison.OrdinalIgnoreCase))
            return false;
        return SelectedFilter switch
        {
            "Compatible" => item.Record.Compatibility?.Status == GameCompatibilityStatus.Confirmed,
            "Potentially compatible" => item.Record.Compatibility?.Status == GameCompatibilityStatus.Likely,
            "Unknown" => item.Record.Compatibility?.Status is null or GameCompatibilityStatus.Unknown,
            "Mod installed" => item.Record.Installation?.State is not null and not ModInstallState.NotInstalled,
            "Updates available" => item.Record.Installation?.State == ModInstallState.UpdateAvailable,
            "Unsupported" => item.Record.Compatibility?.Status is GameCompatibilityStatus.Unsupported or GameCompatibilityStatus.Blocked,
            _ => true
        };
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (Selected != null)
            _main.OpenGame(Selected);
    }

    [RelayCommand]
    private async Task PrimaryAsync(GameItem? item)
    {
        if (item == null)
            return;
        switch (item.PrimaryAction)
        {
            case "Install":
            case "Update":
                await _coordinator.InstallAsync(item, item.PrimaryAction == "Update");
                break;
            case "Repair":
                await _coordinator.RepairAsync(item);
                break;
            case "Configure":
                _coordinator.Configure(item);
                break;
            default:
                _main.OpenGame(item);
                break;
        }
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task ScanAsync() => _coordinator.ScanAsync(CancellationToken.None);

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task AddFolderAsync() => _coordinator.AddFolderAsync();

    private bool Idle() => !_coordinator.Session.IsBusy;
}

public partial class GameDetailViewModel : ObservableObject
{
    private readonly LibraryCoordinator _coordinator;
    private readonly MainViewModel _main;
    [ObservableProperty] private GameItem? game;

    public GameDetailViewModel(LibraryCoordinator coordinator, MainViewModel main)
    {
        _coordinator = coordinator;
        _main = main;
    }

    public string Evidence => Game == null ? "" : string.Join(Environment.NewLine, Game.Record.Compatibility?.Evidence ?? []);
    public string Explanation => Game?.Record.Compatibility?.Explanation ?? "";
    public string Api => Game?.Record.Compatibility?.GraphicsApi ?? "Unknown";
    public string AntiCheat => Game?.Record.Compatibility?.AntiCheat.Count > 0 ? string.Join(", ", Game.Record.Compatibility.AntiCheat) : "None detected";
    public string Activation => Game?.Record.Installation?.ActivationDetail ?? "Not installed. Installation files alone do not mean frame generation is running.";
    public string History => Game == null ? "" : string.Join(Environment.NewLine, _coordinator.History(Game.Record.Id).Select(entry => $"{entry.Utc.ToLocalTime():g}  {entry.Action}  {entry.Result}  {entry.Detail}"));
    private bool CanInstall() => Game != null && _coordinator.Session.SystemReport?.InstallAllowed == true &&
                                 Game.Record.Compatibility?.Status is GameCompatibilityStatus.Confirmed or GameCompatibilityStatus.Likely or GameCompatibilityStatus.Unknown &&
                                 Game.Record.Installation?.State is null or ModInstallState.NotInstalled or ModInstallState.ManualIntervention;
    private bool CanConfigure() => Game?.Record.Installation?.State is ModInstallState.InstalledVerified or ModInstallState.ConfigurationModified or ModInstallState.UpdateAvailable or ModInstallState.RepairRequired or ModInstallState.MissingFiles;
    private bool CanUpdate() => Game?.Record.Installation?.State == ModInstallState.UpdateAvailable;
    private bool CanRepair() => Game?.Record.Installation?.State is ModInstallState.RepairRequired or ModInstallState.MissingFiles or ModInstallState.InstalledVerified;
    private bool CanUninstall() => Game?.Record.Installation?.State is not null and not ModInstallState.NotInstalled && Game.Record.Installation?.Files.Count > 0;
    private bool CanRollback() => !string.IsNullOrWhiteSpace(Game?.Record.Installation?.RollbackDirectory);

    partial void OnGameChanged(GameItem? value) => Refresh();

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
        InstallCommand.NotifyCanExecuteChanged();
        ConfigureCommand.NotifyCanExecuteChanged();
        UpdateCommand.NotifyCanExecuteChanged();
        RepairCommand.NotifyCanExecuteChanged();
        UninstallCommand.NotifyCanExecuteChanged();
        RollbackCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
        ViewLogCommand.NotifyCanExecuteChanged();
        ChooseExeCommand.NotifyCanExecuteChanged();
        ChooseFolderCommand.NotifyCanExecuteChanged();
        ExcludeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private void Back() => _main.NavigateCommand.Execute("Library");
    [RelayCommand(CanExecute = nameof(CanInstall))] private Task InstallAsync() => _coordinator.InstallAsync(Game!, false);
    [RelayCommand(CanExecute = nameof(CanConfigure))] private void Configure() => _coordinator.Configure(Game!);
    [RelayCommand(CanExecute = nameof(CanUpdate))] private Task UpdateAsync() => _coordinator.InstallAsync(Game!, true);
    [RelayCommand(CanExecute = nameof(CanRepair))] private Task RepairAsync() => _coordinator.RepairAsync(Game!);
    [RelayCommand(CanExecute = nameof(CanUninstall))] private Task UninstallAsync() => _coordinator.UninstallAsync(Game!, false);
    [RelayCommand(CanExecute = nameof(CanRollback))] private Task RollbackAsync() => _coordinator.RollbackAsync(Game!);
    [RelayCommand(CanExecute = nameof(CanUninstall))] private Task RestoreAsync() => _coordinator.UninstallAsync(Game!, true);
    private bool HasGame() => Game != null;
    [RelayCommand(CanExecute = nameof(HasGame))] private void OpenFolder() => _coordinator.OpenDirectory(Game!);
    [RelayCommand(CanExecute = nameof(HasGame))] private void ViewLog() => _coordinator.ViewLog(Game!);
    [RelayCommand(CanExecute = nameof(HasGame))] private void ChooseExe() => _coordinator.ChooseExecutable(Game!);
    [RelayCommand(CanExecute = nameof(HasGame))] private void ChooseFolder() => _coordinator.SetInstallFolder(Game!);
    [RelayCommand(CanExecute = nameof(HasGame))] private void Exclude() => _coordinator.Exclude(Game!);
}

public partial class UpdatesViewModel : ObservableObject
{
    private readonly LibraryCoordinator _coordinator;
    public UpdatesViewModel(LibraryCoordinator coordinator) => _coordinator = coordinator;
    public SessionState Session => _coordinator.Session;
    public IEnumerable<GameItem> Pending => Session.Games.Where(game => game.Record.Installation?.State == ModInstallState.UpdateAvailable);
    public string Notes => Session.Releases?.Selected?.Body ?? "Check for updates to load release notes.";
    public string Published => Session.Releases?.Selected?.PublishedAt?.ToLocalTime().ToString("g") ?? "";
    public string Manager => Session.Releases?.ManagerMessage ?? "";

    public void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand(CanExecute = nameof(Idle))] private Task CheckAsync() => _coordinator.CheckUpdatesAsync(CancellationToken.None);
    [RelayCommand(CanExecute = nameof(Idle))] private Task UpdateSelectedAsync() => _coordinator.UpdateSelectedAsync(Pending.Where(game => game.UpdateSelected));
    [RelayCommand(CanExecute = nameof(Idle))] private Task UpdateCompatibleAsync() => _coordinator.UpdateSelectedAsync(Pending.Where(game => game.Record.Compatibility?.Status is GameCompatibilityStatus.Confirmed or GameCompatibilityStatus.Likely));
    [RelayCommand] private void OpenNotes() => _coordinator.OpenReleasePage(Session.Releases?.Selected?.HtmlUrl);
    [RelayCommand] private void OpenManager() => _coordinator.OpenReleasePage(Session.Releases?.ManagerLatest?.HtmlUrl);
    private bool Idle() => !Session.IsBusy;
}

public partial class SettingsViewModel : ObservableObject
{
    private readonly LibraryCoordinator _coordinator;
    public SettingsViewModel(LibraryCoordinator coordinator) => _coordinator = coordinator;
    public AppSettings Settings => _coordinator.Session.Settings;
    public string[] Themes { get; } = ["Dark", "Light"];
    public string[] Presets { get; } = ["Recommended", "Reference", "Stock", "Fastest", "FG Lite"];
    public ObservableCollection<string> Folders { get; } = new();
    [ObservableProperty] private string? selectedFolder;

    public void Load()
    {
        Folders.Clear();
        foreach (var folder in Settings.CustomDirectories)
            Folders.Add(folder);
        OnPropertyChanged(nameof(Settings));
    }

    [RelayCommand]
    private void Save()
    {
        Settings.CustomDirectories = Folders.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _coordinator.SaveSettings();
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Folder to scan" };
        if (dialog.ShowDialog() == true && !Folders.Contains(dialog.FolderName))
            Folders.Add(dialog.FolderName);
    }

    [RelayCommand]
    private void RemoveFolder()
    {
        if (SelectedFolder != null)
            Folders.Remove(SelectedFolder);
    }

    [RelayCommand]
    private void BrowseBackup()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Backup folder" };
        if (dialog.ShowDialog() == true)
            Settings.BackupDirectory = dialog.FolderName;
        OnPropertyChanged(nameof(Settings));
    }

    [RelayCommand]
    private void BrowseCache()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Update cache" };
        if (dialog.ShowDialog() == true)
            Settings.CacheDirectory = dialog.FolderName;
        OnPropertyChanged(nameof(Settings));
    }
}

public partial class DiagnosticsViewModel : ObservableObject
{
    private readonly LibraryCoordinator _coordinator;
    public DiagnosticsViewModel(LibraryCoordinator coordinator) => _coordinator = coordinator;
    [ObservableProperty] private string report = "Open this page to build a redacted diagnostics report. Installed files are not treated as proof that frame generation is running.";

    [RelayCommand]
    public void Refresh() => Report = _coordinator.PreviewDiagnostics();

    [RelayCommand] private void Export() => _coordinator.ExportDiagnostics();
    [RelayCommand] private void Import() => _coordinator.ImportCompatibility();
}
