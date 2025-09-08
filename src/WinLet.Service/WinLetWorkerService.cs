using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using WinLet.Core;

namespace WinLet.Service;

public class WinLetWorkerService : BackgroundService
{
    private readonly ILogger<WinLetWorkerService> _logger;
    private readonly ConfigPathService _configPathService;
    private readonly IHostApplicationLifetime _hostLifetime;
    private ProcessRunner? _processRunner;

    public WinLetWorkerService(ILogger<WinLetWorkerService> logger, ConfigPathService configPathService, IHostApplicationLifetime hostLifetime)
    {
        _logger = logger;
        _configPathService = configPathService;
        _hostLifetime = hostLifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("WinLet Worker Service starting");

        try
        {
            // Get configuration path from injected service
            var configPath = _configPathService.ConfigPath;
            
            if (string.IsNullOrEmpty(configPath))
            {
                _logger.LogError("No configuration path specified. Service cannot start without a config file.");
                return;
            }

            _logger.LogInformation("Using configuration file: {ConfigPath}", configPath);

            if (!File.Exists(configPath))
            {
                _logger.LogError("Configuration file not found: {ConfigPath}", configPath);
                return;
            }

            _logger.LogInformation("Loading configuration...");
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
            
            _logger.LogInformation("Configuration loaded successfully");
            newLogger.LogInformation("WinLet service logs will be written to: {LogFile}", winletLogFile);
            
            // Use the new logger for subsequent log messages
            newLogger.LogInformation("Service: {ServiceName} ({DisplayName})", config.Name, config.DisplayName);
            newLogger.LogInformation("Starting managed process: {ProcessName}", config.Process.Executable);
            newLogger.LogInformation("Working directory: {WorkingDirectory}", config.Process.WorkingDirectory);
            newLogger.LogInformation("Arguments: {Arguments}", config.Process.Arguments ?? "(none)");
            newLogger.LogInformation("Restart policy: {RestartPolicy} (max {MaxAttempts} attempts)", config.Restart.Policy, config.Restart.MaxAttempts);
            
            // Create process runner with the same logger factory that includes file logging
            _processRunner = new ProcessRunner(config, loggerFactory.CreateLogger<ProcessRunner>(), loggerFactory);
            
            // Implement service-level restart logic
            int startupAttempts = 0;
            int maxAttempts = config.Restart.Policy == RestartPolicy.Never ? 1 : config.Restart.MaxAttempts;
            bool infiniteRestart = config.Restart.Policy == RestartPolicy.Always;
            
            while (!stoppingToken.IsCancellationRequested && (infiniteRestart || startupAttempts < maxAttempts))
            {
                startupAttempts++;
                newLogger.LogInformation("Starting managed process (attempt {Attempt}/{MaxAttempts})...", startupAttempts, maxAttempts);
                
                try
                {
                    await _processRunner!.StartAsync(stoppingToken);
                    newLogger.LogInformation("Monitoring managed process...");
                    
                    // Keep the service running while the process is active
                    while (!stoppingToken.IsCancellationRequested && _processRunner?.IsRunning == true)
                    {
                        await Task.Delay(5000, stoppingToken); // Check every 5 seconds
                    }
                    
                    if (_processRunner?.IsRunning != true)
                    {
                        newLogger.LogWarning("Managed process has stopped running");
                        
                        // If the restart policy is Never, break out of the loop
                        if (config.Restart.Policy == RestartPolicy.Never)
                        {
                            newLogger.LogInformation("Restart policy is Never. Service will stop.");
                            _hostLifetime.StopApplication();
                            break;
                        }
                        
                        // If this wasn't the last attempt (or infinite restart), wait before retrying
                        if (infiniteRestart || startupAttempts < maxAttempts)
                        {
                            newLogger.LogInformation("Waiting {DelaySeconds} seconds before restart attempt...", config.Restart.DelaySeconds);
                            await Task.Delay(TimeSpan.FromSeconds(config.Restart.DelaySeconds), stoppingToken);
                        }
                    }
                    else
                    {
                        newLogger.LogInformation("Service shutdown requested");
                        break;
                    }
                }
                catch (Exception ex)
                {
                    newLogger.LogError(ex, "Failed to start managed process (attempt {Attempt}/{MaxAttempts})", startupAttempts, maxAttempts);
                    
                    // Log the error to a separate startup error file to avoid file locking issues
                    var errorMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] Attempt {startupAttempts}/{maxAttempts} failed: {ex.Message}\n";
                    try
                    {
                        var errorLogDir = !string.IsNullOrEmpty(config.Logging.LogPath) 
                            ? config.Logging.LogPath 
                            : Path.Combine(Path.GetDirectoryName(configPath) ?? Environment.CurrentDirectory, "logs");
                        
                        Directory.CreateDirectory(errorLogDir);
                        var startupErrorFile = Path.Combine(errorLogDir, $"{config.Name}.startup-error.log");
                        await File.AppendAllTextAsync(startupErrorFile, errorMessage);
                    }
                    catch (Exception logEx)
                    {
                        newLogger.LogWarning(logEx, "Failed to write startup error to log file");
                    }
                    
                    // If restart policy is Never, break immediately
                    if (config.Restart.Policy == RestartPolicy.Never)
                    {
                        newLogger.LogCritical("Restart policy is Never. Service will stop after startup failure.");
                        _hostLifetime.StopApplication();
                        break;
                    }
                    
                    // If this wasn't the last attempt (or infinite restart), wait before retrying
                    if (infiniteRestart || startupAttempts < maxAttempts)
                    {
                        newLogger.LogInformation("Waiting {DelaySeconds} seconds before restart attempt...", config.Restart.DelaySeconds);
                        await Task.Delay(TimeSpan.FromSeconds(config.Restart.DelaySeconds), stoppingToken);
                    }
                    else
                    {
                        newLogger.LogCritical("Maximum startup attempts ({MaxAttempts}) reached. Service will stop.", maxAttempts);
                    }
                }
            }
            
            if (stoppingToken.IsCancellationRequested)
            {
                newLogger.LogInformation("Service stop requested during startup attempts");
            }
            else if (startupAttempts >= maxAttempts && !infiniteRestart)
            {
                newLogger.LogCritical("All startup attempts exhausted. Stopping the service.");
                _hostLifetime.StopApplication();
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
        _logger.LogInformation("WinLet Worker Service stopping...");
        
        if (_processRunner?.IsRunning == true)
        {
            _logger.LogInformation("Stopping managed process...");
            try
            {
                await _processRunner.StopAsync(cancellationToken);
                _logger.LogInformation("Managed process stopped successfully");
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Process stop was cancelled - this is normal during service shutdown");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error stopping managed process");
            }
        }
        else
        {
            _logger.LogInformation("No managed process to stop");
        }
        
        _logger.LogInformation("WinLet Worker Service stopped");
        
        try
        {
            await base.StopAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Base service stop was cancelled");
        }
    }
} 