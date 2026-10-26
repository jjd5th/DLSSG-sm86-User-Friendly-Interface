using System.Text;
using DLSSGManager.Core.Abstractions;
using DLSSGManager.Core.Infrastructure;
using DLSSGManager.Core.Installation;
using DLSSGManager.Core.Models;

namespace DLSSGManager.Core.Diagnostics;

public sealed class DiagnosticsService : IDiagnosticsService
{
    public DiagnosticReport Build(DiagnosticContext context)
    {
        var builder = new StringBuilder();
        builder.AppendLine("DLSSG Manager diagnostics");
        builder.AppendLine("Created: " + context.CreatedUtcPlaceholder());
        builder.AppendLine("Application version: " + context.ApplicationVersion);
        builder.AppendLine();
        builder.AppendLine("System");
        if (context.System == null)
            builder.AppendLine("Hardware has not been scanned.");
        else
        {
            builder.AppendLine("Status: " + context.System.Summary);
            builder.AppendLine("Operating system: " + context.System.OperatingSystem);
            builder.AppendLine("NVIDIA driver: " + (context.System.NvidiaDriverVersion ?? "not detected"));
            builder.AppendLine("HAGS: " + (context.System.HagsEnabled switch { true => "on", false => "off", _ => "unknown" }));
            builder.AppendLine(context.System.HagsDetail);
            foreach (var gpu in context.System.Gpus)
                builder.AppendLine($"GPU: {gpu.VendorName} {gpu.Name}; {gpu.Architecture}; CC {gpu.ComputeCapability}; {gpu.ShaderModel}; VRAM {gpu.MemoryText}; device {gpu.DeviceId:X4}");
            foreach (var reason in context.System.Reasons)
                builder.AppendLine("Note: " + reason);
        }
        builder.AppendLine();
        builder.AppendLine("Releases");
        builder.AppendLine("Selected mod release: " + (context.Releases?.Selected?.Tag ?? "none"));
        builder.AppendLine(context.Releases?.Warning ?? "");
        builder.AppendLine(context.Releases?.ManagerMessage ?? "Manager update status was not checked.");
        builder.AppendLine();
        builder.AppendLine("Games");
        foreach (var game in context.Games)
        {
            builder.AppendLine($"- {game.Name} [{game.Launcher}]");
            builder.AppendLine("  Compatibility: " + (game.Compatibility?.Status.ToString() ?? "not evaluated"));
            builder.AppendLine("  " + (game.Compatibility?.Summary ?? ""));
            foreach (var line in game.Compatibility?.Evidence ?? [])
                builder.AppendLine("  Evidence: " + line);
            var install = game.Installation;
            builder.AppendLine("  Mod: " + (install == null ? "not installed" : install.State + " " + install.ReleaseTag));
            builder.AppendLine("  Activation: " + (install?.ActivationDetail ?? "not checked"));
            if (install != null)
            {
                foreach (var file in install.Files)
                {
                    var path = Path.Combine(install.InstallDirectory, file.RelativePath);
                    var current = File.Exists(path) ? Hashing.Sha256File(path) : "missing";
                    builder.AppendLine($"  File {file.RelativePath} recorded {file.Sha256} current {current}");
                }
            }
        }
        builder.AppendLine();
        builder.AppendLine("History");
        foreach (var entry in context.History)
            builder.AppendLine($"{entry.Utc:u} {entry.Action} {entry.Result} {entry.Detail}");
        return new DiagnosticReport
        {
            Text = Redaction.Apply(builder.ToString()),
            CreatedUtc = DateTimeOffset.UtcNow
        };
    }

    public string Export(DiagnosticReport report, string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "dlssg-manager-diagnostics.txt");
        File.WriteAllText(path, report.Text);
        return path;
    }

    public string ReadModLogs(string? installDirectory)
    {
        if (string.IsNullOrWhiteSpace(installDirectory))
            return "No install directory is selected.";
        var logs = Path.Combine(installDirectory, "dlssg_sm86", "logs");
        if (!Directory.Exists(logs))
            return "No dlssg_sm86\\logs directory. The mod writes logs there after the game runs.";
        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(logs, "*.jsonl").OrderByDescending(File.GetLastWriteTimeUtc).Take(3))
        {
            builder.AppendLine("---- " + Path.GetFileName(file));
            try
            {
                var lines = File.ReadLines(file).TakeLast(200);
                foreach (var line in lines)
                    builder.AppendLine(line);
            }
            catch (Exception ex)
            {
                builder.AppendLine(ex.Message);
            }
        }
        return Redaction.Apply(builder.Length == 0 ? "The log directory is empty." : builder.ToString());
    }

    public RuntimeActivation ReadActivation(string? installDirectory, out string detail) =>
        ActivationReader.Read(installDirectory, out detail);
}

file static class DiagnosticText
{
    public static string CreatedUtcPlaceholder(this DiagnosticContext context) => DateTimeOffset.UtcNow.ToString("u");
}
