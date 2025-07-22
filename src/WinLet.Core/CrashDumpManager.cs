using System.Diagnostics;
using System.Runtime.InteropServices;
using System.IO.Compression;
using Microsoft.Extensions.Logging;

namespace WinLet.Core;

/// <summary>
/// Manages crash dump generation for monitored processes
/// </summary>
public class CrashDumpManager : IDisposable
{
    private readonly CrashDumpConfig _config;
    private readonly string _serviceName;
    private readonly ILogger<CrashDumpManager> _logger;
    private readonly string _dumpDirectory;
    private bool _disposed = false;

    public CrashDumpManager(CrashDumpConfig config, string serviceName, ILogger<CrashDumpManager> logger)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _serviceName = serviceName ?? throw new ArgumentNullException(nameof(serviceName));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        // Determine dump directory
        _dumpDirectory = !string.IsNullOrEmpty(_config.DumpPath) 
            ? _config.DumpPath 
            : Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? Environment.CurrentDirectory, "dumps");
        
        // Ensure dump directory exists
        Directory.CreateDirectory(_dumpDirectory);
        
        _logger.LogInformation("Crash dump manager initialized - Directory: {DumpDirectory}, Type: {DumpType}", 
            _dumpDirectory, _config.Type);
    }

    /// <summary>
    /// Generate a crash dump for the specified process
    /// </summary>
    public async Task<string?> GenerateCrashDumpAsync(Process process, string reason = "ProcessCrash")
    {
        if (!_config.Enabled)
        {
            _logger.LogDebug("Crash dump generation is disabled");
            return null;
        }

        if (process.HasExited)
        {
            _logger.LogWarning("Cannot generate crash dump for process {ProcessId} - process has already exited", process.Id);
            return null;
        }

        try
        {
            var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            var dumpFileName = $"{_serviceName}_{process.Id}_{reason}_{timestamp}.dmp";
            var dumpPath = Path.Combine(_dumpDirectory, dumpFileName);

            _logger.LogInformation("Generating crash dump for process {ProcessId} - Reason: {Reason}, File: {DumpFile}", 
                process.Id, reason, dumpFileName);

            // Generate the dump
            var success = await GenerateDumpAsync(process, dumpPath);
            
            if (success)
            {
                _logger.LogInformation("Crash dump generated successfully: {DumpFile} (Size: {Size:N0} bytes)", 
                    dumpFileName, new FileInfo(dumpPath).Length);

                // Compress if configured
                if (_config.CompressDumps)
                {
                    var compressedPath = await CompressDumpAsync(dumpPath);
                    if (!string.IsNullOrEmpty(compressedPath))
                    {
                        File.Delete(dumpPath); // Remove original
                        dumpPath = compressedPath;
                        _logger.LogInformation("Crash dump compressed: {CompressedFile}", Path.GetFileName(compressedPath));
                    }
                }

                // Clean up old dumps
                await CleanupOldDumpsAsync();

                return dumpPath;
            }
            else
            {
                _logger.LogError("Failed to generate crash dump for process {ProcessId}", process.Id);
                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating crash dump for process {ProcessId}", process.Id);
            return null;
        }
    }

    /// <summary>
    /// Generate a crash dump for a process by ID (for processes that have already exited)
    /// </summary>
    public async Task<string?> GenerateCrashDumpAsync(int processId, string reason = "PostMortem")
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return await GenerateCrashDumpAsync(process, reason);
        }
        catch (ArgumentException)
        {
            _logger.LogWarning("Cannot generate crash dump for process {ProcessId} - process not found", processId);
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating crash dump for process {ProcessId}", processId);
            return null;
        }
    }

    /// <summary>
    /// Generate the actual dump file using Windows API
    /// </summary>
    private async Task<bool> GenerateDumpAsync(Process process, string dumpPath)
    {
        return await Task.Run(() =>
        {
            try
            {
                var processHandle = process.Handle;
                var processId = process.Id;

                using var dumpFile = File.Create(dumpPath);
                var dumpType = GetMiniDumpType();

                var success = MiniDumpWriteDump(
                    processHandle,
                    (uint)processId,
                    dumpFile.SafeFileHandle.DangerousGetHandle(),
                    dumpType,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    IntPtr.Zero);

                if (!success)
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger.LogError("MiniDumpWriteDump failed with error code: {ErrorCode}", error);
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Exception in GenerateDumpAsync");
                return false;
            }
        });
    }

    /// <summary>
    /// Get the appropriate MiniDumpType based on configuration
    /// </summary>
    private MINIDUMP_TYPE GetMiniDumpType()
    {
        var dumpType = _config.Type switch
        {
            CrashDumpType.MiniDump => MINIDUMP_TYPE.MiniDumpNormal,
            CrashDumpType.MiniDumpWithData => MINIDUMP_TYPE.MiniDumpWithDataSegs,
            CrashDumpType.FullDump => MINIDUMP_TYPE.MiniDumpWithFullMemory,
            CrashDumpType.Custom => MINIDUMP_TYPE.MiniDumpWithHandleData | 
                                   MINIDUMP_TYPE.MiniDumpWithThreadInfo | 
                                   MINIDUMP_TYPE.MiniDumpWithProcessThreadData,
            _ => MINIDUMP_TYPE.MiniDumpNormal
        };

        // Add heap if configured
        if (_config.IncludeHeap && _config.Type != CrashDumpType.FullDump)
        {
            dumpType |= MINIDUMP_TYPE.MiniDumpWithPrivateReadWriteMemory;
        }

        return dumpType;
    }

    /// <summary>
    /// Compress a dump file using gzip
    /// </summary>
    private async Task<string?> CompressDumpAsync(string dumpPath)
    {
        try
        {
            var compressedPath = dumpPath + ".gz";
            
            using var originalFile = File.OpenRead(dumpPath);
            using var compressedFile = File.Create(compressedPath);
            using var gzip = new GZipStream(compressedFile, CompressionMode.Compress);
            
            await originalFile.CopyToAsync(gzip);
            
            var originalSize = new FileInfo(dumpPath).Length;
            var compressedSize = new FileInfo(compressedPath).Length;
            var compressionRatio = (1.0 - (double)compressedSize / originalSize) * 100;
            
            _logger.LogDebug("Dump compression: {OriginalSize:N0} -> {CompressedSize:N0} bytes ({Ratio:F1}% reduction)", 
                originalSize, compressedSize, compressionRatio);
            
            return compressedPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compress dump file: {DumpPath}", dumpPath);
            return null;
        }
    }

    /// <summary>
    /// Clean up old dump files based on configuration
    /// </summary>
    private async Task CleanupOldDumpsAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                var patterns = new[] { "*.dmp", "*.dmp.gz" };
                var allDumpFiles = new List<FileInfo>();

                foreach (var pattern in patterns)
                {
                    var files = Directory.GetFiles(_dumpDirectory, pattern)
                        .Select(f => new FileInfo(f))
                        .Where(f => f.Name.StartsWith(_serviceName + "_"))
                        .ToArray();
                    allDumpFiles.AddRange(files);
                }

                // Remove duplicates and sort by creation time
                var dumpFiles = allDumpFiles
                    .GroupBy(f => f.FullName)
                    .Select(g => g.First())
                    .OrderByDescending(f => f.CreationTime)
                    .ToList();

                // Clean up by count
                if (dumpFiles.Count > _config.MaxDumpFiles)
                {
                    var filesToDelete = dumpFiles.Skip(_config.MaxDumpFiles).ToList();
                    foreach (var file in filesToDelete)
                    {
                        try
                        {
                            file.Delete();
                            _logger.LogDebug("Deleted old dump file: {File}", file.Name);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to delete old dump file: {File}", file.FullName);
                        }
                    }
                }

                // Clean up by age
                var cutoffDate = DateTime.Now.AddDays(-_config.MaxAgeDays);
                var oldFiles = dumpFiles.Where(f => f.CreationTime < cutoffDate).ToList();
                
                foreach (var file in oldFiles)
                {
                    try
                    {
                        file.Delete();
                        _logger.LogDebug("Deleted aged dump file: {File}", file.Name);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to delete aged dump file: {File}", file.FullName);
                    }
                }

                if (dumpFiles.Count > _config.MaxDumpFiles || oldFiles.Count > 0)
                {
                    _logger.LogInformation("Cleaned up {Count} old dump files", 
                        Math.Max(dumpFiles.Count - _config.MaxDumpFiles, 0) + oldFiles.Count);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during dump file cleanup");
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
    }

    #region Windows API Declarations

    [DllImport("dbghelp.dll", SetLastError = true, PreserveSig = true)]
    private static extern bool MiniDumpWriteDump(
        IntPtr hProcess,
        uint processId,
        IntPtr hFile,
        MINIDUMP_TYPE dumpType,
        IntPtr exceptionParam,
        IntPtr userStreamParam,
        IntPtr callbackParam);

    [Flags]
    private enum MINIDUMP_TYPE : uint
    {
        MiniDumpNormal = 0x00000000,
        MiniDumpWithDataSegs = 0x00000001,
        MiniDumpWithFullMemory = 0x00000002,
        MiniDumpWithHandleData = 0x00000004,
        MiniDumpFilterMemory = 0x00000008,
        MiniDumpScanMemory = 0x00000010,
        MiniDumpWithUnloadedModules = 0x00000020,
        MiniDumpWithIndirectlyReferencedMemory = 0x00000040,
        MiniDumpFilterModulePaths = 0x00000080,
        MiniDumpWithProcessThreadData = 0x00000100,
        MiniDumpWithPrivateReadWriteMemory = 0x00000200,
        MiniDumpWithoutOptionalData = 0x00000400,
        MiniDumpWithFullMemoryInfo = 0x00000800,
        MiniDumpWithThreadInfo = 0x00001000,
        MiniDumpWithCodeSegs = 0x00002000,
        MiniDumpWithoutAuxiliaryState = 0x00004000,
        MiniDumpWithFullAuxiliaryState = 0x00008000,
        MiniDumpWithPrivateWriteCopyMemory = 0x00010000,
        MiniDumpIgnoreInaccessibleMemory = 0x00020000,
        MiniDumpValidTypeFlags = 0x0003ffff
    }

    #endregion
} 