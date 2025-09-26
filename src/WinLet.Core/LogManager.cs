using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    private Timer? _delayedArchiveTimer;
    private DateTime _lastRollTime = DateTime.MinValue;
    private long _currentStdoutSize = 0;
    private long _currentStderrSize = 0;
    private readonly SemaphoreSlim _rollLock = new(1, 1);
    private readonly SemaphoreSlim _ioLock = new(1, 1);
    private int _sizeRollQueued = 0;

    // Track the actual file paths we opened to avoid DateTime-based name drift
    private string? _currentStdoutPath;
    private string? _currentStderrPath;

    private bool _disposed = false;

    public LogManager(LoggingConfig config, string serviceName, ILogger<LogManager> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Log successful initialization
        _logger.LogInformation("LogManager initialized for service: {ServiceName}", serviceName);

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

            // Schedule initial archiving if configured (delayed to allow any existing handles to close)
            if (_config.ZipOlderThanDays.HasValue)
            {
                ScheduleDelayedArchiving(5); // Shorter delay on startup
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
        // Use ReadWrite sharing to allow archiving process to read and delete files
        _stdoutStream = new FileStream(stdoutFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _stdoutWriter = new StreamWriter(_stdoutStream, Encoding.UTF8) { AutoFlush = true };
        _currentStdoutSize = _stdoutStream.Length;
        _currentStdoutPath = stdoutFile;

        if (_config.SeparateErrorLog && stderrFile != stdoutFile)
        {
            _stderrStream = new FileStream(stderrFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _stderrWriter = new StreamWriter(_stderrStream, Encoding.UTF8) { AutoFlush = true };
            _currentStderrSize = _stderrStream.Length;
            _currentStderrPath = stderrFile;
        }
        else
        {
            _stderrWriter = _stdoutWriter;
            _stderrStream = null;
            _currentStderrSize = _currentStdoutSize;
            _currentStderrPath = _currentStdoutPath;
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
            PerformLogRoll(LogRollReason.Time).GetAwaiter().GetResult();

            // (Optional) Deferred archive still okay; PerformLogRoll already triggers archive.
            // Schedule delayed archiving after time roll
            ScheduleDelayedArchiving();
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
            bool needRoll = false;
            await _ioLock.WaitAsync();
            try
            {
                await _stdoutWriter.WriteAsync(data);
                _currentStdoutSize += Encoding.UTF8.GetByteCount(data);

                // Check for size-based rolling (queue in background)
                needRoll = ShouldRollBySize(_currentStdoutSize);
            }
            finally
            {
                _ioLock.Release();
            }

            if (needRoll && Interlocked.CompareExchange(ref _sizeRollQueued, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try { await PerformLogRoll(LogRollReason.Size); }
                    finally { Interlocked.Exchange(ref _sizeRollQueued, 0); }
                });
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
            bool needRoll = false;
            await _ioLock.WaitAsync();
            try
            {
                await _stderrWriter.WriteAsync(data);

                if (_config.SeparateErrorLog && _stderrStream != null)
                {
                    _currentStderrSize += Encoding.UTF8.GetByteCount(data);
                    needRoll = ShouldRollBySize(_currentStderrSize);
                }
            }
            finally
            {
                _ioLock.Release();
            }

            if (needRoll && Interlocked.CompareExchange(ref _sizeRollQueued, 1, 0) == 0)
            {
                _ = Task.Run(async () =>
                {
                    try { await PerformLogRoll(LogRollReason.Size); }
                    finally { Interlocked.Exchange(ref _sizeRollQueued, 0); }
                });
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

        if (_config.Mode == LogMode.RollByTime)
            return false;
            
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
        await _rollLock.WaitAsync();
        await _ioLock.WaitAsync();
        try
        {
            _logger.LogDebug("Performing log roll - Reason: {Reason}", reason);

            // Close current writers and flush
            try { _stdoutWriter?.Flush(); } catch { }
            try { _stderrWriter?.Flush(); } catch { }
            try { _stdoutWriter?.Close(); } catch { }
            try { _stderrWriter?.Close(); } catch { }
            try { _stdoutStream?.Close(); } catch { }
            try { _stderrStream?.Close(); } catch { }

            // Roll the files using the actual open paths (avoid recomputation)
            await RollLogFiles();

            // Reopen with new file names
            // IMPORTANT: Recompute names after rolling to avoid reopening the just-rolled files.
            var stdoutFile = GetLogFileName("out");
            var stderrFile = _config.SeparateErrorLog ? GetLogFileName("err") : stdoutFile;
            OpenLogFiles(stdoutFile, stderrFile);

            // Clean up old files
            CleanupOldLogFiles();

            _lastRollTime = DateTime.Now;

            // Schedule delayed archiving after roll to allow file handles to be released
            ScheduleDelayedArchiving();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to perform log roll");
        }
        finally
        {
            _ioLock.Release();
            _rollLock.Release();
        }
    }

    /// <summary>
    /// Roll log files by renaming them
    /// </summary>
    private async Task RollLogFiles()
    {
        var stdoutFile = _currentStdoutPath;
        var stderrFile = _currentStderrPath;

        await Task.Run(() =>
        {
            if (!string.IsNullOrEmpty(stdoutFile))
            {
                RollFile(stdoutFile, "out");
            }
            if (_config.SeparateErrorLog &&
                !string.IsNullOrEmpty(stderrFile) &&
                !string.Equals(stderrFile, stdoutFile, StringComparison.OrdinalIgnoreCase))
            {
                RollFile(stderrFile!, "err");
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
        var fileName = Path.GetFileName(filePath);

        string rolledFile;
        var rollNumber = 1;
        

        if (_config.Mode == LogMode.RollByTime && !string.IsNullOrEmpty(_config.TimePattern))
        {
            _logger.LogDebug("Skipping roll for time-based file (will roll at midnight): {File}", filePath);
            return;
        }
        
        if (_config.Mode == LogMode.RollBySizeTime && !string.IsNullOrEmpty(_config.TimePattern))
        {
            // For mixed time+size rolling, insert roll number before the final .log extension
            // Pattern: ServiceName.YYYYMMDD.type.log -> ServiceName.YYYYMMDD.type.N.log
            var lastDotIndex = fileName.LastIndexOf('.');
            if (lastDotIndex > 0)
            {
                var nameWithoutExt = fileName.Substring(0, lastDotIndex);
                var extension = fileName.Substring(lastDotIndex);
                
                do
                {
                    rolledFile = Path.Combine(directory, $"{nameWithoutExt}.{rollNumber}{extension}");
                    rollNumber++;
                } while (File.Exists(rolledFile) && rollNumber <= _config.KeepFiles);
            }
            else
            {
                // Fallback if no extension found
                do
                {
                    rolledFile = Path.Combine(directory, $"{fileName}.{rollNumber}");
                    rollNumber++;
                } while (File.Exists(rolledFile) && rollNumber <= _config.KeepFiles);
            }
        }
        else
        {
            var baseFileName = Path.GetFileNameWithoutExtension(filePath);
            var extension = Path.GetExtension(filePath);
            
            do
            {
                rolledFile = Path.Combine(directory, $"{baseFileName}.{rollNumber}{extension}");
                rollNumber++;
            } while (File.Exists(rolledFile) && rollNumber <= _config.KeepFiles);
        }

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
    /// Check if a file is currently active (being written to by this LogManager)
    /// </summary>
    private bool IsCurrentlyActiveLogFile(string filePath)
    {
        return string.Equals(filePath, _currentStdoutPath, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(filePath, _currentStderrPath, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Schedule delayed archiving to allow file handles to be released
    /// </summary>
    private void ScheduleDelayedArchiving(int delaySeconds = 30)
    {
        if (!_config.ZipOlderThanDays.HasValue)
        {
            _logger.LogDebug("Skipping delayed archiving - ZipOlderThanDays not configured");
            return;
        }

        // Cancel any existing delayed archive timer
        if (_delayedArchiveTimer != null)
        {
            _logger.LogDebug("Cancelling existing delayed archive timer");
            _delayedArchiveTimer.Dispose();
        }

        // Schedule new delayed archiving
        var scheduledTime = DateTime.Now.AddSeconds(delaySeconds);
        _delayedArchiveTimer = new Timer(OnDelayedArchive, null, TimeSpan.FromSeconds(delaySeconds), Timeout.InfiniteTimeSpan);
        _logger.LogInformation("Scheduled delayed archiving in {DelaySeconds} seconds (at {ScheduledTime})", delaySeconds, scheduledTime);
    }

    /// <summary>
    /// Handle delayed archiving callback
    /// </summary>
    private void OnDelayedArchive(object? state)
    {
        try
        {
            _logger.LogInformation("Performing delayed archiving (timer fired)");
            ArchiveOldLogs();
            _logger.LogInformation("Delayed archiving completed");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during delayed archiving");
        }
        finally
        {
            // Clean up timer after execution
            try
            {
                _delayedArchiveTimer?.Dispose();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error disposing delayed archive timer");
            }
            finally
            {
                _delayedArchiveTimer = null;
            }
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
            var cutoffDate = DateTime.Now.AddDays(-_config.ZipOlderThanDays.Value).AddSeconds(-1);
            var currentBucket = !string.IsNullOrEmpty(_config.TimePattern)
                ? DateTime.Now.ToString(_config.TimePattern)
                : null;

            // Use multiple patterns to catch all log file variations
            var patterns = new[]
            {
                $"{_serviceName}.*.log",           // Basic: service.out.log, service.err.log
                $"{_serviceName}.*.*.log",         // Rolled: service.out.1.log, service.20241201.out.log
                $"{_serviceName}.*.*.*.log"        // Complex: service.20241201.out.1.log (if both time + size rolling)
            };

            var candidates = new List<FileInfo>();
            foreach (var pattern in patterns)
            {
                var files = Directory.GetFiles(_logDirectory, pattern)
                    .Select(f => new FileInfo(f))
                    .ToArray();
                candidates.AddRange(files);
            }

            // Remove duplicates (in case a file matches multiple patterns)
            var distinct = candidates.GroupBy(f => f.FullName).Select(g => g.First()).ToList();

            // Exclude currently active log files using tracked paths
            var currentStdoutFile = _currentStdoutPath;
            var currentStderrFile = _currentStderrPath ?? _currentStdoutPath;

            var oldFiles = distinct.Where(f =>
                    !string.Equals(f.FullName, currentStdoutFile, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(f.FullName, currentStderrFile, StringComparison.OrdinalIgnoreCase) &&
                    (
                        // Standard age-based inclusion
                        f.LastWriteTime < cutoffDate ||
                        // Immediate-roll inclusion when ZipOlderThanDays == 0 and we have a time bucket pattern
                        (_config.ZipOlderThanDays.GetValueOrDefault() == 0 && currentBucket != null &&
                         !f.Name.Contains(currentBucket, StringComparison.OrdinalIgnoreCase))
                    ))
                .ToList();

            _logger.LogInformation("Archive scan found {Count} candidate files, {OldCount} old files to archive", distinct.Count, oldFiles.Count);
            _logger.LogInformation("Current stdout: {Stdout}, stderr: {Stderr}", currentStdoutFile, currentStderrFile);
            _logger.LogInformation("Cutoff date: {CutoffDate}, Current bucket: {CurrentBucket}, ZipOlderThanDays: {ZipDays}", cutoffDate, currentBucket, _config.ZipOlderThanDays);

            foreach (var f in distinct)
            {
                var isOld = f.LastWriteTime < cutoffDate ||
                           (_config.ZipOlderThanDays.GetValueOrDefault() == 0 && currentBucket != null &&
                            !f.Name.Contains(currentBucket, StringComparison.OrdinalIgnoreCase));
                var isActive = string.Equals(f.FullName, currentStdoutFile, StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(f.FullName, currentStderrFile, StringComparison.OrdinalIgnoreCase);
                _logger.LogInformation("File: {File}, LastWrite: {LastWrite}, IsOld: {IsOld}, IsActive: {IsActive}, Size: {Size}", 
                    f.Name, f.LastWriteTime, isOld, isActive, f.Length);
            }

            if (!oldFiles.Any())
            {
                _logger.LogInformation("No old log files found for archiving");
                return;
            }

            // Zip per day (based on file's last write date), grouping by yyyyMMdd
            var dateFormat = _config.ZipDateFormat ?? "yyyyMMdd";
            var groups = oldFiles
                .GroupBy(f => f.LastWriteTime.ToString(dateFormat))
                .ToList();

            foreach (var g in groups)
            {
                var filesToAdd = g.ToList();
                if (filesToAdd.Count == 0) continue;

                var zipFileName = $"{_serviceName}.{g.Key}.zip";
                var zipPath = Path.Combine(_logDirectory, zipFileName);
                var addedAny = false;
                var filesToDelete = new List<string>();

                _logger.LogInformation("Processing zip group {DateKey} with {FileCount} files: {Files}", 
                    g.Key, filesToAdd.Count, string.Join(", ", filesToAdd.Select(f => f.Name)));

                try
                {
                    // Use Create mode for new files, Update mode for existing files
                    var zipMode = File.Exists(zipPath) ? ZipArchiveMode.Update : ZipArchiveMode.Create;
                    _logger.LogInformation("Opening zip {ZipPath} in {Mode} mode", zipPath, zipMode);
                    using var zip = ZipFile.Open(zipPath, zipMode);

                    foreach (var file in filesToAdd)
                    {
                        try
                        {
                            _logger.LogInformation("Processing file for archiving: {File}", file.FullName);

                            // Verify file still exists and is not currently being written to
                            if (!File.Exists(file.FullName))
                            {
                                _logger.LogWarning("File no longer exists, skipping: {File}", file.FullName);
                                continue;
                            }

                            // Check if this is a currently active log file (skip archiving active files)
                            if (IsCurrentlyActiveLogFile(file.FullName))
                            {
                                _logger.LogInformation("File is currently active, skipping: {File}", file.FullName);
                                continue;
                            }

                            // Check if entry already exists in zip to avoid duplicates (only for Update mode)
                            var entryName = file.Name;
                            _logger.LogInformation("[VERSION_CHECK_v2025_09_08_15_16] ZipMode is: {ZipMode}, checking duplicates for: {EntryName}", zipMode, entryName);
                            
                            if (zipMode == ZipArchiveMode.Update)
                            {
                                _logger.LogInformation("[VERSION_CHECK_v2025_09_08_15_16] In Update mode, checking for existing entries");
                                try
                                {
                                    if (zip.Entries.Any(e => e.Name.Equals(entryName, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        _logger.LogInformation("File already exists in zip, skipping: {File}", file.FullName);
                                        filesToDelete.Add(file.FullName); // Still mark for deletion
                                        continue;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "[VERSION_CHECK_v2025_09_08_15_16] Could not check zip entries in Update mode, proceeding with archiving: {File}", file.FullName);
                                }
                            }
                            else
                            {
                                _logger.LogInformation("[VERSION_CHECK_v2025_09_08_15_16] In Create mode, skipping duplicate check");
                            }

                            // Try to open file with appropriate sharing - if it fails, the file is locked
                            _logger.LogInformation("Opening file for reading: {File}", file.FullName);
                            using var src = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                            var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                            using var entryStream = entry.Open();
                            _logger.LogInformation("Copying {Bytes} bytes from {File} to zip", src.Length, file.FullName);
                            src.CopyTo(entryStream);
                            addedAny = true;
                            filesToDelete.Add(file.FullName);
                            
                            _logger.LogInformation("Successfully added file to zip: {File}", file.FullName);
                        }
                        catch (Exception exFile)
                        {
                            _logger.LogWarning(exFile, "Failed to archive file: {File}", file.FullName);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to open/create zip for group {DateKey}: {ZipPath}", g.Key, zipPath);
                    // Don't delete files if zip creation failed
                    filesToDelete.Clear();
                }

                // Only delete files that were successfully added to zip
                foreach (var fileToDelete in filesToDelete)
                {
                    try
                    {
                        // Skip deletion if file is currently active
                        if (IsCurrentlyActiveLogFile(fileToDelete))
                        {
                            _logger.LogDebug("Skipping deletion of active log file: {File}", fileToDelete);
                            continue;
                        }

                        // Try delete with retries and better error handling
                        var deleted = false;
                        Exception? lastException = null;
                        
                        for (int i = 0; i < 5 && !deleted; i++)
                        {
                            try 
                            { 
                                // Test if file can be opened for deletion first
                                using (var testStream = new FileStream(fileToDelete, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                                {
                                    // If we can open it with Delete sharing, deletion should work
                                }
                                
                                File.Delete(fileToDelete); 
                                deleted = true; 
                            }
                            catch (IOException ex) when (i < 4) 
                            { 
                                lastException = ex;
                                Thread.Sleep(500 + (i * 200)); // Progressive backoff
                            }
                            catch (UnauthorizedAccessException ex) when (i < 4)
                            {
                                lastException = ex;
                                Thread.Sleep(500 + (i * 200)); // Progressive backoff
                            }
                        }
                        
                        if (deleted)
                            _logger.LogDebug("Archived and deleted log file: {File}", fileToDelete);
                        else
                        {
                            _logger.LogWarning("Archived but could not delete log file after 5 attempts (in use by another process?): {File}. Last error: {Error}", 
                                fileToDelete, lastException?.Message ?? "Unknown");
                        }
                    }
                    catch (Exception exDelete)
                    {
                        _logger.LogWarning(exDelete, "Failed to delete archived file: {File}", fileToDelete);
                    }
                }

                // Remove empty zip if nothing was added
                try
                {
                    if (!addedAny && File.Exists(zipPath))
                    {
                        File.Delete(zipPath);
                        _logger.LogDebug("Deleted empty zip file: {ZipPath}", zipPath);
                    }
                }
                catch (Exception exDel)
                {
                    _logger.LogWarning(exDel, "Failed to delete empty zip {Zip}", zipFileName);
                }

                if (addedAny)
                    _logger.LogInformation("Archived {Count} files to {ZipFile}", filesToDelete.Count, zipFileName);
            }
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
        _delayedArchiveTimer?.Dispose();
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
