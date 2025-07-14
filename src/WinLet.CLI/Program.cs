using System.CommandLine;
using System.Runtime.Versioning;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using WinLet.Core;

namespace WinLet.CLI;

[SupportedOSPlatform("windows")]
class Program
{
    [SupportedOSPlatform("windows")]
    static async Task<int> Main(string[] args)
    {
        // Create root command
        var rootCommand = new RootCommand("WinLet - Modern Windows Service Manager")
        {
            Name = "winlet"
        };

        // Add commands
        rootCommand.AddCommand(CreateInstallCommand());
        rootCommand.AddCommand(CreateUninstallCommand());
        rootCommand.AddCommand(CreateStartCommand());
        rootCommand.AddCommand(CreateStopCommand());
        rootCommand.AddCommand(CreateStatusCommand());
        rootCommand.AddCommand(CreateLogsCommand());

        return await rootCommand.InvokeAsync(args);
    }

    private static Command CreateInstallCommand()
    {
        var configOption = new Option<string>("--config", "Path to the TOML configuration file")
        {
            IsRequired = true
        };
        configOption.AddAlias("-c");

        var command = new Command("install", "Install a new Windows service")
        {
            configOption
        };

        command.SetHandler(async (string configPath) =>
        {
            try
            {
                var config = ConfigLoader.LoadFromFile(configPath);
                var serviceManager = CreateServiceManager();
                
                // Get the WinLet.Service executable path (should be alongside the CLI)
                var serviceExePath = Path.Combine(AppContext.BaseDirectory, "WinLetService.exe");
                if (!File.Exists(serviceExePath))
                {
                    // Try looking in the same directory as the CLI
                    serviceExePath = Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? "", "WinLetService.exe");
                }
                
                Console.WriteLine($"Installing service: {config.Name}");
                await serviceManager.InstallServiceAsync(config, serviceExePath);
                Console.WriteLine($"✅ Service '{config.Name}' installed successfully!");
                Console.WriteLine($"   Display Name: {config.DisplayName}");
                if (!string.IsNullOrEmpty(config.Description))
                    Console.WriteLine($"   Description: {config.Description}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                Environment.Exit(1);
            }
        }, configOption);

        return command;
    }

    private static Command CreateUninstallCommand()
    {
        var nameOption = new Option<string>("--name", "Service name to uninstall")
        {
            IsRequired = true
        };
        nameOption.AddAlias("-n");

        var command = new Command("uninstall", "Uninstall a Windows service")
        {
            nameOption
        };

        command.SetHandler(async (string serviceName) =>
        {
            try
            {
                var serviceManager = CreateServiceManager();
                
                Console.WriteLine($"Uninstalling service: {serviceName}");
                await serviceManager.UninstallServiceAsync(serviceName);
                Console.WriteLine($"✅ Service '{serviceName}' uninstalled successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                Environment.Exit(1);
            }
        }, nameOption);

        return command;
    }

    private static Command CreateStartCommand()
    {
        var nameOption = new Option<string>("--name", "Service name to start")
        {
            IsRequired = true
        };
        nameOption.AddAlias("-n");

        var command = new Command("start", "Start a Windows service")
        {
            nameOption
        };

        command.SetHandler(async (string serviceName) =>
        {
            try
            {
                var serviceManager = CreateServiceManager();
                
                Console.WriteLine($"Starting service: {serviceName}");
                await serviceManager.StartServiceAsync(serviceName);
                Console.WriteLine($"✅ Service '{serviceName}' started successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                Environment.Exit(1);
            }
        }, nameOption);

        return command;
    }

    private static Command CreateStopCommand()
    {
        var nameOption = new Option<string>("--name", "Service name to stop")
        {
            IsRequired = true
        };
        nameOption.AddAlias("-n");

        var command = new Command("stop", "Stop a Windows service")
        {
            nameOption
        };

        command.SetHandler(async (string serviceName) =>
        {
            try
            {
                var serviceManager = CreateServiceManager();
                
                Console.WriteLine($"Stopping service: {serviceName}");
                await serviceManager.StopServiceAsync(serviceName);
                Console.WriteLine($"✅ Service '{serviceName}' stopped successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                Environment.Exit(1);
            }
        }, nameOption);

        return command;
    }

    private static Command CreateStatusCommand()
    {
        var nameOption = new Option<string>("--name", "Service name to check")
        {
            IsRequired = true
        };
        nameOption.AddAlias("-n");

        var command = new Command("status", "Check service status")
        {
            nameOption
        };

        command.SetHandler(async (string serviceName) =>
        {
            try
            {
                var serviceManager = CreateServiceManager();
                var status = await serviceManager.GetServiceStatusAsync(serviceName);
                
                Console.WriteLine($"Service: {serviceName}");
                
                if (status.HasValue)
                {
                    Console.WriteLine($"Status: {status}");
                    
                    // Add colored output based on status
                    var statusColor = status.ToString().ToLower() switch
                    {
                        "running" => ConsoleColor.Green,
                        "stopped" => ConsoleColor.Red,
                        _ => ConsoleColor.Yellow
                    };
                    
                    Console.ForegroundColor = statusColor;
                    Console.WriteLine($"● {status}");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine("Status: Service not found");
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("● Not Found");
                    Console.ResetColor();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ Error: {ex.Message}");
                Environment.Exit(1);
            }
        }, nameOption);

        return command;
    }

    private static Command CreateLogsCommand()
    {
        var nameOption = new Option<string>("--name", "Service name")
        {
            IsRequired = true
        };
        nameOption.AddAlias("-n");

        var tailOption = new Option<int>("--tail", () => 100, "Number of lines to show");
        tailOption.AddAlias("-t");

        var command = new Command("logs", "View service logs")
        {
            nameOption,
            tailOption
        };

        command.SetHandler(async (string serviceName, int tailLines) =>
        {
            // For now, this is a placeholder - we'd need to implement log reading
            Console.WriteLine($"📋 Logs for service: {serviceName} (last {tailLines} lines)");
            Console.WriteLine("Log viewing functionality will be implemented based on logging configuration");
            await Task.CompletedTask;
        }, nameOption, tailOption);

        return command;
    }

    private static WindowsServiceManager CreateServiceManager()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddConsole());
        var serviceProvider = services.BuildServiceProvider();
        var logger = serviceProvider.GetRequiredService<ILogger<WindowsServiceManager>>();
        
        return new WindowsServiceManager(logger);
    }
} 