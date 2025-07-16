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
        // Convert relative config paths to absolute paths before UAC elevation
        args = ConvertConfigPathsToAbsolute(args);
        
        // Check for UAC elevation requirement for admin operations
        // But skip elevation for help commands
        if (args.Length > 0 && 
            UacHelper.RequiresAdminPrivileges(args[0]) && 
            !args.Contains("--help") && 
            !args.Contains("-h") && 
            !args.Contains("-?"))
        {
            if (UacHelper.ElevateIfRequired(args))
            {
                // Process was restarted with elevation, this code won't be reached
                return 0;
            }
        }

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
                Console.WriteLine("Starting service installation...");
                
                // Double-check admin privileges before proceeding
                if (!UacHelper.IsRunningAsAdministrator())
                {
                    Console.WriteLine("Error: Administrator privileges are required to install services.");
                    Console.WriteLine("   Please run this command from an elevated command prompt or allow UAC elevation.");
                    Environment.Exit(1);
                }

                Console.WriteLine($"Running with administrator privileges");
                Console.WriteLine($"Working directory: {Environment.CurrentDirectory}");
                Console.WriteLine($"Loading configuration from: {configPath}");
                
                if (!File.Exists(configPath))
                {
                    Console.WriteLine($"Error: Configuration file not found: {configPath}");
                    Environment.Exit(1);
                }

                var config = ConfigLoader.LoadFromFile(configPath);
                Console.WriteLine($"Configuration loaded successfully");
                Console.WriteLine($"   Service Name: {config.Name}");
                Console.WriteLine($"   Display Name: {config.DisplayName}");
                Console.WriteLine($"   Executable: {config.Process.Executable}");
                
                var serviceManager = CreateServiceManager();
                
                // Get the WinLet.Service executable path
                var serviceExePath = Path.Combine(AppContext.BaseDirectory, "service", "WinLetService.exe");
                Console.WriteLine($"Looking for WinLetService.exe at: {serviceExePath}");
                
                if (!File.Exists(serviceExePath))
                {
                    // Try looking in the CLI bin directory
                    var cliDir = Path.GetDirectoryName(Environment.ProcessPath) ?? "";
                    serviceExePath = Path.Combine(cliDir, "service", "WinLetService.exe");
                    Console.WriteLine($"Trying alternative location: {serviceExePath}");
                }
                
                if (!File.Exists(serviceExePath))
                {
                    // Try looking in the build output directory (for development)
                    var projectRoot = FindProjectRoot();
                    if (!string.IsNullOrEmpty(projectRoot))
                    {
                        serviceExePath = Path.Combine(projectRoot, "src", "WinLet.CLI", "bin", "Release", "net8.0", "service", "WinLetService.exe");
                        Console.WriteLine($"Trying development location: {serviceExePath}");
                    }
                }
                
                if (!File.Exists(serviceExePath))
                {
                    Console.WriteLine("Error: WinLetService.exe not found. Please ensure the service is built and published.");
                    Console.WriteLine($"   Expected location: {serviceExePath}");
                    Console.WriteLine($"   Current directory: {Environment.CurrentDirectory}");
                    Console.WriteLine($"   Process path: {Environment.ProcessPath}");
                    Environment.Exit(1);
                }
                
                Console.WriteLine($"Found WinLetService.exe at: {serviceExePath}");
                Console.WriteLine($"Installing service: {config.Name}");
                
                await serviceManager.InstallServiceAsync(config, serviceExePath, configPath);
                
                Console.WriteLine($"Service '{config.Name}' installed successfully!");
                Console.WriteLine($"   Display Name: {config.DisplayName}");
                if (!string.IsNullOrEmpty(config.Description))
                    Console.WriteLine($"   Description: {config.Description}");
                Console.WriteLine($"Use 'winlet start --name {config.Name}' to start the service");
            }
            catch (ConfigurationException ex)
            {
                Console.WriteLine($"Configuration Error: {ex.Message}");
                Environment.Exit(1);
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Error: Access denied. Administrator privileges are required to install services.");
                Console.WriteLine("   Please run this command from an elevated command prompt.");
                Environment.Exit(1);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5) // ERROR_ACCESS_DENIED
            {
                Console.WriteLine("Error: Access denied. Administrator privileges are required to install services.");
                Console.WriteLine("   Please run this command from an elevated command prompt.");
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
                Console.WriteLine($"   Exception Type: {ex.GetType().Name}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"   Inner Exception: {ex.InnerException.Message}");
                }
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
                // Double-check admin privileges before proceeding
                if (!UacHelper.IsRunningAsAdministrator())
                {
                    Console.WriteLine("Error: Administrator privileges are required to uninstall services.");
                    Console.WriteLine("   Please run this command from an elevated command prompt or allow UAC elevation.");
                    Environment.Exit(1);
                }

                var serviceManager = CreateServiceManager();
                
                Console.WriteLine($"Uninstalling service: {serviceName}");
                await serviceManager.UninstallServiceAsync(serviceName);
                Console.WriteLine($"Service '{serviceName}' uninstalled successfully!");
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Error: Access denied. Administrator privileges are required to uninstall services.");
                Console.WriteLine("   Please run this command from an elevated command prompt.");
                Environment.Exit(1);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5) // ERROR_ACCESS_DENIED
            {
                Console.WriteLine("Error: Access denied. Administrator privileges are required to uninstall services.");
                Console.WriteLine("   Please run this command from an elevated command prompt.");
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
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
                // Double-check admin privileges before proceeding
                if (!UacHelper.IsRunningAsAdministrator())
                {
                    Console.WriteLine("Error: Administrator privileges are required to start services.");
                    Console.WriteLine("   Please run this command from an elevated command prompt or allow UAC elevation.");
                    Environment.Exit(1);
                }

                var serviceManager = CreateServiceManager();
                
                Console.WriteLine($"Starting service: {serviceName}");
                await serviceManager.StartServiceAsync(serviceName);
                Console.WriteLine($"Service '{serviceName}' started successfully!");
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Error: Access denied. Administrator privileges are required to start services.");
                Console.WriteLine("   Please run this command from an elevated command prompt.");
                Environment.Exit(1);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5) // ERROR_ACCESS_DENIED
            {
                Console.WriteLine("Error: Access denied. Administrator privileges are required to start services.");
                Console.WriteLine("   Please run this command from an elevated command prompt.");
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
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
                // Double-check admin privileges before proceeding
                if (!UacHelper.IsRunningAsAdministrator())
                {
                    Console.WriteLine("Error: Administrator privileges are required to stop services.");
                    Console.WriteLine("   Please run this command from an elevated command prompt or allow UAC elevation.");
                    Environment.Exit(1);
                }

                var serviceManager = CreateServiceManager();
                
                Console.WriteLine($"Stopping service: {serviceName}");
                await serviceManager.StopServiceAsync(serviceName);
                Console.WriteLine($"Service '{serviceName}' stopped successfully!");
            }
            catch (UnauthorizedAccessException)
            {
                Console.WriteLine("Error: Access denied. Administrator privileges are required to stop services.");
                Console.WriteLine("   Please run this command from an elevated command prompt.");
                Environment.Exit(1);
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 5) // ERROR_ACCESS_DENIED
            {
                Console.WriteLine("Error: Access denied. Administrator privileges are required to stop services.");
                Console.WriteLine("   Please run this command from an elevated command prompt.");
                Environment.Exit(1);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
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
                Console.WriteLine($"Error: {ex.Message}");
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
            Console.WriteLine($"Logs for service: {serviceName} (last {tailLines} lines)");
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

    /// <summary>
    /// Convert relative config file paths to absolute paths in command line arguments
    /// </summary>
    /// <param name="args">Original command line arguments</param>
    /// <returns>Arguments with absolute config paths</returns>
    private static string[] ConvertConfigPathsToAbsolute(string[] args)
    {
        var result = new List<string>();
        
        for (int i = 0; i < args.Length; i++)
        {
            result.Add(args[i]);
            
            // Check if this is a --config argument and we have a next argument
            if ((args[i] == "--config" || args[i] == "-c") && i + 1 < args.Length)
            {
                // Convert the config path to absolute
                var configPath = args[i + 1];
                var absolutePath = Path.GetFullPath(configPath);
                result.Add(absolutePath);
                i++; // Skip the next argument since we've processed it
            }
        }
        
        return result.ToArray();
    }

    /// <summary>
    /// Find the project root directory by looking for the .sln file
    /// </summary>
    /// <returns>Project root path or null if not found</returns>
    private static string? FindProjectRoot()
    {
        var currentDir = Environment.CurrentDirectory;
        var processDir = Path.GetDirectoryName(Environment.ProcessPath);
        
        // Try current directory first
        var dir = new DirectoryInfo(currentDir);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Any())
                return dir.FullName;
            dir = dir.Parent;
        }
        
        // Try process directory
        if (!string.IsNullOrEmpty(processDir))
        {
            dir = new DirectoryInfo(processDir);
            while (dir != null)
            {
                if (dir.GetFiles("*.sln").Any())
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        
        return null;
    }
} 