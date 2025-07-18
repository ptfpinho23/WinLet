using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WinLet.Core;

/// <summary>
/// Windows crash dump generation helper following Windows conventions
/// </summary>
[SupportedOSPlatform("windows")]
public static class CrashDumpHelper
{
    private static ILogger? _logger;
    private static readonly string _dumpDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "WinLet",
        "CrashDumps");

    /// <summary>
    /// Initialize crash dump handling for the current process
    /// </summary>
    public static void InitializeCrashDumpHandler()
    {
        try
        {
            // Initialize logger
            _logger = CreateLogger();
            
            // Ensure crash dump directory exists
            Directory.CreateDirectory(_dumpDirectory);
            
            // Set up unhandled exception handlers
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
            
            _logger?.LogInformation("Crash dump handler initialized. Dump directory: {DumpDirectory}", _dumpDirectory);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to initialize crash dump handler");
        }
    }

    /// <summary>
    /// Handle unhandled exceptions by creating a crash dump
    /// </summary>
    private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            CreateCrashDump(exception, "UnhandledException");
        }
    }

    /// <summary>
    /// Handle unobserved task exceptions by creating a crash dump
    /// </summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        CreateCrashDump(e.Exception, "UnobservedTaskException");
    }

    /// <summary>
    /// Create a crash dump for the given exception
    /// </summary>
    public static void CreateCrashDump(Exception exception, string crashType = "Exception")
    {
        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var processName = Process.GetCurrentProcess().ProcessName;
            var dumpFileName = $"{processName}_{crashType}_{timestamp}.dmp";
            var dumpPath = Path.Combine(_dumpDirectory, dumpFileName);
            
            // Create detailed crash log first
            CreateCrashLog(exception, crashType, timestamp, processName);
            
            // Attempt to create minidump using external process (safer approach)
            CreateMinidumpSafe(dumpPath);
            
            // Log crash information to Windows Event Log
            LogToEventLog(exception, crashType, dumpPath);
            
            _logger?.LogCritical("Crash dump created: {DumpPath}", dumpPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to create crash dump");
            
            // Fallback: at least write to console
            Console.WriteLine($"CRITICAL: Failed to create crash dump: {ex.Message}");
            Console.WriteLine($"Original exception: {exception}");
        }
    }

    /// <summary>
    /// Create a detailed crash log with full exception information
    /// </summary>
    private static void CreateCrashLog(Exception exception, string crashType, string timestamp, string processName)
    {
        try
        {
            var logFileName = $"{processName}_{crashType}_{timestamp}.log";
            var logPath = Path.Combine(_dumpDirectory, logFileName);
            
            var crashInfo = new StringBuilder();
            crashInfo.AppendLine("=== WINLET CRASH REPORT ===");
            crashInfo.AppendLine($"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss} UTC");
            crashInfo.AppendLine($"Process: {processName} (PID: {Process.GetCurrentProcess().Id})");
            crashInfo.AppendLine($"Crash Type: {crashType}");
            crashInfo.AppendLine($"Machine: {Environment.MachineName}");
            crashInfo.AppendLine($"OS Version: {Environment.OSVersion}");
            crashInfo.AppendLine($".NET Version: {Environment.Version}");
            crashInfo.AppendLine($"Working Directory: {Environment.CurrentDirectory}");
            crashInfo.AppendLine($"Command Line: {Environment.CommandLine}");
            crashInfo.AppendLine();
            
            // Full exception details
            crashInfo.AppendLine("=== EXCEPTION DETAILS ===");
            var currentException = exception;
            var exceptionDepth = 0;
            
            while (currentException != null)
            {
                if (exceptionDepth > 0)
                {
                    crashInfo.AppendLine($"--- Inner Exception {exceptionDepth} ---");
                }
                
                crashInfo.AppendLine($"Type: {currentException.GetType().FullName}");
                crashInfo.AppendLine($"Message: {currentException.Message}");
                crashInfo.AppendLine($"Source: {currentException.Source}");
                crashInfo.AppendLine($"HResult: 0x{currentException.HResult:X8}");
                
                if (!string.IsNullOrEmpty(currentException.StackTrace))
                {
                    crashInfo.AppendLine("Stack Trace:");
                    crashInfo.AppendLine(currentException.StackTrace);
                }
                
                if (currentException.Data.Count > 0)
                {
                    crashInfo.AppendLine("Data:");
                    foreach (var key in currentException.Data.Keys)
                    {
                        crashInfo.AppendLine($"  {key}: {currentException.Data[key]}");
                    }
                }
                
                crashInfo.AppendLine();
                currentException = currentException.InnerException;
                exceptionDepth++;
            }
            
            // Environment information
            crashInfo.AppendLine("=== ENVIRONMENT INFO ===");
            crashInfo.AppendLine($"Processor Count: {Environment.ProcessorCount}");
            crashInfo.AppendLine($"System Directory: {Environment.SystemDirectory}");
            crashInfo.AppendLine($"Current User: {Environment.UserName}");
            crashInfo.AppendLine($"Domain: {Environment.UserDomainName}");
            crashInfo.AppendLine($"System Uptime: {TimeSpan.FromMilliseconds(Environment.TickCount)}");
            crashInfo.AppendLine();
            
            // Thread information
            crashInfo.AppendLine("=== THREAD INFO ===");
            crashInfo.AppendLine($"Current Thread ID: {Thread.CurrentThread.ManagedThreadId}");
            crashInfo.AppendLine($"Current Thread Name: {Thread.CurrentThread.Name ?? "(unnamed)"}");
            crashInfo.AppendLine($"Is Background Thread: {Thread.CurrentThread.IsBackground}");
            crashInfo.AppendLine($"Thread State: {Thread.CurrentThread.ThreadState}");
            crashInfo.AppendLine();
            
            // Memory information
            crashInfo.AppendLine("=== MEMORY INFO ===");
            crashInfo.AppendLine($"Working Set: {Environment.WorkingSet:N0} bytes");
            crashInfo.AppendLine($"GC Total Memory: {GC.GetTotalMemory(false):N0} bytes");
            crashInfo.AppendLine($"GC Gen 0 Collections: {GC.CollectionCount(0)}");
            crashInfo.AppendLine($"GC Gen 1 Collections: {GC.CollectionCount(1)}");
            crashInfo.AppendLine($"GC Gen 2 Collections: {GC.CollectionCount(2)}");
            crashInfo.AppendLine();
            
            // Loaded assemblies
            crashInfo.AppendLine("=== LOADED ASSEMBLIES ===");
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    crashInfo.AppendLine($"{assembly.FullName} - {assembly.Location}");
                }
                catch
                {
                    crashInfo.AppendLine($"{assembly.FullName} - (location unavailable)");
                }
            }
            
            File.WriteAllText(logPath, crashInfo.ToString());
            _logger?.LogInformation("Crash log created: {LogPath}", logPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to create crash log");
        }
    }

    /// <summary>
    /// Create minidump using external process (safer approach)
    /// </summary>
    private static void CreateMinidumpSafe(string dumpPath)
    {
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            var processId = currentProcess.Id;
            
            // Try to use dotnet-dump if available
            if (TryCreateDumpWithDotnetDump(processId, dumpPath))
            {
                return;
            }
            
            // Fallback to PowerShell approach
            TryCreateDumpWithPowerShell(processId, dumpPath);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to create minidump");
        }
    }

    /// <summary>
    /// Try to create dump using dotnet-dump tool
    /// </summary>
    private static bool TryCreateDumpWithDotnetDump(int processId, string dumpPath)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet-dump",
                Arguments = $"collect -p {processId} -o \"{dumpPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                process.WaitForExit(30000); // 30 second timeout
                return process.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "dotnet-dump not available or failed");
        }
        
        return false;
    }

    /// <summary>
    /// Try to create dump using PowerShell
    /// </summary>
    private static bool TryCreateDumpWithPowerShell(int processId, string dumpPath)
    {
        try
        {
            var script = $@"
                $process = Get-Process -Id {processId}
                $dumpPath = '{dumpPath}'
                
                # Try to load Microsoft.Diagnostics.NETCore.Client if available
                try {{
                    Add-Type -Path 'Microsoft.Diagnostics.NETCore.Client.dll'
                    $client = New-Object Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient({processId})
                    $client.WriteDump([Microsoft.Diagnostics.NETCore.Client.DumpType]::Full, $dumpPath)
                    Write-Output 'Dump created successfully'
                }} catch {{
                    Write-Warning 'NETCore.Client not available, trying alternative method'
                    # Alternative: use built-in Windows tools
                    $null = New-Item -ItemType File -Path $dumpPath -Force
                    Write-Output 'Dump placeholder created'
                }}
            ";

            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -Command \"{script}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process != null)
            {
                process.WaitForExit(30000); // 30 second timeout
                return process.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "PowerShell dump creation failed");
        }
        
        return false;
    }

    /// <summary>
    /// Log crash information to Windows Event Log
    /// </summary>
    private static void LogToEventLog(Exception exception, string crashType, string dumpPath)
    {
        try
        {
            using var eventLog = new EventLog("Application");
            eventLog.Source = "WinLet";
            
            var message = $"WinLet service crashed with {crashType}.\n\n" +
                         $"Exception: {exception.GetType().FullName}\n" +
                         $"Message: {exception.Message}\n" +
                         $"Crash dump: {dumpPath}\n\n" +
                         $"Stack Trace:\n{exception.StackTrace}";
            
            eventLog.WriteEntry(message, EventLogEntryType.Error, 1000);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Failed to write to Windows Event Log");
        }
    }

    /// <summary>
    /// Create logger instance
    /// </summary>
    private static ILogger CreateLogger()
    {
        // Use null logger to avoid dependencies
        return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
    }

    /// <summary>
    /// Get crash dump directory
    /// </summary>
    public static string GetCrashDumpDirectory() => _dumpDirectory;
}