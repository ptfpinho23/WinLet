using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
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
    /// <param name="sourceConfigPath">Path to the original config file</param>
    public async Task InstallServiceAsync(ServiceConfig config, string executablePath, string? sourceConfigPath = null)
    {
        _logger.LogInformation("Installing Windows service: {ServiceName}", config.Name);
        Console.WriteLine($"Checking if service '{config.Name}' already exists...");

        try
        {
            // Check if service already exists
            if (await ServiceExistsAsync(config.Name))
            {
                Console.WriteLine($"Service '{config.Name}' already exists!");
                throw new InvalidOperationException($"Service '{config.Name}' already exists");
            }

            Console.WriteLine($"Service name is available");
            Console.WriteLine($"Building service installation command...");

            // Build sc.exe command to create service
            var arguments = BuildCreateServiceArguments(config, executablePath, sourceConfigPath);
            
            Console.WriteLine($"Running: sc.exe create {arguments}");
            var result = await RunServiceControlCommand("create", arguments);
            
            Console.WriteLine($"sc.exe output: {result.Output}");
            Console.WriteLine($"sc.exe exit code: {result.ExitCode}");
            
            if (result.ExitCode != 0)
            {
                Console.WriteLine($"Service creation failed!");
                throw new InvalidOperationException($"Failed to create service: {result.Output}");
            }

            Console.WriteLine($"Service created successfully");
            

            if (!string.IsNullOrEmpty(config.Description))
            {
                Console.WriteLine($"Setting service description...");
                var descriptionResult = await RunServiceControlCommand("description", $"{config.Name} \"{config.Description}\"");
                
                if (descriptionResult.ExitCode != 0)
                {
                    Console.WriteLine($"Warning: Failed to set service description: {descriptionResult.Output}");
                    _logger.LogWarning("Failed to set service description for {ServiceName}: {Error}", config.Name, descriptionResult.Output);
                }
                else
                {
                    Console.WriteLine($"Service description set successfully");
                }
            }
            
            // Grant "Log on as a service" right if requested
            var account = config.ServiceAccount;
            if (account?.AllowServiceLogon == true &&
                account.Username is { Length: > 0 } username &&
                !IsBuiltInAccount(username))
            {
                Console.WriteLine($"Granting 'Log on as a service' right to '{username}'...");
                try
                {
                    GrantServiceLogonRight(username);
                    Console.WriteLine("'Log on as a service' right granted successfully");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Warning: Failed to grant 'Log on as a service' right: {ex.Message}");
                    _logger.LogWarning("Failed to grant SeServiceLogonRight to {Username}: {Error}", username, ex.Message);
                }
            }

            _logger.LogInformation("Service '{ServiceName}' installed successfully", config.Name);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Installation failed: {ex.Message}");
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

    private string BuildCreateServiceArguments(ServiceConfig config, string executablePath, string? sourceConfigPath = null)
    {
        // Pass the original config file path directly to the service
        if (string.IsNullOrEmpty(sourceConfigPath) || !File.Exists(sourceConfigPath))
        {
            throw new InvalidOperationException("Source config path is required and must exist to install the service.");
        }

        // Convert to absolute path to ensure service can always find it
        var absoluteConfigPath = Path.GetFullPath(sourceConfigPath);
        
        _logger.LogInformation("Service will use config file at: {ConfigPath}", absoluteConfigPath);
        
        var arguments = new List<string>
        {
            config.Name,
            $"binPath= \"{executablePath} --config-path \\\"{absoluteConfigPath}\\\"\"",
            $"DisplayName= \"{config.DisplayName}\"",
            "start= auto",
            "type= own"
        };

        // Add service account if configured
        var account = config.ServiceAccount;
        if (account?.Username is { Length: > 0 } username)
        {
            arguments.Add($"obj= \"{username}\"");

            // Built-in accounts (LocalSystem, LocalService, NetworkService) don't need a password
            if (!IsBuiltInAccount(username) && !string.IsNullOrEmpty(account.Password))
                arguments.Add($"password= \"{account.Password}\"");
        }

        // Note: Description and service logon right are applied after service creation

        return string.Join(" ", arguments);
    }

    private static readonly string[] BuiltInAccounts =
    [
        "localsystem", @".\localsystem", "system",
        @"nt authority\localservice", "localservice",
        @"nt authority\networkservice", "networkservice",
        @"nt authority\system"
    ];

    private static bool IsBuiltInAccount(string username) =>
        BuiltInAccounts.Contains(username.ToLowerInvariant().Trim());

    /// <summary>
    /// Grants the "Log on as a service" (SeServiceLogonRight) privilege to the specified account
    /// using the LSA policy API.
    /// </summary>
    private static void GrantServiceLogonRight(string username)
    {
        var sid = ResolveAccountSid(username);

        var systemName = new LsaUnicodeString();
        var objectAttributes = new LsaObjectAttributes();

        var status = NativeMethods.LsaOpenPolicy(ref systemName, ref objectAttributes,
            PolicyAccess.LookupNames | PolicyAccess.CreateAccount, out var policyHandle);
        ThrowIfLsaError(status, "LsaOpenPolicy");

        try
        {
            var right = new LsaUnicodeString("SeServiceLogonRight");
            var rights = new[] { right };
            status = NativeMethods.LsaAddAccountRights(policyHandle, sid, rights, (uint)rights.Length);
            ThrowIfLsaError(status, "LsaAddAccountRights");
        }
        finally
        {
            NativeMethods.LsaClose(policyHandle);
        }
    }

    private static byte[] ResolveAccountSid(string username)
    {
        var sid = new byte[256];
        var sidSize = sid.Length;
        var domainName = new System.Text.StringBuilder(256);
        var domainNameSize = domainName.Capacity;

        if (!NativeMethods.LookupAccountName(null, username, sid, ref sidSize, domainName, ref domainNameSize, out _))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not resolve account '{username}'");

        var result = new byte[sidSize];
        Array.Copy(sid, result, sidSize);
        return result;
    }

    private static void ThrowIfLsaError(uint ntStatus, string operation)
    {
        if (ntStatus == 0) return; // STATUS_SUCCESS
        var win32Error = NativeMethods.LsaNtStatusToWinError(ntStatus);
        throw new Win32Exception((int)win32Error, $"{operation} failed with NTSTATUS 0x{ntStatus:X8}");
    }

    [SupportedOSPlatform("windows")]
    private static class NativeMethods
    {
        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern bool LookupAccountName(
            string? systemName,
            string accountName,
            [Out] byte[] sid,
            ref int cbSid,
            System.Text.StringBuilder referencedDomainName,
            ref int cbReferencedDomainName,
            out int use);

        [DllImport("advapi32.dll")]
        internal static extern uint LsaOpenPolicy(
            ref LsaUnicodeString systemName,
            ref LsaObjectAttributes objectAttributes,
            PolicyAccess desiredAccess,
            out IntPtr policyHandle);

        [DllImport("advapi32.dll")]
        internal static extern uint LsaAddAccountRights(
            IntPtr policyHandle,
            [MarshalAs(UnmanagedType.LPArray)] byte[] accountSid,
            [MarshalAs(UnmanagedType.LPArray)] LsaUnicodeString[] userRights,
            uint countOfRights);

        [DllImport("advapi32.dll")]
        internal static extern uint LsaClose(IntPtr objectHandle);

        [DllImport("advapi32.dll")]
        internal static extern uint LsaNtStatusToWinError(uint status);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LsaObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public int Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LsaUnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Buffer;

        public LsaUnicodeString(string value)
        {
            Buffer = value;
            Length = (ushort)(value.Length * 2);
            MaximumLength = (ushort)(Length + 2);
        }
    }

    [Flags]
    private enum PolicyAccess : uint
    {
        LookupNames   = 0x00000800,
        CreateAccount = 0x00000010,
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