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
    private readonly CrashDumpManager? _crashDumpManager;
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
        
        // Create a simple logger for LogManager - we'll use a basic console logger
        var logManagerLogger = new ConsoleLogger<LogManager>();
        _logManager = new LogManager(config.Logging, config.Name, logManagerLogger);
        
        // Create crash dump manager if crash dumps are enabled
        if (config.CrashDump?.Enabled == true)
        {
            var crashDumpLogger = new ConsoleLogger<CrashDumpManager>();
            _crashDumpManager = new CrashDumpManager(config.CrashDump, config.Name, crashDumpLogger);
        }
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

            _logger.LogDebug("Attempting to start process: {Executable} with args: {Arguments} in directory: {WorkingDirectory}", 
                processStartInfo.FileName, processStartInfo.Arguments, processStartInfo.WorkingDirectory);

            if (!_process.Start())
            {
                var error = $"Failed to start process: {_config.Process.Executable}";
                _logger.LogError(error);
                
                // Try to write to log manager, but don't fail if it doesn't work
                try
                {
                    await _logManager.WriteStderrAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] {error}\n");
                }
                catch (Exception logEx)
                {
                    _logger.LogWarning(logEx, "Failed to write process start failure to log file via LogManager");
                }
                
                throw new InvalidOperationException(error);
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
            _logger.LogInformation("Process started: {Executable} (PID: {ProcessId})", _config.Process.Executable, _process.Id);
            
            ProcessStarted?.Invoke(this, new ProcessEventArgs(_process.Id, StartTime.Value));

            // Start monitoring task
            _monitoringTask = MonitorProcessAsync(_cancellationTokenSource.Token);
        }
        catch (System.ComponentModel.Win32Exception win32Ex)
        {
            var error = $"Win32 error starting process '{_config.Process.Executable}': {win32Ex.Message} (Error Code: {win32Ex.NativeErrorCode})";
            _logger.LogError(win32Ex, error);
            
            // Try to write to log manager, but don't fail if it doesn't work
            try
            {
                await _logManager.WriteStderrAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] {error}\n");
            }
            catch (Exception logEx)
            {
                _logger.LogWarning(logEx, "Failed to write Win32 error to log file via LogManager");
            }
            
            throw new InvalidOperationException(error, win32Ex);
        }
        catch (Exception ex)
        {
            var error = $"Failed to start process: {_config.Process.Executable}. Error: {ex.Message}";
            _logger.LogError(ex, error);
            
            // Try to write to log manager, but don't fail if it doesn't work
            try
            {
                await _logManager.WriteStderrAsync($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] {error}\n");
            }
            catch (Exception logEx)
            {
                _logger.LogWarning(logEx, "Failed to write error to log file via LogManager");
            }
            
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
        _logger.LogInformation("Stopping process: {Executable} (PID: {ProcessId})", _config.Process.Executable, _process.Id);

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

        // Validate working directory exists
        if (!Directory.Exists(startInfo.WorkingDirectory))
        {
            var error = $"Working directory not found: {startInfo.WorkingDirectory}";
            _logger.LogError(error);
            throw new InvalidOperationException(error);
        }

        // Validate executable exists (for non-shell commands)
        if (!Path.IsPathRooted(_config.Process.Executable))
        {
            // Check if executable is in PATH
            var executablePath = FindExecutableInPath(_config.Process.Executable);
            if (string.IsNullOrEmpty(executablePath))
            {
                var error = $"Executable '{_config.Process.Executable}' not found in PATH or working directory";
                _logger.LogError(error);
                throw new InvalidOperationException(error);
            }
            startInfo.FileName = executablePath;
            _logger.LogDebug("Found executable in PATH: {ExecutablePath}", executablePath);
        }
        else if (!File.Exists(_config.Process.Executable))
        {
            var error = $"Executable file not found: {_config.Process.Executable}";
            _logger.LogError(error);
            throw new InvalidOperationException(error);
        }

        return startInfo;
    }

    /// <summary>
    /// Find executable in PATH
    /// </summary>
    private string? FindExecutableInPath(string executable)
    {
        try
        {
            // Check if it's already a full path
            if (Path.IsPathRooted(executable))
            {
                return File.Exists(executable) ? executable : null;
            }

            // Get PATH environment variable
            var path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            // Check each PATH directory
            var pathDirectories = path.Split(Path.PathSeparator);
            foreach (var directory in pathDirectories)
            {
                if (string.IsNullOrEmpty(directory)) continue;

                var fullPath = Path.Combine(directory, executable);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }

                // Also check with .exe extension on Windows
                if (OperatingSystem.IsWindows())
                {
                    var exePath = Path.Combine(directory, $"{executable}.exe");
                    if (File.Exists(exePath))
                    {
                        return exePath;
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error searching for executable in PATH: {Executable}", executable);
            return null;
        }
    }

    private async Task MonitorProcessAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Starting process monitoring for: {Executable}", _config.Process.Executable);
            
            while (!cancellationToken.IsCancellationRequested && _process != null)
            {
                if (_process.HasExited)
                {
                    var exitCode = _process.ExitCode;
                    var exitTime = DateTime.UtcNow;
                    
                    _logger.LogWarning("Process exited with code: {ExitCode}", exitCode);
                    
                    // Log to WinLet service log (using regular ILogger)
                    _logger.LogWarning("Process exited: {Executable} (PID: {ProcessId}, Exit Code: {ExitCode})", _config.Process.Executable, _process.Id, exitCode);
                    
                    // Also log to application error log
                    var errorMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] Process exited with code {exitCode}: {_config.Process.Executable}\n";
                    await _logManager.WriteStderrAsync(errorMessage);
                    
                    ProcessStopped?.Invoke(this, new ProcessEventArgs(_process.Id, exitTime, exitCode));

                    if (exitCode != 0)
                    {
                        ProcessCrashed?.Invoke(this, new ProcessEventArgs(_process.Id, exitTime, exitCode));
                        
                        // Generate crash dump if enabled
                        if (_crashDumpManager != null && _config.CrashDump?.Enabled == true)
                        {
                            try
                            {
                                _logger.LogInformation("Generating crash dump for crashed process: {Executable} (PID: {ProcessId}, Exit Code: {ExitCode})", 
                                    _config.Process.Executable, _process.Id, exitCode);
                                
                                // Try to generate crash dump using the process handle (may not work if process has fully exited)
                                var dumpFile = await _crashDumpManager.GenerateCrashDumpAsync(_process.Id, $"ExitCode{exitCode}");
                                
                                if (!string.IsNullOrEmpty(dumpFile))
                                {
                                    var crashDumpMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] Crash dump generated: {Path.GetFileName(dumpFile)}\n";
                                    await _logManager.WriteStdoutAsync(crashDumpMessage);
                                    _logger.LogInformation("Crash dump saved: {DumpFile}", dumpFile);
                                }
                                else
                                {
                                    var noDumpMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [WARNING] Failed to generate crash dump for process {_process.Id}\n";
                                    await _logManager.WriteStderrAsync(noDumpMessage);
                                    _logger.LogWarning("Failed to generate crash dump for process {ProcessId}", _process.Id);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Error generating crash dump for process {ProcessId}", _process.Id);
                                var crashDumpErrorMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] Crash dump generation error: {ex.Message}\n";
                                await _logManager.WriteStderrAsync(crashDumpErrorMessage);
                            }
                        }
                        
                        if (ShouldRestart())
                        {
                            _logger.LogInformation("Scheduling restart for: {Executable}", _config.Process.Executable);
                            await HandleRestartAsync(cancellationToken);
                        }
                        else
                        {
                            _logger.LogError("Maximum restart attempts reached for: {Executable}", _config.Process.Executable);
                            // Log this to the application error log as well
                            var maxAttemptsMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] Maximum restart attempts ({_config.Restart.MaxAttempts}) reached for {_config.Process.Executable}\n";
                            await _logManager.WriteStderrAsync(maxAttemptsMessage);
                        }
                    }
                    break;
                }

                // TODO: Implement health checks here if configured
                await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            }
            
            _logger.LogDebug("Process monitoring ended for: {Executable}", _config.Process.Executable);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("Process monitoring cancelled for: {Executable}", _config.Process.Executable);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in process monitoring for: {Executable}", _config.Process.Executable);
            var monitoringErrorMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] Process monitoring error: {ex.Message}\n";
            await _logManager.WriteStderrAsync(monitoringErrorMessage);
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
        _logger.LogInformation("Restarting process: {Executable} (attempt {Attempt}/{MaxAttempts})", _config.Process.Executable, _restartAttempts, _config.Restart.MaxAttempts);

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
            if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
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
            
            // If this looks like a startup error, log it more prominently
            if (e.Data.Contains("not found") || e.Data.Contains("not recognized") || e.Data.Contains("command not found"))
            {
                _logger.LogError("Startup error detected: {Error}", e.Data);
            }
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
        _crashDumpManager?.Dispose();
        _logManager.Dispose();
        _cancellationTokenSource.Dispose();
        
        _disposed = true;
    }
}

/// <summary>
/// Simple console logger implementation for LogManager
/// </summary>
internal class ConsoleLogger<T> : ILogger<T>
{
    public IDisposable BeginScope<TState>(TState state) => null!;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [{logLevel}] [LogManager] {message}");
        if (exception != null)
        {
            Console.WriteLine(exception.ToString());
        }
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