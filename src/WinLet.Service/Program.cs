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
        
        // Add Windows Services support
        builder.Services.AddWindowsService(options =>
        {
            options.ServiceName = "WinLet Service Host";
        });

        // Add core services
        builder.Services.AddSingleton<LogManager>();
        
        // Add the worker service
        builder.Services.AddHostedService<WinLetWorkerService>();

        // Configure logging
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole();
        builder.Logging.AddEventLog(); // For Windows Event Log

        var host = builder.Build();
        await host.RunAsync();
    }
} 