using System.IO;
using System.Windows;
using System.Windows.Controls;
using DLSSGManager.Core.Configuration;
using Microsoft.Win32;

namespace DLSSGManager;

public partial class ConfigureWindow : Window
{
    private readonly ConfigEditRequest _request;
    private bool _loading = true;
    private bool _rawDirty;

    public ConfigureWindow(ConfigEditRequest request)
    {
        _request = request;
        InitializeComponent();
        Title = "Configure " + request.Title;
        ReleaseText.Text = "Installed release " + request.Release + ". Restart the game after saving.";
        EnabledBox.IsChecked = request.Config.Enabled;
        for (var tier = 0; tier <= 3; tier++)
            OptimizedBox.Items.Add(tier);
        OptimizedBox.SelectedItem = Math.Clamp(request.Config.Optimized, 0, 3);
        foreach (var level in new[] { "0 errors off", "1 errors", "2 configuration", "3 verbose" })
            LogBox.Items.Add(level);
        LogBox.SelectedIndex = Math.Clamp(request.Config.LoggingLevel, 0, 3);
        EngineBox.Items.Add("DLSS");
        if (request.Capabilities.SupportsXess)
            EngineBox.Items.Add("XeSS");
        EngineBox.SelectedItem = request.Config.Engine;
        if (EngineBox.SelectedItem == null)
            EngineBox.SelectedIndex = 0;
        foreach (var preset in new[] { "Auto", "A", "B" })
            PresetBox.Items.Add(preset);
        if (request.Capabilities.SupportsPresetC)
            PresetBox.Items.Add("C");
        PresetBox.SelectedItem = request.Config.Preset;
        if (PresetBox.SelectedItem == null)
            PresetBox.SelectedIndex = 0;
        SrBox.Items.Add("Off");
        if (request.Capabilities.SupportsSuperResolution)
            SrBox.Items.Add("On");
        SrBox.SelectedItem = request.Config.SuperResolutionMode;
        if (SrBox.SelectedItem == null)
            SrBox.SelectedIndex = 0;
        foreach (var option in request.Service.Multipliers(request.Capabilities))
        {
            var radio = new RadioButton
            {
                Content = option.Label + " — " + option.Description,
                Tag = option.Value,
                IsEnabled = option.Enabled,
                ToolTip = option.Enabled ? option.Description : option.DisabledReason,
                Margin = new Thickness(0, 4, 0, 0),
                IsChecked = request.Config.MaxGeneratedFrames == option.Value
            };
            MultiplierPanel.Children.Insert(2, radio);
        }
        TimingBox.IsChecked = request.Config.FrameTiming;
        WarpBox.Text = request.Config.CatmullRomWarp ?? "";
        NewerBox.Text = request.Config.AccelerateNewerGpus ?? "";
        SpoofBox.Text = request.Config.SpoofArchToGame ?? "";
        foreach (var tier in new[] { "", "Auto", "Quality", "Balanced", "Performance", "UltraPerformance" })
            TierBox.Items.Add(tier);
        TierBox.SelectedItem = request.Config.SuperResolutionTier ?? "";
        foreach (var flow in new[] { "Coarse", "Middle", "Fine" })
            FlowBox.Items.Add(flow);
        FlowBox.SelectedItem = request.Config.FlowResolution;
        HudlessBox.IsChecked = request.Config.UseHudless;
        RawBox.Text = request.Config.Text;
        _loading = false;
        ShowOptimizedHelp();
    }

    private void OnOptimized(object sender, SelectionChangedEventArgs e) => ShowOptimizedHelp();

    private void ShowOptimizedHelp()
    {
        if (OptimizedBox.SelectedItem is int tier && ConfigurationService.OptimizedDescriptions.TryGetValue(tier, out var text))
            OptimizedHelp.Text = text;
    }

    private void OnRawChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading)
            _rawDirty = true;
    }

    private void PullForm()
    {
        var config = _request.Config;
        if (_rawDirty)
        {
            config.Text = RawBox.Text;
            return;
        }
        config.Enabled = EnabledBox.IsChecked == true;
        config.Optimized = OptimizedBox.SelectedItem is int tier ? tier : 2;
        config.LoggingLevel = LogBox.SelectedIndex;
        config.Engine = EngineBox.SelectedItem?.ToString() ?? "DLSS";
        config.Preset = PresetBox.SelectedItem?.ToString() ?? "Auto";
        config.SuperResolutionMode = SrBox.SelectedItem?.ToString() ?? "Off";
        var selected = MultiplierPanel.Children.OfType<RadioButton>().FirstOrDefault(radio => radio.IsChecked == true);
        if (selected?.Tag is int frames)
            config.MaxGeneratedFrames = frames;
        config.FrameTiming = TimingBox.IsChecked == true;
        config.CatmullRomWarp = Blank(WarpBox.Text);
        config.AccelerateNewerGpus = Blank(NewerBox.Text);
        config.SpoofArchToGame = Blank(SpoofBox.Text);
        config.SuperResolutionTier = Blank(TierBox.SelectedItem?.ToString());
        config.FlowResolution = FlowBox.SelectedItem?.ToString() ?? "Middle";
        config.UseHudless = HudlessBox.IsChecked == true;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            PullForm();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "INI");
            return;
        }
        var errors = _request.Service.Validate(_request.Config, _request.Capabilities, strictMultipliers: !_rawDirty);
        if (errors.Count > 0)
        {
            MessageBox.Show(this, string.Join(Environment.NewLine, errors), "Configuration");
            return;
        }
        DialogResult = true;
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        try
        {
            _request.Service.ApplyPreset(_request.Config, _request.Preset, _request.Capabilities);
            _rawDirty = false;
            _loading = true;
            EnabledBox.IsChecked = _request.Config.Enabled;
            OptimizedBox.SelectedItem = _request.Config.Optimized;
            PresetBox.SelectedItem = _request.Config.Preset;
            EngineBox.SelectedItem = _request.Config.Engine;
            SrBox.SelectedItem = _request.Config.SuperResolutionMode;
            foreach (var radio in MultiplierPanel.Children.OfType<RadioButton>())
                radio.IsChecked = radio.Tag is int value && value == _request.Config.MaxGeneratedFrames;
            RawBox.Text = _request.Config.Text;
            _loading = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Preset");
        }
    }

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "INI|*.ini" };
        if (dialog.ShowDialog() != true)
            return;
        _request.Config.Text = File.ReadAllText(dialog.FileName);
        RawBox.Text = _request.Config.Text;
        _rawDirty = true;
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        PullForm();
        var dialog = new SaveFileDialog { Filter = "INI|*.ini", FileName = "dlssg_sm86.ini" };
        if (dialog.ShowDialog() == true)
            File.WriteAllText(dialog.FileName, _request.Config.Text);
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}