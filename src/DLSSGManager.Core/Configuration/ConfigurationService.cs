using DLSSGManager.Core.Abstractions;

namespace DLSSGManager.Core.Configuration;

public sealed class ConfigurationService : IConfigurationService
{
    public static IReadOnlyDictionary<int, string> OptimizedDescriptions { get; } = new Dictionary<int, string>
    {
        [0] = "Stock kernels. No acceleration. On RTX 30 this is still the SM86 route using the runtime's own kernel images, which is what makes stock numerics possible there.",
        [1] = "Every acceleration that stays bit-identical to the official runtime. Use this when you want a reference image.",
        [2] = "Factory default. Adds lossy image kernels. Upstream measured about 44–53 dB against the official output and no visible difference side by side with tier 1 on the tiles they compared. It is faster and no longer bit-identical.",
        [3] = "All lossy accelerations that are still faster. This is the largest image-quality cost."
    };

    public GameConfig Load(string path) => Parse(File.ReadAllText(path));

    public GameConfig Parse(string text) => new(IniDocument.Parse(text));

    public IReadOnlyList<string> Validate(GameConfig config, ConfigCapabilities capabilities, bool strictMultipliers)
    {
        var errors = new List<string>();
        if (config.Optimized is < 0 or > 3)
            errors.Add("Optimized must be 0, 1, 2, or 3.");
        var allowed = Multipliers(capabilities).Where(option => option.Enabled).Select(option => option.Value).ToHashSet();
        if (strictMultipliers && config.MaxGeneratedFrames != 0 && !allowed.Contains(config.MaxGeneratedFrames))
            errors.Add($"MaxGeneratedFrames={config.MaxGeneratedFrames} is not available for this release and game. {capabilities.SixXReason}");
        if (config.Engine is not ("DLSS" or "XeSS"))
            errors.Add("Engine must be DLSS or XeSS.");
        if (config.Engine.Equals("XeSS", StringComparison.OrdinalIgnoreCase) && !capabilities.SupportsXess)
            errors.Add("This package has no XeSS-FG kernels, so Engine=XeSS is not offered.");
        if (config.Preset.Equals("C", StringComparison.OrdinalIgnoreCase) && !capabilities.SupportsPresetC)
            errors.Add("Preset C (FG Lite) needs the 310.9.16 runtime.");
        if (config.Preset is not ("Auto" or "A" or "B" or "C"))
            errors.Add("Preset must be Auto, A, B, or C.");
        if (config.LoggingLevel is < 0 or > 3)
            errors.Add("Logging level must be 0 through 3.");
        if (config.SuperResolutionMode.Equals("On", StringComparison.OrdinalIgnoreCase) && !capabilities.SupportsSuperResolution)
            errors.Add("Super Resolution INT8 needs the sr files from the full release package.");
        if (config.CatmullRomWarp != null && config.CatmullRomWarp is not ("0" or "1" or "2"))
            errors.Add("CatmullRomWarp must be 0, 1, or 2.");
        if (config.AccelerateNewerGpus != null && config.AccelerateNewerGpus is not ("0" or "1"))
            errors.Add("AccelerateNewerGpus must be 0 or 1.");
        if (config.SpoofArchToGame != null && config.SpoofArchToGame is not ("0" or "1"))
            errors.Add("SpoofArchToGame must be 0 or 1.");
        var tier = config.SuperResolutionTier;
        if (tier != null && tier is not ("Auto" or "Quality" or "Balanced" or "Performance" or "UltraPerformance"))
            errors.Add("Super Resolution Tier must be Auto, Quality, Balanced, Performance, or UltraPerformance.");
        if (config.FlowResolution is not ("Coarse" or "Middle" or "Fine"))
            errors.Add("FlowResolution must be Coarse, Middle, or Fine.");
        return errors;
    }

    public void ApplyPreset(GameConfig config, string preset, ConfigCapabilities capabilities)
    {
        config.Enabled = true;
        config.Engine = "DLSS";
        config.LoggingLevel = Math.Clamp(config.LoggingLevel, 0, 3);
        if (string.IsNullOrWhiteSpace(config.Document.Get("Logging", "Level")))
            config.LoggingLevel = 1;
        config.SuperResolutionMode = "Off";
        var cap = Math.Max(1, Math.Min(3, capabilities.MaxGeneratedFrames));
        config.MaxGeneratedFrames = cap;
        switch (preset)
        {
            case "Stock":
                config.Optimized = 0;
                config.Preset = "Auto";
                break;
            case "Reference":
                config.Optimized = 1;
                config.Preset = "Auto";
                break;
            case "Fastest":
                config.Optimized = 3;
                config.Preset = "Auto";
                break;
            case "FG Lite":
                if (!capabilities.SupportsPresetC)
                    throw new InvalidOperationException("This release does not support preset C.");
                config.Optimized = 2;
                config.Preset = "C";
                break;
            default:
                config.Optimized = 2;
                config.Preset = "Auto";
                break;
        }
    }

    public IReadOnlyList<string> Migrate(IniDocument from, IniDocument onto)
    {
        var notes = new List<string>();
        void copy(string section, string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                onto.Set(section, key, value);
        }
        var optimized = from.Get("DLSSG", "Optimized") ?? from.Get("FrameGeneration", "Optimized") ?? from.Get("Compatibility", "OptimizedKernels");
        if (optimized != null)
        {
            onto.Set("DLSSG", "Optimized", optimized);
            if (from.Get("DLSSG", "Optimized") == null)
                notes.Add("Optimized was moved into [DLSSG]. Review it; an old value is kept and the new file uses the current schema.");
        }
        var preset = from.Get("DLSSG", "Preset") ?? from.Get("Compatibility", "Preset");
        if (preset != null && from.Get("DLSSG", "Preset") == null)
        {
            onto.Set("DLSSG", "Preset", preset);
            notes.Add("Preset was moved into [DLSSG].");
        }
        else
            copy("DLSSG", "Preset", preset);
        copy("General", "Enabled", from.Get("General", "Enabled"));
        copy("FrameGeneration", "MaxGeneratedFrames", from.Get("FrameGeneration", "MaxGeneratedFrames"));
        copy("FrameGeneration", "Engine", from.Get("FrameGeneration", "Engine"));
        copy("Logging", "Level", from.Get("Logging", "Level"));
        copy("SuperResolution", "Mode", from.Get("SuperResolution", "Mode"));
        if (notes.Count == 0)
            notes.Add("Existing configuration values were copied onto the new release INI.");
        else
            notes.Add("Keys that this release no longer uses were left behind. Restart the game after saving.");
        return notes;
    }

    public IReadOnlyList<FrameMultiplierOption> Multipliers(ConfigCapabilities capabilities)
    {
        bool allow(int generated) => capabilities.MaxGeneratedFrames >= generated;
        return
        [
            new(1, "2×", "One generated frame.", allow(1), "This runtime does not report 2×."),
            new(2, "3×", "Two generated frames.", allow(2), "This runtime's ceiling is below 3×."),
            new(3, "4×", "Three generated frames. This is the factory ceiling.", allow(3), "This runtime's ceiling is below 4×."),
            new(5, "6×", "Five generated frames. Only when the runtime and the game both support it.", allow(5) && capabilities.GameSupports6x, capabilities.SixXReason)
        ];
    }

    public void Save(string path, GameConfig config)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, config.Text);
    }
}
