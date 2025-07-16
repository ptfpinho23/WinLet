using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace WinLet.Core;

/// <summary>
/// Helper class for handling User Access Control (UAC) elevation on Windows
/// </summary>
[SupportedOSPlatform("windows")]
public static class UacHelper
{
    /// <summary>
    /// Check if the current process is running with administrator privileges
    /// </summary>
    /// <returns>True if running as administrator</returns>
    public static bool IsRunningAsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Check if UAC elevation is required and restart the process with elevation if needed
    /// </summary>
    /// <param name="args">Command line arguments to pass to the elevated process</param>
    /// <returns>True if elevation was attempted (process will restart), False if already elevated</returns>
    public static bool ElevateIfRequired(string[] args)
    {
        if (IsRunningAsAdministrator())
        {
            return false; // Already running as admin
        }

        try
        {
            // Get the current executable path
            var currentProcess = Environment.ProcessPath;
            if (string.IsNullOrEmpty(currentProcess))
            {
                throw new InvalidOperationException("Could not determine current process path");
            }

            Console.WriteLine("Administrator privileges required.");
            Console.WriteLine("   Requesting UAC elevation...");
            
            // Properly escape arguments for command line
            var escapedArgs = args.Select(arg => 
            {
                // If argument contains spaces or special characters, wrap in quotes
                if (arg.Contains(' ') || arg.Contains('"') || arg.Contains('\\'))
                {
                    return $"\"{arg.Replace("\"", "\\\"")}\"";
                }
                return arg;
            });
            
            var arguments = string.Join(" ", escapedArgs);
            
            // Create process start info with runas verb for UAC elevation
            var startInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{currentProcess}\" {arguments} & pause & exit\"",
                UseShellExecute = true,
                Verb = "runas", // This triggers UAC elevation
                WindowStyle = ProcessWindowStyle.Normal
            };
            
            // Start the elevated process
            using var elevatedProcess = Process.Start(startInfo);
            
            if (elevatedProcess != null)
            {
                // Wait for the elevated process to complete
                elevatedProcess.WaitForExit();
                
                // Show completion message
                if (elevatedProcess.ExitCode == 0)
                {
                    Console.WriteLine("Operation completed successfully!");
                }
                else
                {
                    Console.WriteLine($"Operation failed (exit code: {elevatedProcess.ExitCode})");
                }
                
                Environment.Exit(elevatedProcess.ExitCode);
            }
            else
            {
                Console.WriteLine("Failed to start elevated process");
                Environment.Exit(1);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // User cancelled the UAC prompt
            Console.WriteLine("Operation cancelled by user");
            Environment.Exit(1);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            // Other Win32 errors
            Console.WriteLine($"Elevation failed: {ex.Message}");
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to elevate privileges: {ex.Message}");
            Environment.Exit(1);
        }

        return true; // This line should never be reached
    }

    /// <summary>
    /// Check if the operation requires administrator privileges
    /// </summary>
    /// <param name="operation">The operation being performed</param>
    /// <returns>True if the operation requires admin privileges</returns>
    public static bool RequiresAdminPrivileges(string operation)
    {
        var adminOperations = new[] { "install", "uninstall", "start", "stop" };
        return adminOperations.Contains(operation.ToLowerInvariant());
    }

    /// <summary>
    /// Display a user-friendly message about privilege requirements
    /// </summary>
    /// <param name="operation">The operation that requires privileges</param>
    public static void ShowPrivilegeRequirementMessage(string operation)
    {
        Console.WriteLine($"The '{operation}' operation requires administrator privileges.");
        Console.WriteLine("   You will be prompted to elevate permissions.");
        Console.WriteLine();
    }
} 