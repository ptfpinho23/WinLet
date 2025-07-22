using System.ComponentModel.DataAnnotations;
using Tomlyn;
using Tomlyn.Model;

namespace WinLet.Core;

/// <summary>
/// Loads and validates WinLet service configuration from TOML files
/// </summary>
public class ConfigLoader
{
    /// <summary>
    /// Load service configuration from TOML file
    /// </summary>
    /// <param name="configPath">Path to the .toml configuration file</param>
    /// <returns>Validated ServiceConfig instance</returns>
    /// <exception cref="ConfigurationException">Thrown when configuration is invalid</exception>
    public static ServiceConfig LoadFromFile(string configPath)
    {
        if (!File.Exists(configPath))
        {
            throw new ConfigurationException($"Configuration file not found: {configPath}");
        }

        try
        {
            var tomlContent = File.ReadAllText(configPath);
            return LoadFromString(tomlContent);
        }
        catch (Exception ex) when (!(ex is ConfigurationException))
        {
            throw new ConfigurationException($"Failed to read configuration file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Load service configuration from TOML string content
    /// </summary>
    /// <param name="tomlContent">TOML content as string</param>
    /// <returns>Validated ServiceConfig instance</returns>
    /// <exception cref="ConfigurationException">Thrown when configuration is invalid</exception>
    public static ServiceConfig LoadFromString(string tomlContent)
    {
        try
        {
            var tomlTable = Toml.ToModel(tomlContent);
            var config = MapToServiceConfig(tomlTable);
            
            ValidateConfiguration(config);
            
            return config;
        }
        catch (Exception ex) when (!(ex is ConfigurationException))
        {
            throw new ConfigurationException($"Failed to parse TOML configuration: {ex.Message}", ex);
        }
    }

    private static ServiceConfig MapToServiceConfig(TomlTable tomlTable)
    {
        var config = new ServiceConfig();

        // Map basic service properties from [service] section
        if (tomlTable.TryGetValue("service", out var serviceObj) && serviceObj is TomlTable serviceTable)
        {
            if (serviceTable.TryGetValue("name", out var name))
                config.Name = name.ToString() ?? string.Empty;
            
            if (serviceTable.TryGetValue("display_name", out var displayName))
                config.DisplayName = displayName.ToString() ?? string.Empty;
            
            if (serviceTable.TryGetValue("description", out var description))
                config.Description = description.ToString();
        }

        // Map process configuration
        if (tomlTable.TryGetValue("process", out var processObj) && processObj is TomlTable processTable)
        {
            config.Process = MapProcessConfig(processTable);
        }

        // Map logging configuration
        if (tomlTable.TryGetValue("logging", out var loggingObj) && loggingObj is TomlTable loggingTable)
        {
            config.Logging = MapLoggingConfig(loggingTable);
        }

        // Map restart configuration
        if (tomlTable.TryGetValue("restart", out var restartObj) && restartObj is TomlTable restartTable)
        {
            config.Restart = MapRestartConfig(restartTable);
        }

        // Map health check configuration
        if (tomlTable.TryGetValue("health_check", out var healthObj) && healthObj is TomlTable healthTable)
        {
            config.HealthCheck = MapHealthCheckConfig(healthTable);
        }

        // Map service account configuration
        if (tomlTable.TryGetValue("service_account", out var serviceAccountObj) && serviceAccountObj is TomlTable serviceAccountTable)
        {
            config.ServiceAccount = MapServiceAccountConfig(serviceAccountTable);
        }

        // Map crash dump configuration
        if (tomlTable.TryGetValue("crash_dump", out var crashDumpObj) && crashDumpObj is TomlTable crashDumpTable)
        {
            config.CrashDump = MapCrashDumpConfig(crashDumpTable);
        }

        return config;
    }

    private static ProcessConfig MapProcessConfig(TomlTable processTable)
    {
        var process = new ProcessConfig();

        if (processTable.TryGetValue("executable", out var executable))
            process.Executable = executable.ToString() ?? string.Empty;
        
        if (processTable.TryGetValue("arguments", out var arguments))
            process.Arguments = arguments.ToString();
        
        if (processTable.TryGetValue("working_directory", out var workingDir))
            process.WorkingDirectory = workingDir.ToString();
        
        if (processTable.TryGetValue("shutdown_timeout_seconds", out var timeout))
            process.ShutdownTimeoutSeconds = Convert.ToInt32(timeout);

        // Map environment variables
        if (processTable.TryGetValue("environment", out var envObj) && envObj is TomlTable envTable)
        {
            foreach (var kvp in envTable)
            {
                process.Environment[kvp.Key] = kvp.Value.ToString() ?? string.Empty;
            }
        }

        return process;
    }

    private static LoggingConfig MapLoggingConfig(TomlTable loggingTable)
    {
        var logging = new LoggingConfig();

        if (loggingTable.TryGetValue("level", out var level))
        {
            if (Enum.TryParse<LogLevel>(level.ToString(), true, out var logLevel))
                logging.Level = logLevel;
        }
        
        if (loggingTable.TryGetValue("log_to_console", out var logToConsole))
            logging.LogToConsole = Convert.ToBoolean(logToConsole);
        
        // New logging properties
        if (loggingTable.TryGetValue("log_path", out var logPath))
            logging.LogPath = logPath.ToString();
            
        if (loggingTable.TryGetValue("mode", out var mode))
        {
            if (Enum.TryParse<LogMode>(mode.ToString().Replace("-", ""), true, out var logMode))
                logging.Mode = logMode;
        }
        
        if (loggingTable.TryGetValue("size_threshold_kb", out var sizeThreshold))
            logging.SizeThresholdKB = Convert.ToInt32(sizeThreshold);
            
        if (loggingTable.TryGetValue("keep_files", out var keepFiles))
            logging.KeepFiles = Convert.ToInt32(keepFiles);
            
        if (loggingTable.TryGetValue("time_pattern", out var timePattern))
            logging.TimePattern = timePattern.ToString();
            
        if (loggingTable.TryGetValue("auto_roll_at_time", out var autoRollTime))
            logging.AutoRollAtTime = autoRollTime.ToString();
            
        if (loggingTable.TryGetValue("zip_older_than_days", out var zipOlderDays))
            logging.ZipOlderThanDays = Convert.ToInt32(zipOlderDays);
            
        if (loggingTable.TryGetValue("zip_date_format", out var zipDateFormat))
            logging.ZipDateFormat = zipDateFormat.ToString();
            
        if (loggingTable.TryGetValue("separate_error_log", out var separateError))
            logging.SeparateErrorLog = Convert.ToBoolean(separateError);
            
        if (loggingTable.TryGetValue("stdout_log_file", out var stdoutFile))
            logging.StdoutLogFile = stdoutFile.ToString();
            
        if (loggingTable.TryGetValue("stderr_log_file", out var stderrFile))
            logging.StderrLogFile = stderrFile.ToString();
        
        // Legacy properties for backward compatibility
        if (loggingTable.TryGetValue("log_file", out var logFile))
            logging.LogFile = logFile.ToString();
        
        if (loggingTable.TryGetValue("max_log_file_size_mb", out var maxSize))
            logging.MaxLogFileSizeMB = Convert.ToInt32(maxSize);
        
        if (loggingTable.TryGetValue("max_log_files", out var maxFiles))
            logging.MaxLogFiles = Convert.ToInt32(maxFiles);

        return logging;
    }

    private static RestartConfig MapRestartConfig(TomlTable restartTable)
    {
        var restart = new RestartConfig();

        if (restartTable.TryGetValue("policy", out var policy))
        {
            if (Enum.TryParse<RestartPolicy>(policy.ToString(), true, out var restartPolicy))
                restart.Policy = restartPolicy;
        }
        
        if (restartTable.TryGetValue("delay_seconds", out var delay))
            restart.DelaySeconds = Convert.ToInt32(delay);
        
        if (restartTable.TryGetValue("max_attempts", out var maxAttempts))
            restart.MaxAttempts = Convert.ToInt32(maxAttempts);
        
        if (restartTable.TryGetValue("window_seconds", out var window))
            restart.WindowSeconds = Convert.ToInt32(window);

        return restart;
    }

    private static HealthCheckConfig MapHealthCheckConfig(TomlTable healthTable)
    {
        var health = new HealthCheckConfig();

        if (healthTable.TryGetValue("type", out var type))
        {
            if (Enum.TryParse<HealthCheckType>(type.ToString(), true, out var healthType))
                health.Type = healthType;
        }
        
        if (healthTable.TryGetValue("endpoint", out var endpoint))
            health.Endpoint = endpoint.ToString();
        
        if (healthTable.TryGetValue("interval_seconds", out var interval))
            health.IntervalSeconds = Convert.ToInt32(interval);
        
        if (healthTable.TryGetValue("timeout_seconds", out var timeout))
            health.TimeoutSeconds = Convert.ToInt32(timeout);
        
        if (healthTable.TryGetValue("failure_threshold", out var threshold))
            health.FailureThreshold = Convert.ToInt32(threshold);

        return health;
    }

    private static ServiceAccountConfig MapServiceAccountConfig(TomlTable serviceAccountTable)
    {
        var serviceAccount = new ServiceAccountConfig();

        if (serviceAccountTable.TryGetValue("username", out var username))
            serviceAccount.Username = username.ToString();
            
        if (serviceAccountTable.TryGetValue("password", out var password))
            serviceAccount.Password = password.ToString();
            
        if (serviceAccountTable.TryGetValue("allow_service_logon", out var allowLogon))
            serviceAccount.AllowServiceLogon = Convert.ToBoolean(allowLogon);
            
        if (serviceAccountTable.TryGetValue("prompt", out var prompt))
        {
            if (Enum.TryParse<PromptType>(prompt.ToString(), true, out var promptType))
                serviceAccount.Prompt = promptType;
        }

        return serviceAccount;
    }

    private static CrashDumpConfig MapCrashDumpConfig(TomlTable crashDumpTable)
    {
        var crashDump = new CrashDumpConfig();

        if (crashDumpTable.TryGetValue("enabled", out var enabled))
            crashDump.Enabled = Convert.ToBoolean(enabled);
            
        if (crashDumpTable.TryGetValue("dump_path", out var dumpPath))
            crashDump.DumpPath = dumpPath.ToString();
            
        if (crashDumpTable.TryGetValue("type", out var type))
        {
            if (Enum.TryParse<CrashDumpType>(type.ToString(), true, out var dumpType))
                crashDump.Type = dumpType;
        }
        
        if (crashDumpTable.TryGetValue("max_dump_files", out var maxFiles))
            crashDump.MaxDumpFiles = Convert.ToInt32(maxFiles);
            
        if (crashDumpTable.TryGetValue("max_age_days", out var maxAge))
            crashDump.MaxAgeDays = Convert.ToInt32(maxAge);
            
        if (crashDumpTable.TryGetValue("include_heap", out var includeHeap))
            crashDump.IncludeHeap = Convert.ToBoolean(includeHeap);
            
        if (crashDumpTable.TryGetValue("compress_dumps", out var compress))
            crashDump.CompressDumps = Convert.ToBoolean(compress);
            
        if (crashDumpTable.TryGetValue("dump_on_exception", out var dumpOnException))
            crashDump.DumpOnException = Convert.ToBoolean(dumpOnException);

        return crashDump;
    }

    private static void ValidateConfiguration(ServiceConfig config)
    {
        var context = new ValidationContext(config);
        var results = new List<ValidationResult>();

        if (!Validator.TryValidateObject(config, context, results, true))
        {
            var errors = string.Join(Environment.NewLine, results.Select(r => r.ErrorMessage));
            throw new ConfigurationException($"Configuration validation failed:{Environment.NewLine}{errors}");
        }

        // Additional custom validation
        if (string.IsNullOrWhiteSpace(config.Process.Executable))
        {
            throw new ConfigurationException("Process executable is required");
        }

        if (!string.IsNullOrEmpty(config.Process.WorkingDirectory) && 
            !Directory.Exists(config.Process.WorkingDirectory))
        {
            throw new ConfigurationException($"Working directory does not exist: {config.Process.WorkingDirectory}");
        }
    }
}

/// <summary>
/// Exception thrown when configuration loading or validation fails
/// </summary>
public class ConfigurationException : Exception
{
    public ConfigurationException(string message) : base(message) { }
    public ConfigurationException(string message, Exception innerException) : base(message, innerException) { }
} 