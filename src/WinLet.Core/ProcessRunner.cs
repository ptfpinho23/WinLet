using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace WinLet.Core;

/// <summary>
/// Manages execution and monitoring of a child process with restart capabilities
/// </summary>
public class ProcessRunner : IDisposable
{
    private readonly ServiceConfig _config;
    private readonly ILogger<ProcessRunner> _logger;
    private readonly LogManager _logManager;
    private Process? _process;
    private readonly CancellationTokenSource _cancellationTokenSource = new();
    private Task? _monitoringTask;
    private int _restartAttempts = 0;
    private DateTime _lastRestartTime = DateTime.MinValue;
    private bool _disposed = false;

    public event EventHandler<ProcessEventArgs>? ProcessStarted;
    public event EventHandler<ProcessEventArgs>? ProcessStopped;
    public event EventHandler<ProcessEventArgs>? ProcessCrashed;

    public bool IsRunning => _process?.HasExited == false;
    public int? ProcessId => _process?.Id;
    public DateTime? StartTime { get; private set; }

    public ProcessRunner(ServiceConfig config, ILogger<ProcessRunner> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Create logger factory for LogManager
        var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
        var logManagerLogger = loggerFactory.CreateLogger<LogManager>();
        
        _logManager = new LogManager(config.Logging, config.Name, logManagerLogger);
    }

