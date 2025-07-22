using System.ComponentModel.DataAnnotations;

namespace WinLet.Core;

/// <summary>
/// Configuration model for WinLet service definition from TOML file
/// </summary>
public class ServiceConfig
{
    [Required]
    public string Name { get; set; } = string.Empty;
    
    [Required]
    public string DisplayName { get; set; } = string.Empty;
    
    public string? Description { get; set; }
    
    [Required]
    public ProcessConfig Process { get; set; } = new();
    
    public LoggingConfig Logging { get; set; } = new();
    
    public RestartConfig Restart { get; set; } = new();
    
    public HealthCheckConfig? HealthCheck { get; set; }
    
    public ServiceAccountConfig? ServiceAccount { get; set; }
    
    public CrashDumpConfig? CrashDump { get; set; }
}

/// <summary>
/// Process execution configuration
/// </summary>
public class ProcessConfig
{
    [Required]
    public string Executable { get; set; } = string.Empty;
    
    public string? Arguments { get; set; }
    
    public string? WorkingDirectory { get; set; }
    
    public Dictionary<string, string> Environment { get; set; } = new();
    
    /// <summary>
    /// Timeout in seconds for graceful shutdown
    /// </summary>
    public int ShutdownTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Enhanced logging configuration with multiple modes
/// </summary>
public class LoggingConfig
{
    public LogLevel Level { get; set; } = LogLevel.Information;
    
    public bool LogToConsole { get; set; } = true;
    
    /// <summary>
    /// Directory where log files are created. Defaults to service directory.
    /// </summary>
    public string? LogPath { get; set; }
    
    /// <summary>
    /// Logging mode (append, reset, none, roll-by-size, roll-by-time, roll-by-size-time)
    /// </summary>
    public LogMode Mode { get; set; } = LogMode.Append;
    
    /// <summary>
    /// Size threshold for log rotation in KB
    /// </summary>
    public int SizeThresholdKB { get; set; } = 10240; // 10MB default
    
    /// <summary>
    /// Number of rolled files to keep
    /// </summary>
    public int KeepFiles { get; set; } = 8;
    
    /// <summary>
    /// Pattern for time-based rolling (DateTime.ToString format)
    /// </summary>
    public string? TimePattern { get; set; } = "yyyyMMdd";
    
    /// <summary>
    /// Time of day to automatically roll logs (HH:mm:ss format)
    /// </summary>
    public string? AutoRollAtTime { get; set; } = "00:00:00";
    
    /// <summary>
    /// Compress logs older than specified days
    /// </summary>
    public int? ZipOlderThanDays { get; set; }
    
    /// <summary>
    /// Date format for zip file names
    /// </summary>
    public string? ZipDateFormat { get; set; } = "yyyyMM";
    
    /// <summary>
    /// Whether to separate stdout and stderr into different files
    /// </summary>
    public bool SeparateErrorLog { get; set; } = true;
    
    /// <summary>
    /// Custom stdout log file name (optional)
    /// </summary>
    public string? StdoutLogFile { get; set; }
    
    /// <summary>
    /// Custom stderr log file name (optional)
    /// </summary>
    public string? StderrLogFile { get; set; }

    // Legacy properties for backward compatibility
    public string? LogFile { get; set; }
    public int MaxLogFileSizeMB { get; set; } = 10;
    public int MaxLogFiles { get; set; } = 5;
}

/// <summary>
/// Service restart policy configuration
/// </summary>
public class RestartConfig
{
    public RestartPolicy Policy { get; set; } = RestartPolicy.OnFailure;
    
    /// <summary>
    /// Delay in seconds before restarting
    /// </summary>
    public int DelaySeconds { get; set; } = 5;
    
    /// <summary>
    /// Maximum restart attempts before giving up
    /// </summary>
    public int MaxAttempts { get; set; } = 3;
    
