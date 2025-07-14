using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinLet.Core;

namespace WinLet.Service;

public class WinLetWorkerService : BackgroundService
{
    private readonly ILogger<WinLetWorkerService> _logger;
    private ProcessRunner? _processRunner;

    public WinLetWorkerService(ILogger<WinLetWorkerService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WinLet Worker Service starting");

        try
        {
            // Load configuration (this would be passed in via service installation)
            // For now, we'll implement a way to get the config path from service parameters
            var configPath = GetConfigurationPath();
            
            if (string.IsNullOrEmpty(configPath))
            {
                _logger.LogError("No configuration path specified");
                return;
            }

            var config = ConfigLoader.LoadFromFile(configPath);
            
            _logger.LogInformation("Starting managed process: {ProcessName}", config.Process.Executable);
            
            // Create and start the process runner
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _processRunner = new ProcessRunner(config, loggerFactory.CreateLogger<ProcessRunner>());
            await _processRunner.StartAsync(stoppingToken);

            // Keep the service running while the process is active
            while (!stoppingToken.IsCancellationRequested && _processRunner?.IsRunning == true)
            {
                await Task.Delay(5000, stoppingToken); // Check every 5 seconds
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("WinLet Worker Service stopping due to cancellation");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "WinLet Worker Service encountered an error");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("WinLet Worker Service stopping");
        
        if (_processRunner?.IsRunning == true)
        {
            await _processRunner.StopAsync();
        }
        
        await base.StopAsync(cancellationToken);
    }

    private string? GetConfigurationPath()
    {
        // This could be implemented by reading from:
        // 1. Service parameters
        // 2. Registry
        // 3. Environment variables
        // 4. Command line arguments
        
        // For now, return a placeholder - this would be set during service installation
        return Environment.GetEnvironmentVariable("WINLET_CONFIG_PATH");
    }
} 