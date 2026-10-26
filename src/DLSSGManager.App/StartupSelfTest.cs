using System.IO;
using System.Windows;
using DLSSGManager.Core.Infrastructure;
using Microsoft.Extensions.DependencyInjection;

namespace DLSSGManager;

public static class StartupSelfTest
{
    public static int Run(string[] args)
    {
        var data = Option(args, "--data") ?? Path.Combine(Path.GetTempPath(), "dlssg-manager-self-test", Guid.NewGuid().ToString("N"));
        var log = Option(args, "--log") ?? Path.Combine(data, "self-test.log");
        var lines = new List<string> { "DLSSG Manager self-test" };
        var code = 1;
        try
        {
            _ = typeof(Window).Assembly;
            _ = typeof(Application).Assembly;
            lines.Add("wpf: ok");
            var services = new ServiceCollection();
            services.AddSingleton(new AppDataPaths(data));
            using var provider = services.BuildServiceProvider();
            _ = provider.GetRequiredService<AppDataPaths>();
            lines.Add("dependency-injection: ok");
            code = StartupChecks.Run(data, lines);
        }
        catch (Exception ex)
        {
            lines.Add("startup: fail " + ex.Message);
            lines.Add("result: fail");
            code = 1;
        }
        try
        {
            var directory = Path.GetDirectoryName(log);
            if (!string.IsNullOrWhiteSpace(directory))
                Directory.CreateDirectory(directory);
            File.WriteAllLines(log, lines);
        }
        catch (Exception ex)
        {
            lines.Add("log: fail " + ex.Message);
            code = 1;
        }
        try
        {
            Console.Out.WriteLine(string.Join(Environment.NewLine, lines));
        }
        catch
        {
            // A Windows application may have no console. The log file is the result.
        }
        return code;
    }

    private static string? Option(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
    }
}
