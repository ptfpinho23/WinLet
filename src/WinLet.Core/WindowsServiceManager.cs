using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using Microsoft.Extensions.Logging;

namespace WinLet.Core;

/// <summary>
/// Manages Windows service registration and operations
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsServiceManager
{
    private readonly ILogger<WindowsServiceManager> _logger;

    public WindowsServiceManager(ILogger<WindowsServiceManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Install a new Windows service
    /// </summary>
    /// <param name="config">Service configuration</param>
    /// <param name="executablePath">Path to the WinLet executable</param>
    public async Task InstallServiceAsync(ServiceConfig config, string executablePath)
    {
        _logger.LogInformation("Installing Windows service: {ServiceName}", config.Name);

        try
        {
            // Check if service already exists
            if (await ServiceExistsAsync(config.Name))
            {
                throw new InvalidOperationException($"Service '{config.Name}' already exists");
            }

            // Build sc.exe command to create service
            var arguments = BuildCreateServiceArguments(config, executablePath);
            
            var result = await RunServiceControlCommand("create", arguments);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to create service: {result.Output}");
            }

            _logger.LogInformation("Service '{ServiceName}' installed successfully", config.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to install service: {ServiceName}", config.Name);
            throw;
        }
    }

    /// <summary>
    /// Uninstall a Windows service
    /// </summary>
    /// <param name="serviceName">Name of the service to uninstall</param>
    public async Task UninstallServiceAsync(string serviceName)
    {
        _logger.LogInformation("Uninstalling Windows service: {ServiceName}", serviceName);

        try
        {
            // Check if service exists
            if (!await ServiceExistsAsync(serviceName))
            {
                _logger.LogWarning("Service '{ServiceName}' does not exist", serviceName);
                return;
            }

            // Stop service if running
            var status = await GetServiceStatusAsync(serviceName);
            if (status == ServiceControllerStatus.Running)
            {
                _logger.LogInformation("Stopping service before uninstall");
                await StopServiceAsync(serviceName);
            }

            // Delete service
            var result = await RunServiceControlCommand("delete", serviceName);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException($"Failed to delete service: {result.Output}");
            }

            _logger.LogInformation("Service '{ServiceName}' uninstalled successfully", serviceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to uninstall service: {ServiceName}", serviceName);
            throw;
        }
    }

    /// <summary>
    /// Start a Windows service
    /// </summary>
    /// <param name="serviceName">Name of the service to start</param>
    public async Task StartServiceAsync(string serviceName)
    {
        _logger.LogInformation("Starting Windows service: {ServiceName}", serviceName);

        try
        {
            using var service = new ServiceController(serviceName);
            
            if (service.Status == ServiceControllerStatus.Running)
            {
                _logger.LogInformation("Service '{ServiceName}' is already running", serviceName);
                return;
            }

            service.Start();
            service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            
            _logger.LogInformation("Service '{ServiceName}' started successfully", serviceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start service: {ServiceName}", serviceName);
            throw;
        }
    }

    /// <summary>
    /// Stop a Windows service
    /// </summary>
    /// <param name="serviceName">Name of the service to stop</param>
    public async Task StopServiceAsync(string serviceName)
    {
        _logger.LogInformation("Stopping Windows service: {ServiceName}", serviceName);

        try
        {
            using var service = new ServiceController(serviceName);
            
            if (service.Status == ServiceControllerStatus.Stopped)
            {
                _logger.LogInformation("Service '{ServiceName}' is already stopped", serviceName);
                return;
            }

            if (service.CanStop)
            {
                service.Stop();
                service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
            else
            {
                throw new InvalidOperationException($"Service '{serviceName}' cannot be stopped");
            }
            
            _logger.LogInformation("Service '{ServiceName}' stopped successfully", serviceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to stop service: {ServiceName}", serviceName);
            throw;
        }
    }

    /// <summary>
    /// Get the status of a Windows service
    /// </summary>
    /// <param name="serviceName">Name of the service</param>
    /// <returns>Service status</returns>
    public async Task<ServiceControllerStatus?> GetServiceStatusAsync(string serviceName)
    {
        try
        {
            using var service = new ServiceController(serviceName);
            return service.Status;
        }
        catch (InvalidOperationException)
        {
            // Service doesn't exist
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get service status: {ServiceName}", serviceName);
            throw;
        }
    }

    /// <summary>
    /// Check if a service exists
    /// </summary>
    /// <param name="serviceName">Name of the service</param>
    /// <returns>True if service exists</returns>
    public async Task<bool> ServiceExistsAsync(string serviceName)
    {
        var status = await GetServiceStatusAsync(serviceName);
        return status.HasValue;
    }

    private string BuildCreateServiceArguments(ServiceConfig config, string executablePath)
    {
        var arguments = new List<string>
        {
            config.Name,
            $"binPath= \"{executablePath} --service\"",
            $"DisplayName= \"{config.DisplayName}\"",
            "start= auto",
            "type= own"
        };

        if (!string.IsNullOrEmpty(config.Description))
        {
            // Description needs to be set separately using sc.exe description command
        }

        return string.Join(" ", arguments);
    }

    private async Task<(int ExitCode, string Output)> RunServiceControlCommand(string command, string arguments)
    {
        var processStartInfo = new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = $"{command} {arguments}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = processStartInfo };
        
        var outputBuilder = new List<string>();
        var errorBuilder = new List<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                outputBuilder.Add(e.Data);
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                errorBuilder.Add(e.Data);
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync();

        var output = string.Join(Environment.NewLine, outputBuilder);
        var error = string.Join(Environment.NewLine, errorBuilder);
        
        var allOutput = string.IsNullOrEmpty(error) ? output : $"{output}{Environment.NewLine}{error}";

        return (process.ExitCode, allOutput);
    }
}

/// <summary>
/// Service status information
/// </summary>
public class ServiceStatusInfo
{
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public ServiceControllerStatus Status { get; set; }
    public DateTime? StartTime { get; set; }
    public int? ProcessId { get; set; }

    public string StatusDescription => Status switch
    {
        ServiceControllerStatus.Running => "Running",
        ServiceControllerStatus.Stopped => "Stopped",
        ServiceControllerStatus.Paused => "Paused",
        ServiceControllerStatus.StartPending => "Starting",
        ServiceControllerStatus.StopPending => "Stopping",
        ServiceControllerStatus.ContinuePending => "Resuming",
        ServiceControllerStatus.PausePending => "Pausing",
        _ => "Unknown"
    };
} 