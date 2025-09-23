using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace WinLet.Core;

/// <summary>
/// Manages execution and monitoring of a child process with restart capabilities
/// </summary>
public class ProcessRunner : IDisposable
{
    // Windows API declarations for sending CTRL+C to console processes
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? HandlerRoutine, bool Add);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GenerateConsoleCtrlEvent(uint dwCtrlEvent, uint dwProcessGroupId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private delegate bool ConsoleCtrlDelegate(uint CtrlType);

    private const uint CTRL_C_EVENT = 0;
    private const uint CTRL_BREAK_EVENT = 1;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const IntPtr INVALID_HANDLE_VALUE = (IntPtr)(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }
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

    public ProcessRunner(ServiceConfig config, ILogger<ProcessRunner> logger, ILoggerFactory? loggerFactory = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Create LogManager logger - use provided factory or fall back to console
        var logManagerLogger = loggerFactory?.CreateLogger<LogManager>() ?? new ConsoleLogger<LogManager>();
        _logManager = new LogManager(config.Logging, config.Name, logManagerLogger);
        
        // Create crash dump manager if crash dumps are enabled
        if (config.CrashDump?.Enabled == true)
        {
            var crashDumpLogger = loggerFactory?.CreateLogger<CrashDumpManager>() ?? new ConsoleLogger<CrashDumpManager>();
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
            // Try graceful shutdown first - attempt GUI close
            bool sentGracefulSignal = false;
            
            if (_process.CloseMainWindow())
            {
                _logger.LogInformation("Sent close message to GUI process");
                sentGracefulSignal = true;
            }
            else
            {
                _logger.LogDebug("CloseMainWindow failed (likely console app), trying CTRL+C to process tree");
                
                // Try CTRL+C for console applications - send to entire process tree
                var signalCount = await SendCtrlCToProcessTree(_process.Id, cancellationToken);
                if (signalCount > 0)
                {
                    _logger.LogInformation("Sent CTRL+C signal to {SignalCount} processes in tree", signalCount);
                    sentGracefulSignal = true;
                }
                else
                {
                    _logger.LogWarning("Failed to send CTRL+C signal to any processes, will force kill after timeout");
                }
            }
            
            // Give processes time to handle the graceful shutdown signal
            if (sentGracefulSignal)
            {
                _logger.LogInformation("Waiting 2 seconds for processes to begin graceful shutdown...");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
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
        var workDir = _config.Process.WorkingDirectory ?? Environment.CurrentDirectory;
        
        // Handle executable path correctly - don't combine with working directory if it's already absolute
        var executable = _config.Process.Executable;
        var fileName = Path.IsPathRooted(executable) ? executable : Path.Combine(workDir, executable);
        
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = _config.Process.Arguments ?? string.Empty,
            WorkingDirectory = workDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true
        };

        // Inherit system environment variables first (including PATH)
        foreach (DictionaryEntry envVar in Environment.GetEnvironmentVariables())
        {
            if (envVar.Key is string key && envVar.Value is string value)
            {
                startInfo.Environment[key] = value;
            }
        }

        // Add the executable's directory to PATH so it can find related binaries
        var executableDir = Path.GetDirectoryName(fileName);
        if (!string.IsNullOrEmpty(executableDir))
        {
            var currentPath = startInfo.Environment.TryGetValue("PATH", out var pathValue) ? pathValue : string.Empty;
            
            // Check if executable directory is already in PATH
            var pathDirectories = currentPath?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
            var executableDirNormalized = Path.GetFullPath(executableDir).TrimEnd(Path.DirectorySeparatorChar);
            
            var alreadyInPath = pathDirectories.Any(dir => 
                string.Equals(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar), 
                             executableDirNormalized, 
                             StringComparison.OrdinalIgnoreCase));
            
            if (!alreadyInPath)
            {
                // Add executable directory to the beginning of PATH
                var newPath = string.IsNullOrEmpty(currentPath) 
                    ? executableDir 
                    : $"{executableDir}{Path.PathSeparator}{currentPath}";
                
                startInfo.Environment["PATH"] = newPath;
                _logger.LogDebug("Added executable directory to PATH: {ExecutableDir}", executableDir);
            }
        }

        // Then add/override with custom environment variables from config
        foreach (var envVar in _config.Process.Environment)
        {
            startInfo.Environment[envVar.Key] = envVar.Value;
        }

        // Ensure working directory exists, create if it doesn't
        if (!Directory.Exists(startInfo.WorkingDirectory))
        {
            _logger.LogInformation("Working directory does not exist, creating: {WorkingDirectory}", startInfo.WorkingDirectory);
            try
            {
                Directory.CreateDirectory(startInfo.WorkingDirectory);
                _logger.LogInformation("Successfully created working directory: {WorkingDirectory}", startInfo.WorkingDirectory);
            }
            catch (Exception ex)
            {
                var error = $"Failed to create working directory: {startInfo.WorkingDirectory}";
                _logger.LogError(ex, error);
                throw new InvalidOperationException(error, ex);
            }
        }
        
        return startInfo;
    }

    /// <summary>
    /// Send CTRL+C signal to a console process
    /// </summary>
    /// <param name="processId">Process ID to send signal to</param>
    /// <returns>True if signal was sent successfully</returns>
    private bool SendCtrlCToProcess(int processId)
    {
        try
        {
            _logger.LogDebug("Attempting to send CTRL+C to process {ProcessId}", processId);
            
            // Attach to the target process console
            if (!AttachConsole((uint)processId))
            {
                var error = Marshal.GetLastWin32Error();
                _logger.LogWarning("Failed to attach to console of process {ProcessId}, error: {Error}", processId, error);
                return false;
            }

            // Disable CTRL+C handling for our own process to avoid affecting ourselves
            SetConsoleCtrlHandler(null, true);

            try
            {
                // Send CTRL+C event to the process group
                if (!GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0))
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger.LogWarning("Failed to generate CTRL+C event for process {ProcessId}, error: {Error}", processId, error);
                    return false;
                }

                _logger.LogInformation("Successfully sent CTRL+C signal to process {ProcessId}", processId);
                return true;
            }
            finally
            {
                // Re-enable CTRL+C handling for our own process
                SetConsoleCtrlHandler(null, false);
                
                // Detach from the target process console
                FreeConsole();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception while sending CTRL+C to process {ProcessId}", processId);
            return false;
        }
    }

    /// <summary>
    /// Get all child processes of a given process ID
    /// </summary>
    /// <param name="parentProcessId">Parent process ID</param>
    /// <returns>List of child process IDs</returns>
    private List<int> GetChildProcesses(int parentProcessId)
    {
        var childProcesses = new List<int>();
        
        try
        {
            var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == INVALID_HANDLE_VALUE)
            {
                _logger.LogWarning("Failed to create process snapshot");
                return childProcesses;
            }

            try
            {
                var processEntry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32)) };
                
                if (Process32First(snapshot, ref processEntry))
                {
                    do
                    {
                        if (processEntry.th32ParentProcessID == parentProcessId)
                        {
                            childProcesses.Add((int)processEntry.th32ProcessID);
                            _logger.LogDebug("Found child process: {ProcessId} ({ProcessName})", 
                                processEntry.th32ProcessID, processEntry.szExeFile);
                        }
                    }
                    while (Process32Next(snapshot, ref processEntry));
                }
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error discovering child processes for PID {ParentProcessId}", parentProcessId);
        }

        return childProcesses;
    }

    /// <summary>
    /// Get all processes in the process tree (including grandchildren, etc.)
    /// </summary>
    /// <param name="rootProcessId">Root process ID</param>
    /// <returns>List of all process IDs in the tree</returns>
    private List<int> GetProcessTree(int rootProcessId)
    {
        var allProcesses = new List<int> { rootProcessId };
        var processesToCheck = new Queue<int>();
        processesToCheck.Enqueue(rootProcessId);

        while (processesToCheck.Count > 0)
        {
            var currentProcessId = processesToCheck.Dequeue();
            var children = GetChildProcesses(currentProcessId);
            
            foreach (var childId in children)
            {
                if (!allProcesses.Contains(childId))
                {
                    allProcesses.Add(childId);
                    processesToCheck.Enqueue(childId);
                }
            }
        }

        _logger.LogInformation("Process tree for PID {RootProcessId} contains {ProcessCount} processes: [{ProcessIds}]", 
            rootProcessId, allProcesses.Count, string.Join(", ", allProcesses));
        
        return allProcesses;
    }

    /// <summary>
    /// Send CTRL+C signal to all processes in a process tree
    /// </summary>
    /// <param name="rootProcessId">Root process ID</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Number of processes that received the signal successfully</returns>
    private async Task<int> SendCtrlCToProcessTree(int rootProcessId, CancellationToken cancellationToken)
    {
        var processTree = GetProcessTree(rootProcessId);
        var successCount = 0;

        _logger.LogInformation("Sending CTRL+C signal to {ProcessCount} processes in tree", processTree.Count);

        // Send signals to all processes (starting with children, then parents)
        // This gives child processes a chance to clean up before their parents shut down
        var reversedTree = processTree.AsEnumerable().Reverse().ToList();
        
        foreach (var processId in reversedTree)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            try
            {
                // Check if process is still running
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    if (SendCtrlCToProcess(processId))
                    {
                        successCount++;
                    }
                }
                else
                {
                    _logger.LogDebug("Process {ProcessId} already exited, skipping", processId);
                }
            }
            catch (ArgumentException)
            {
                // Process doesn't exist anymore
                _logger.LogDebug("Process {ProcessId} no longer exists, skipping", processId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error accessing process {ProcessId}", processId);
            }

            // Small delay between signals to avoid overwhelming the system
            await Task.Delay(100, cancellationToken);
        }

        _logger.LogInformation("Successfully sent CTRL+C signal to {SuccessCount}/{TotalCount} processes", 
            successCount, processTree.Count);
        
        return successCount;
    }

    /// <summary>
    /// Force kill all processes in the process tree
    /// </summary>
    /// <param name="rootProcessId">Root process ID</param>
    /// <returns>Number of processes that were killed</returns>
    private int ForceKillProcessTree(int rootProcessId)
    {
        var processTree = GetProcessTree(rootProcessId);
        var killedCount = 0;

        _logger.LogWarning("Force killing {ProcessCount} processes in tree", processTree.Count);

        // Kill processes in reverse order (children first, then parents)
        var reversedTree = processTree.AsEnumerable().Reverse().ToList();
        
        foreach (var processId in reversedTree)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited)
                {
                    _logger.LogWarning("Force killing process {ProcessId} ({ProcessName})", processId, process.ProcessName);
                    process.Kill(entireProcessTree: true);
                    killedCount++;
                    
                    // Give a small delay to allow the kill to take effect
                    Thread.Sleep(100);
                }
                else
                {
                    _logger.LogDebug("Process {ProcessId} already exited, skipping", processId);
                }
            }
            catch (ArgumentException)
            {
                // Process doesn't exist anymore
                _logger.LogDebug("Process {ProcessId} no longer exists, skipping", processId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error force killing process {ProcessId}", processId);
            }
        }

        _logger.LogInformation("Force killed {KilledCount}/{TotalCount} processes", killedCount, processTree.Count);
        
        // Wait a bit more to ensure processes are fully terminated
        Thread.Sleep(1000);
        
        return killedCount;
    }

    /// <summary>
    /// Ensure all processes from previous run are completely terminated before restart
    /// </summary>
    private async Task EnsureProcessTreeTerminated()
    {
        if (_process == null)
            return;

        var processId = _process.Id;
        var processName = _process.ProcessName;
        
        _logger.LogInformation("Ensuring complete termination of process tree for PID {ProcessId} ({ProcessName})", processId, processName);

        try
        {
            // First, try graceful shutdown if process is still running
            if (!_process.HasExited)
            {
                _logger.LogInformation("Process still running, attempting graceful shutdown first");
                
                // Try CTRL+C to the process tree
                var signalCount = await SendCtrlCToProcessTree(processId, CancellationToken.None);
                if (signalCount > 0)
                {
                    _logger.LogInformation("Sent CTRL+C to {SignalCount} processes, waiting 5 seconds for graceful shutdown", signalCount);
                    
                    // Wait for graceful shutdown
                    var waited = 0;
                    while (!_process.HasExited && waited < 5000)
                    {
                        await Task.Delay(200);
                        waited += 200;
                    }
                }
            }

            // Force kill any remaining processes
            if (!_process.HasExited)
            {
                _logger.LogWarning("Graceful shutdown failed or timed out, force killing process tree");
                ForceKillProcessTree(processId);
            }
            else
            {
                _logger.LogInformation("Main process has exited, checking for orphaned child processes");
                
                // Even if main process exited, there might be orphaned children
                var orphanedChildren = GetChildProcesses(processId);
                if (orphanedChildren.Count > 0)
                {
                    _logger.LogWarning("Found {OrphanCount} orphaned child processes, force killing them", orphanedChildren.Count);
                    
                    foreach (var childId in orphanedChildren)
                    {
                        try
                        {
                            using var childProcess = Process.GetProcessById(childId);
                            if (!childProcess.HasExited)
                            {
                                _logger.LogWarning("Force killing orphaned child process {ProcessId} ({ProcessName})", childId, childProcess.ProcessName);
                                childProcess.Kill(entireProcessTree: true);
                                Thread.Sleep(100); // Small delay between kills
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Error killing orphaned child process {ProcessId}", childId);
                        }
                    }
                }
            }

            // Final verification - wait a bit and check if any processes are still running
            await Task.Delay(1000);
            var remainingProcesses = GetProcessTree(processId);
            var stillRunning = new List<int>();
            
            foreach (var pid in remainingProcesses)
            {
                try
                {
                    using var proc = Process.GetProcessById(pid);
                    if (!proc.HasExited)
                    {
                        stillRunning.Add(pid);
                    }
                }
                catch (ArgumentException)
                {
                    // Process doesn't exist - good!
                }
            }

            if (stillRunning.Count > 0)
            {
                _logger.LogError("WARNING: {StillRunningCount} processes are still running after termination attempt: [{ProcessIds}]", 
                    stillRunning.Count, string.Join(", ", stillRunning));
                
                // Last resort: try to kill them one more time
                foreach (var pid in stillRunning)
                {
                    try
                    {
                        using var stubborn = Process.GetProcessById(pid);
                        _logger.LogError("FORCE KILLING stubborn process {ProcessId} ({ProcessName})", pid, stubborn.ProcessName);
                        stubborn.Kill(entireProcessTree: true);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to kill stubborn process {ProcessId}", pid);
                    }
                }
            }
            else
            {
                _logger.LogInformation("All processes in tree have been successfully terminated");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during process tree termination");
        }
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

        try
        {
            // CRITICAL: Ensure all processes from previous run are completely dead before restart
            _logger.LogInformation("Ensuring complete termination of previous process tree before restart");
            await EnsureProcessTreeTerminated();
            
            // Clear the previous process reference since we've ensured it's terminated
            _process?.Dispose();
            _process = null;
            
            // Wait the configured delay before restarting
            _logger.LogInformation("Waiting {DelaySeconds} seconds before restart attempt", _config.Restart.DelaySeconds);
            await Task.Delay(TimeSpan.FromSeconds(_config.Restart.DelaySeconds), cancellationToken);

            _logger.LogInformation("Starting fresh process instance");
            await StartAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to restart process");
            
            // Log the restart failure to application error log as well
            var restartErrorMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] Restart attempt {_restartAttempts}/{_config.Restart.MaxAttempts} failed: {ex.Message}\n";
            await _logManager.WriteStderrAsync(restartErrorMessage);
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
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
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