    /// <summary>
    /// Start the configured process
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting process: {Executable} {Arguments}", 
            _config.Process.Executable, _config.Process.Arguments);

        try
        {
            var processStartInfo = CreateProcessStartInfo();
            _process = new Process { StartInfo = processStartInfo };
            
            // Enable events
            _process.EnableRaisingEvents = true;
            _process.Exited += OnProcessExited;

            if (!_process.Start())
            {
                throw new InvalidOperationException("Failed to start process");
            }

            // Start reading stdout and stderr asynchronously
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            
            // Hook up event handlers for output capture
            _process.OutputDataReceived += OnOutputDataReceived;
            _process.ErrorDataReceived += OnErrorDataReceived;

            StartTime = DateTime.UtcNow;
            _logger.LogInformation("Process started with PID: {ProcessId}", _process.Id);
            
            // Log to WinLet service log (using regular ILogger)
            _logger.LogInformation("🚀 Process started: {Executable} (PID: {ProcessId})", _config.Process.Executable, _process.Id);
            
            ProcessStarted?.Invoke(this, new ProcessEventArgs(_process.Id, StartTime.Value));

            // Start monitoring task
            _monitoringTask = MonitorProcessAsync(_cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start process: {Executable}", _config.Process.Executable);
            throw;
        }
    }

    /// <summary>
    /// Stop the process gracefully
    /// </summary>
    /// <param name="cancellationToken">Cancellation token for the operation</param>
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_process == null || _process.HasExited)
        {
            _logger.LogInformation("Process is not running, nothing to stop");
            return;
        }

        _logger.LogInformation("Stopping process with PID: {ProcessId}", _process.Id);
        
        // Log to WinLet service log (using regular ILogger)
        _logger.LogInformation("🛑 Stopping process: {Executable} (PID: {ProcessId})", _config.Process.Executable, _process.Id);

        try
        {
            // Try graceful shutdown first
            if (!_process.CloseMainWindow())
            {
                _logger.LogWarning("Failed to send close message to process, trying CTRL+C");
                
                // TODO: Implement CTRL+C signal for console applications
                // For now, wait a bit then force kill
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
            }

            // Wait for graceful shutdown
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(_config.Process.ShutdownTimeoutSeconds), cancellationToken);
            var processTask = Task.Run(() => _process.WaitForExit(), cancellationToken);

            var completedTask = await Task.WhenAny(timeoutTask, processTask);

            if (completedTask == timeoutTask)
            {
                _logger.LogWarning("Process did not exit within timeout, force killing");
                _process.Kill(entireProcessTree: true);
                await processTask; // Wait for the kill to complete
            }

            _logger.LogInformation("Process stopped");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error stopping process");
            throw;
        }
        finally
        {
            _cancellationTokenSource.Cancel();
        }
    }

    private ProcessStartInfo CreateProcessStartInfo()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = _config.Process.Executable,
            Arguments = _config.Process.Arguments ?? string.Empty,
            WorkingDirectory = _config.Process.WorkingDirectory ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        // Add environment variables
        foreach (var envVar in _config.Process.Environment)
        {
            startInfo.Environment[envVar.Key] = envVar.Value;
        }

        return startInfo;
    }

    private async Task MonitorProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && _process != null)
            {
                if (_process.HasExited)
                {
                    var exitCode = _process.ExitCode;
                    var exitTime = DateTime.UtcNow;
                    
                    _logger.LogWarning("Process exited with code: {ExitCode}", exitCode);
                    
                    // Log to WinLet service log (using regular ILogger)
                    _logger.LogWarning("❌ Process exited: {Executable} (PID: {ProcessId}, Exit Code: {ExitCode})", _config.Process.Executable, _process.Id, exitCode);
                    
                    ProcessStopped?.Invoke(this, new ProcessEventArgs(_process.Id, exitTime, exitCode));

                    if (exitCode != 0)
                    {
                        ProcessCrashed?.Invoke(this, new ProcessEventArgs(_process.Id, exitTime, exitCode));
                        
                        if (ShouldRestart())
                        {
                            _logger.LogInformation("🔄 Scheduling restart for: {Executable}", _config.Process.Executable);
                            await HandleRestartAsync(cancellationToken);
                        }
                        else
                        {
                            _logger.LogError("⛔ Maximum restart attempts reached for: {Executable}", _config.Process.Executable);
                        }
                    }
                    break;
                }

                // TODO: Implement health checks here if configured
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancellation is requested
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in process monitoring");
        }
    }

    private bool ShouldRestart()
    {
        if (_config.Restart.Policy == RestartPolicy.Never)
            return false;

        if (_config.Restart.Policy == RestartPolicy.Always)
            return true;

        // OnFailure policy - check restart window and attempts
        var now = DateTime.UtcNow;
        if (now - _lastRestartTime > TimeSpan.FromSeconds(_config.Restart.WindowSeconds))
        {
            // Reset counter if outside the window
            _restartAttempts = 0;
        }

        return _restartAttempts < _config.Restart.MaxAttempts;
    }

    private async Task HandleRestartAsync(CancellationToken cancellationToken)
    {
        _restartAttempts++;
        _lastRestartTime = DateTime.UtcNow;

        _logger.LogInformation("Restarting process (attempt {Attempt}/{MaxAttempts})", 
            _restartAttempts, _config.Restart.MaxAttempts);

        // Log to WinLet service log (using regular ILogger)
        _logger.LogInformation("🔄 Restarting process: {Executable} (attempt {Attempt}/{MaxAttempts})", _config.Process.Executable, _restartAttempts, _config.Restart.MaxAttempts);

        await Task.Delay(TimeSpan.FromSeconds(_config.Restart.DelaySeconds), cancellationToken);

        try
        {
            await StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart process");
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        // This event handler runs on a background thread
        // The monitoring task will handle the restart logic
    }

    private async void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            // Write application stdout to application log file with timestamp
            var timestampedOutput = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Data}\n";
            await _logManager.WriteStdoutAsync(timestampedOutput);
            
            // Also log to WinLet service log for debugging (but less verbose)
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("App stdout: {Output}", e.Data);
            }
        }
    }

    private async void OnErrorDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Data))
        {
            // Write application stderr to application log file with timestamp
            var timestampedError = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] {e.Data}\n";
            await _logManager.WriteStderrAsync(timestampedError);
            
            // Also log to WinLet service log (always log errors)
            _logger.LogWarning("App stderr: {Error}", e.Data);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _cancellationTokenSource.Cancel();
        
        try
        {
            _monitoringTask?.Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for monitoring task to complete");
        }

        _process?.Dispose();
        _cancellationTokenSource.Dispose();
        
        _disposed = true;
    }
}

/// <summary>
/// Event arguments for process lifecycle events
/// </summary>
public class ProcessEventArgs : EventArgs
{
    public int ProcessId { get; }
    public DateTime Timestamp { get; }
    public int? ExitCode { get; }

    public ProcessEventArgs(int processId, DateTime timestamp, int? exitCode = null)
    {
        ProcessId = processId;
        Timestamp = timestamp;
        ExitCode = exitCode;
    }
} 