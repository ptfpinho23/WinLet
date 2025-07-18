using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinLet.Core;

namespace WinLet.Service;

[SupportedOSPlatform("windows")]
public class Program
{
    public static async Task Main(string[] args)
    {
        // Initialize crash dump handling for Windows
        CrashDumpHelper.InitializeCrashDumpHandler();
        
        var builder = Host.CreateApplicationBuilder(args);
        
        // Parse command line arguments
        string? configPath = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--config-path" && i + 1 < args.Length)
            {
                configPath = args[i + 1];
                break;
            }
        }
        
        // Add Windows Services support
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "WinLet Service Host";
        });

        // Add core services
        builder.Services.AddSingleton<LogManager>();
        
        // Add the config path as a singleton so the worker service can access it
        builder.Services.AddSingleton(provider => new ConfigPathService(configPath));
        
        // Add the worker service
        builder.Services.AddHostedService<WinLetWorkerService>();

        // Configure logging
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        
        // We'll set up file logging after we load the config to use the same directory
        
        // Write startup info to temp log file initially
        try
        {
            var tempLogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinLet", "Logs");
            Directory.CreateDirectory(tempLogDir);
            var tempLogFile = Path.Combine(tempLogDir, "service.log");
            var startupMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WinLet Service starting with args: [{string.Join(", ", args)}]\n";
            File.AppendAllText(tempLogFile, startupMessage);
        }
        catch (Exception ex)
        {
            // Log full exception details for better debugging
            Console.WriteLine($"Could not write to log file: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException != null)
            {
                Console.WriteLine($"Inner exception: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            }
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
        
        try
        {
            // Event log requires admin privileges to create sources, so skip it
            // builder.Logging.AddEventLog(); 
        }
        catch (Exception)
        {
            // Event log might not be accessible, that's OK
        }

        var host = builder.Build();
        await host.RunAsync();
    }
}

// Simple service to hold the config path
public class ConfigPathService
{
    public string? ConfigPath { get; }
    
    public ConfigPathService(string? configPath)
    {
        ConfigPath = configPath;
    }
} 