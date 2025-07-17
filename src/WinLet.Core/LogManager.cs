using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Microsoft.Extensions.Logging;

namespace WinLet.Core;

/// <summary>
/// Manages process output logging with various modes and rotation strategies
/// </summary>
public class LogManager : IDisposable
{
    private readonly LoggingConfig _config;
    private readonly string _serviceName;
    private readonly ILogger<LogManager> _logger;
    private readonly string _logDirectory;
    
    private StreamWriter? _stdoutWriter;
    private StreamWriter? _stderrWriter;
    private FileStream? _stdoutStream;
    private FileStream? _stderrStream;
    
    private Timer? _timeRollTimer;
    private DateTime _lastRollTime = DateTime.MinValue;
    private long _currentStdoutSize = 0;
    private long _currentStderrSize = 0;
    
    private bool _disposed = false;

    public LogManager(LoggingConfig config, string serviceName, ILogger<LogManager> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Determine log directory
        _logDirectory = !string.IsNullOrEmpty(_config.LogPath) 
            ? _config.LogPath 
            : Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory;
        
        // Ensure log directory exists
        Directory.CreateDirectory(_logDirectory);
        
        InitializeLogging();
    }

    /// <summary>
    /// Initialize logging based on configuration
    /// </summary>
    private void InitializeLogging()
    {
        if (_config.Mode == LogMode.None)
        {
            _logger.LogInformation("Logging disabled (mode: none)");
            return;
        }

        try
        {
            var stdoutFile = GetLogFileName("out");
            var stderrFile = _config.SeparateErrorLog ? GetLogFileName("err") : stdoutFile;

            _logger.LogInformation("Initializing logging - Mode: {Mode}, Stdout: {StdoutFile}, Stderr: {StderrFile}",
                _config.Mode, stdoutFile, stderrFile);

            // Handle reset mode
            if (_config.Mode == LogMode.Reset)
            {
                ResetLogFiles(stdoutFile, stderrFile);
            }

            // Open log files
            OpenLogFiles(stdoutFile, stderrFile);

            // Setup time-based rolling if needed
            if (_config.Mode == LogMode.RollByTime || _config.Mode == LogMode.RollBySizeTime)
            {
                SetupTimeRolling();
            }

            // Archive old logs if configured
            if (_config.ZipOlderThanDays.HasValue)
            {
                ArchiveOldLogs();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize logging");
            throw;
        }
    }

    /// <summary>
    /// Get the log file name based on configuration and current time
    /// </summary>
    private string GetLogFileName(string type)
    {
        var baseFileName = _serviceName;
        
        // Use custom file names if specified
        if (type == "out" && !string.IsNullOrEmpty(_config.StdoutLogFile))
        {
            return Path.Combine(_logDirectory, _config.StdoutLogFile);
        }
        if (type == "err" && !string.IsNullOrEmpty(_config.StderrLogFile))
        {
            return Path.Combine(_logDirectory, _config.StderrLogFile);
        }

        var fileName = _config.Mode switch
        {
            LogMode.RollByTime or LogMode.RollBySizeTime when !string.IsNullOrEmpty(_config.TimePattern)
                => $"{baseFileName}.{DateTime.Now.ToString(_config.TimePattern)}.{type}.log",
            _ => $"{baseFileName}.{type}.log"
        };

        return Path.Combine(_logDirectory, fileName);
    }

    /// <summary>
    /// Reset (truncate) log files
    /// </summary>
    private void ResetLogFiles(string stdoutFile, string stderrFile)
    {
        try
        {
            if (File.Exists(stdoutFile))
            {
                File.WriteAllText(stdoutFile, string.Empty);
                _logger.LogDebug("Reset stdout log file: {File}", stdoutFile);
            }

            if (_config.SeparateErrorLog && File.Exists(stderrFile) && stderrFile != stdoutFile)
            {
                File.WriteAllText(stderrFile, string.Empty);
                _logger.LogDebug("Reset stderr log file: {File}", stderrFile);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reset log files");
        }
    }

    /// <summary>
    /// Open log file streams
    /// </summary>
    private void OpenLogFiles(string stdoutFile, string stderrFile)
    {
        // Open stdout log
        _stdoutStream = new FileStream(stdoutFile, FileMode.Append, FileAccess.Write, FileShare.Read);
        _stdoutWriter = new StreamWriter(_stdoutStream, Encoding.UTF8) { AutoFlush = true };
        _currentStdoutSize = _stdoutStream.Length;

        // Open stderr log (may be same file as stdout)
        if (_config.SeparateErrorLog && stderrFile != stdoutFile)
        {
            _stderrStream = new FileStream(stderrFile, FileMode.Append, FileAccess.Write, FileShare.Read);
            _stderrWriter = new StreamWriter(_stderrStream, Encoding.UTF8) { AutoFlush = true };
            _currentStderrSize = _stderrStream.Length;
        }
        else
        {
            _stderrWriter = _stdoutWriter;
        }
    }

    /// <summary>
    /// Setup time-based log rolling timer
    /// </summary>
    private void SetupTimeRolling()
    {
        if (string.IsNullOrEmpty(_config.AutoRollAtTime))
            return;

        try
        {
            if (TimeSpan.TryParse(_config.AutoRollAtTime, out var rollTime))
            {
                var now = DateTime.Now;
                var nextRoll = now.Date.Add(rollTime);
                if (nextRoll <= now)
                    nextRoll = nextRoll.AddDays(1);

                var dueTime = nextRoll - now;
                var period = TimeSpan.FromDays(1);

                _timeRollTimer = new Timer(OnTimeRoll, null, dueTime, period);
                _logger.LogDebug("Time-based rolling scheduled for {NextRoll}", nextRoll);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to setup time-based rolling");
        }
    }

    /// <summary>
    /// Handle time-based log rolling
    /// </summary>
    private void OnTimeRoll(object? state)
    {
        try
        {
            _logger.LogDebug("Performing time-based log roll");
            PerformLogRoll(LogRollReason.Time);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during time-based log roll");
        }
    }

    /// <summary>
    /// Write stdout data to log
    /// </summary>
    public async Task WriteStdoutAsync(string data)
    {
        if (_config.Mode == LogMode.None || _stdoutWriter == null)
            return;

        try
        {
            await _stdoutWriter.WriteAsync(data);
            _currentStdoutSize += Encoding.UTF8.GetByteCount(data);

            // Check for size-based rolling
            if (ShouldRollBySize(_currentStdoutSize))
            {
                await PerformLogRoll(LogRollReason.Size);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write stdout data");
        }
    }

    /// <summary>
    /// Write stderr data to log
    /// </summary>
    public async Task WriteStderrAsync(string data)
    {
        if (_config.Mode == LogMode.None || _stderrWriter == null)
            return;

        try
        {
            await _stderrWriter.WriteAsync(data);
            
            if (_config.SeparateErrorLog && _stderrStream != null)
            {
                _currentStderrSize += Encoding.UTF8.GetByteCount(data);

                // Check for size-based rolling
                if (ShouldRollBySize(_currentStderrSize))
                {
                    await PerformLogRoll(LogRollReason.Size);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write stderr data");
        }
    }

    /// <summary>
    /// Check if logs should be rolled based on size
    /// </summary>
    private bool ShouldRollBySize(long currentSize)
    {
        if (_config.Mode != LogMode.RollBySize && _config.Mode != LogMode.RollBySizeTime)
            return false;

        var thresholdBytes = _config.SizeThresholdKB * 1024L;
        return currentSize >= thresholdBytes;
    }

    /// <summary>
    /// Perform log file rolling
    /// </summary>
    private async Task PerformLogRoll(LogRollReason reason)
    {
        try
        {
            _logger.LogDebug("Performing log roll - Reason: {Reason}", reason);

            // Close current writers
            _stdoutWriter?.Close();
            _stderrWriter?.Close();
            _stdoutStream?.Close();
            _stderrStream?.Close();

            // Roll the files
            await RollLogFiles();

            // Reopen with new file names
            var stdoutFile = GetLogFileName("out");
            var stderrFile = _config.SeparateErrorLog ? GetLogFileName("err") : stdoutFile;
            OpenLogFiles(stdoutFile, stderrFile);

            // Clean up old files
            CleanupOldLogFiles();

            _lastRollTime = DateTime.Now;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to perform log roll");
        }
    }

    /// <summary>
    /// Roll log files by renaming them
    /// </summary>
    private async Task RollLogFiles()
    {
        var stdoutFile = GetLogFileName("out");
        var stderrFile = _config.SeparateErrorLog ? GetLogFileName("err") : stdoutFile;

        await Task.Run(() =>
        {
            RollFile(stdoutFile, "out");
            if (_config.SeparateErrorLog && stderrFile != stdoutFile)
            {
                RollFile(stderrFile, "err");
            }
        });
    }

    /// <summary>
    /// Roll a single log file
    /// </summary>
    private void RollFile(string filePath, string type)
    {
        if (!File.Exists(filePath))
            return;

        var directory = Path.GetDirectoryName(filePath)!;
        var baseFileName = Path.GetFileNameWithoutExtension(filePath);
        var extension = Path.GetExtension(filePath);

        // Find the next available number
        var rollNumber = 1;
        string rolledFile;
        do
        {
            rolledFile = Path.Combine(directory, $"{baseFileName}.{rollNumber}{extension}");
            rollNumber++;
        } while (File.Exists(rolledFile) && rollNumber <= _config.KeepFiles);

        try
        {
            File.Move(filePath, rolledFile);
            _logger.LogDebug("Rolled log file: {Original} -> {Rolled}", filePath, rolledFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to roll log file: {File}", filePath);
        }
    }

    /// <summary>
    /// Clean up old log files beyond the keep limit
    /// </summary>
    private void CleanupOldLogFiles()
    {
        try
        {
            // Use multiple patterns to catch all log file variations  
            var patterns = new[]
            {
                $"{_serviceName}.*.log",           // Basic: service.out.log, service.err.log
                $"{_serviceName}.*.*.log",         // Rolled: service.out.1.log, service.20241201.out.log
                $"{_serviceName}.*.*.*.log"        // Complex: service.20241201.out.1.log (if both time + size rolling)
            };
            
            var allLogFiles = new List<FileInfo>();
            foreach (var pattern in patterns)
            {
                var files = Directory.GetFiles(_logDirectory, pattern)
                    .Select(f => new FileInfo(f))
                    .ToArray();
                allLogFiles.AddRange(files);
            }
            
            // Remove duplicates and sort by last write time
            var logFiles = allLogFiles
                .GroupBy(f => f.FullName)
                .Select(g => g.First())
                .OrderByDescending(f => f.LastWriteTime)
                .Skip(_config.KeepFiles)
                .ToList();

            foreach (var file in logFiles)
            {
                try
                {
                    file.Delete();
                    _logger.LogDebug("Deleted old log file: {File}", file.FullName);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete old log file: {File}", file.FullName);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to cleanup old log files");
        }
    }

    /// <summary>
    /// Archive old log files
    /// </summary>
    private void ArchiveOldLogs()
    {
        if (!_config.ZipOlderThanDays.HasValue)
            return;

        try
        {
            var cutoffDate = DateTime.Now.AddDays(-_config.ZipOlderThanDays.Value);
            
            // Use multiple patterns to catch all log file variations
            var patterns = new[]
            {
                $"{_serviceName}.*.log",           // Basic: service.out.log, service.err.log
                $"{_serviceName}.*.*.log",         // Rolled: service.out.1.log, service.20241201.out.log
                $"{_serviceName}.*.*.*.log"        // Complex: service.20241201.out.1.log (if both time + size rolling)
            };
            
            var oldFiles = new List<FileInfo>();
            foreach (var pattern in patterns)
            {
                var files = Directory.GetFiles(_logDirectory, pattern)
                    .Select(f => new FileInfo(f))
                    .Where(f => f.LastWriteTime < cutoffDate)
                    .ToArray();
                oldFiles.AddRange(files);
            }
            
            // Remove duplicates (in case a file matches multiple patterns)
            oldFiles = oldFiles.GroupBy(f => f.FullName).Select(g => g.First()).ToList();
            
            // Exclude currently active log files to prevent conflicts
            var currentStdoutFile = GetLogFileName("out");
            var currentStderrFile = _config.SeparateErrorLog ? GetLogFileName("err") : currentStdoutFile;
            
            oldFiles = oldFiles.Where(f => 
                !string.Equals(f.FullName, currentStdoutFile, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(f.FullName, currentStderrFile, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (!oldFiles.Any())
            {
                _logger.LogDebug("No old log files found for archiving");
                return;
            }

            var zipFileName = $"{_serviceName}.{DateTime.Now.ToString(_config.ZipDateFormat ?? "yyyyMM")}.zip";
            var zipPath = Path.Combine(_logDirectory, zipFileName);
            
            // If zip file already exists, append timestamp to make it unique
            if (File.Exists(zipPath))
            {
                var timestamp = DateTime.Now.ToString("HHmmss");
                zipFileName = $"{_serviceName}.{DateTime.Now.ToString(_config.ZipDateFormat ?? "yyyyMM")}.{timestamp}.zip";
                zipPath = Path.Combine(_logDirectory, zipFileName);
            }

            using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
            foreach (var file in oldFiles)
            {
                zip.CreateEntryFromFile(file.FullName, file.Name);
                file.Delete();
                _logger.LogDebug("Archived and deleted log file: {File}", file.FullName);
            }

            _logger.LogInformation("Archived {Count} old log files to {ZipFile}", oldFiles.Count, zipFileName);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to archive old log files");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _timeRollTimer?.Dispose();
        _stdoutWriter?.Close();
        _stderrWriter?.Close();
        _stdoutStream?.Close();
        _stderrStream?.Close();

        _disposed = true;
    }
}

/// <summary>
/// Reason for log rolling
/// </summary>
public enum LogRollReason
{
    Size,
    Time,
    Manual
} 