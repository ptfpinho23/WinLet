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
        
        // Create a file for debugging (manual logging)
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WinLet", "Logs");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, "service.log");
        
        // Add custom file logger
        builder.Logging.AddProvider(new FileLoggerProvider(logFile));
        
        // Write startup info to log file
        try
        {
            var startupMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] WinLet Service starting with args: [{string.Join(", ", args)}]\n";
            File.AppendAllText(logFile, startupMessage);
        }
        catch (Exception ex)
        {
            // Can't write to log file, continue anyway
            Console.WriteLine($"Could not write to log file: {ex.Message}");
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