    /// <summary>
    /// Time window in seconds for counting restart attempts
    /// </summary>
    public int WindowSeconds { get; set; } = 300;
}

/// <summary>
/// Health check configuration (future extensibility)
/// </summary>
public class HealthCheckConfig
{
    public HealthCheckType Type { get; set; } = HealthCheckType.Http;
    
    public string? Endpoint { get; set; }
    
    /// <summary>
    /// Health check interval in seconds
    /// </summary>
    public int IntervalSeconds { get; set; } = 30;
    
    /// <summary>
    /// Timeout for health check in seconds
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;
    
    /// <summary>
    /// Number of consecutive failures before marking unhealthy
    /// </summary>
    public int FailureThreshold { get; set; } = 3;
}

/// <summary>
/// Service account configuration
/// </summary>
public class ServiceAccountConfig
{
    /// <summary>
    /// Username in format: DomainName\UserName, UserName@DomainName, .\UserName, or built-in accounts
    /// </summary>
    public string? Username { get; set; }
    
    /// <summary>
    /// Password for the user account (not needed for built-in accounts or GMSA)
    /// </summary>
    public string? Password { get; set; }
    
    /// <summary>
    /// Automatically grant "Log On As A Service" right to the account
    /// </summary>
    public bool AllowServiceLogon { get; set; } = true;
    
    /// <summary>
    /// Prompt for credentials (dialog or console)
    /// </summary>
    public PromptType? Prompt { get; set; }
}

/// <summary>
/// Crash dump generation configuration
/// </summary>
public class CrashDumpConfig
{
    /// <summary>
    /// Enable crash dump generation
    /// </summary>
    public bool Enabled { get; set; } = false;
    
    /// <summary>
    /// Directory where crash dumps are stored
    /// </summary>
    public string? DumpPath { get; set; }
    
    /// <summary>
    /// Type of crash dump to generate
    /// </summary>
    public CrashDumpType Type { get; set; } = CrashDumpType.MiniDump;
    
    /// <summary>
    /// Maximum number of crash dumps to keep
    /// </summary>
    public int MaxDumpFiles { get; set; } = 5;
    
    /// <summary>
    /// Maximum age in days for crash dumps before cleanup
    /// </summary>
    public int MaxAgeDays { get; set; } = 30;
    
    /// <summary>
    /// Include heap memory in crash dumps
    /// </summary>
    public bool IncludeHeap { get; set; } = false;
    
    /// <summary>
    /// Compress crash dump files
    /// </summary>
    public bool CompressDumps { get; set; } = true;
    
    /// <summary>
    /// Generate dump on unhandled exceptions (in addition to process crashes)
    /// </summary>
    public bool DumpOnException { get; set; } = true;
}

public enum LogLevel
{
    Trace,
    Debug,
    Information,
    Warning,
    Error,
    Critical
}

public enum LogMode
{
    /// <summary>
    /// Append to log files (default)
    /// </summary>
    Append,
    
    /// <summary>
    /// Reset (truncate) log files on service start
    /// </summary>
    Reset,
    
    /// <summary>
    /// Discard all output (no log files)
    /// </summary>
    None,
    
    /// <summary>
    /// Roll logs when they exceed size threshold
    /// </summary>
    RollBySize,
    
    /// <summary>
    /// Roll logs based on time pattern
    /// </summary>
    RollByTime,
    
    /// <summary>
    /// Roll logs by both size and time
    /// </summary>
    RollBySizeTime
}

public enum RestartPolicy
{
    Never,
    Always,
    OnFailure
}

public enum HealthCheckType
{
    Http,
    Tcp,
    Process
}

public enum PromptType
{
    Dialog,
    Console
}

public enum CrashDumpType
{
    /// <summary>
    /// Mini dump with minimal information (stack traces, loaded modules)
    /// </summary>
    MiniDump,
    
    /// <summary>
    /// Mini dump with data segments
    /// </summary>
    MiniDumpWithData,
    
    /// <summary>
    /// Full memory dump (includes all process memory)
    /// </summary>
    FullDump,
    
    /// <summary>
    /// Custom dump with specific data types
    /// </summary>
    Custom
}
