using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinLet.Core;

namespace WinLet.Service;

public class WinLetWorkerService : BackgroundService
{
    private readonly ILogger<WinLetWorkerService> _logger;
    private readonly ConfigPathService _configPathService;
    private ProcessRunner? _processRunner;

    public WinLetWorkerService(ILogger<WinLetWorkerService> logger, ConfigPathService configPathService)
    {
        _logger = logger;
        _configPathService = configPathService;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("🚀 WinLet Worker Service starting");

        try
        {
            // Get configuration path from injected service
            var configPath = _configPathService.ConfigPath;
            
            if (string.IsNullOrEmpty(configPath))
            {
                _logger.LogError("❌ No configuration path specified. Service cannot start without a config file.");
                return;
            }

            _logger.LogInformation("📁 Using configuration file: {ConfigPath}", configPath);

            if (!File.Exists(configPath))
            {
                _logger.LogError("❌ Configuration file not found: {ConfigPath}", configPath);
                return;
            }

            _logger.LogInformation("📖 Loading configuration...");
            var config = ConfigLoader.LoadFromFile(configPath);
            
            // Now that we have the config, set up file logging in the same directory as application logs
            var logDir = !string.IsNullOrEmpty(config.Logging.LogPath) 
                ? config.Logging.LogPath 
                : Path.Combine(Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory, "logs");
            
            Directory.CreateDirectory(logDir);
            var winletLogFile = Path.Combine(logDir, "winlet.log");
            
            // Set up WinLet service file logging
            var loggerFactory = LoggerFactory.Create(builder => 
            {
                builder.AddConsole();
                builder.AddProvider(new FileLoggerProvider(winletLogFile));
            });
            
            // Replace the current logger with one that includes file logging
            var newLogger = loggerFactory.CreateLogger<WinLetWorkerService>();
            
            _logger.LogInformation("✅ Configuration loaded successfully");
            newLogger.LogInformation("📁 WinLet service logs will be written to: {LogFile}", winletLogFile);
            
            // Use the new logger for subsequent log messages
            newLogger.LogInformation("🔧 Service: {ServiceName} ({DisplayName})", config.Name, config.DisplayName);
            newLogger.LogInformation("🎯 Starting managed process: {ProcessName}", config.Process.Executable);
            newLogger.LogInformation("📂 Working directory: {WorkingDirectory}", config.Process.WorkingDirectory);
            newLogger.LogInformation("⚙️  Arguments: {Arguments}", config.Process.Arguments ?? "(none)");
            newLogger.LogInformation("🔄 Restart policy: {RestartPolicy} (max {MaxAttempts} attempts)", config.Restart.Policy, config.Restart.MaxAttempts);
            
            // Create and start the process runner
            var processLoggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _processRunner = new ProcessRunner(config, processLoggerFactory.CreateLogger<ProcessRunner>());
            
            newLogger.LogInformation("🏁 Starting managed process...");
            await _processRunner.StartAsync(stoppingToken);

            newLogger.LogInformation("👀 Monitoring managed process...");
            
            // Keep the service running while the process is active
            while (!stoppingToken.IsCancellationRequested && _processRunner?.IsRunning == true)
            {
                await Task.Delay(5000, stoppingToken); // Check every 5 seconds
            }
            
            if (_processRunner?.IsRunning != true)
            {
                newLogger.LogWarning("⚠️  Managed process has stopped running");
            }
            else
            {
                newLogger.LogInformation("🔄 Service shutdown requested");
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("🛑 WinLet Worker Service stopping due to cancellation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "💥 WinLet Worker Service encountered an error");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("🛑 WinLet Worker Service stopping...");
        
        if (_processRunner?.IsRunning == true)
        {
            _logger.LogInformation("📤 Stopping managed process...");
            try
            {
                await _processRunner.StopAsync();
                _logger.LogInformation("✅ Managed process stopped successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "❌ Error stopping managed process");
            }
        }
        else
        {
            _logger.LogInformation("ℹ️  No managed process to stop");
        }
        
        _logger.LogInformation("🏁 WinLet Worker Service stopped");
        await base.StopAsync(cancellationToken);
    }
} 