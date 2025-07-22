# WinLet Configuration Guide

This guide covers all configuration options available in WinLet TOML files.

## Table of Contents

- [Basic Service Configuration](#basic-service-configuration)
- [Process Configuration](#process-configuration)
- [Logging Configuration](#logging-configuration)
- [Restart Policies](#restart-policies)
- [Health Checks](#health-checks)
- [Service Accounts](#service-accounts)
- [Crash Dump Generation](#crash-dump-generation)
- [Complete Examples](#complete-examples)

## Basic Service Configuration

### Required Fields

```toml
[service]
name = "my-service"           # Unique service name (Windows service identifier)
display_name = "My Service"   # Display name shown in Services panel
```

### Optional Fields

```toml
[service]
name = "my-service"
display_name = "My Service"
description = "A detailed description of what this service does"
```

## Process Configuration

### Basic Process Setup

```toml
[process]
executable = "node"                    # Executable to run
arguments = "server.js --port 3000"    # Command line arguments
working_directory = "C:\\Apps\\MyApp"  # Working directory (use double backslashes)
shutdown_timeout_seconds = 30          # Time to wait for graceful shutdown
```

### Environment Variables

```toml
[process.environment]
NODE_ENV = "production"
DATABASE_URL = "postgresql://localhost/mydb"
API_KEY = "your-secret-key"
PORT = "3000"
```

## Logging Configuration

### Basic Logging

```toml
[logging]
level = "Information"              # Trace, Debug, Information, Warning, Error, Critical
log_to_console = true             # Also log to console output
log_path = "C:\\Logs\\MyService"  # Directory for log files
separate_error_log = true         # Separate stderr into different file
```

### Log Rotation Modes

#### Append Mode (Default)
```toml
[logging]
mode = "Append"  # Just append to log files indefinitely
```

#### Reset Mode
```toml
[logging]
mode = "Reset"  # Truncate log files on service start
```

#### Size-Based Rotation
```toml
[logging]
mode = "RollBySize"
size_threshold_kb = 10240  # Roll when log reaches 10MB
keep_files = 8             # Keep 8 rolled files
```

#### Time-Based Rotation
```toml
[logging]
mode = "RollByTime"
time_pattern = "yyyyMMdd"           # Daily rotation pattern
auto_roll_at_time = "00:00:00"      # Roll at midnight
```

#### Combined Size and Time Rotation
```toml
[logging]
mode = "RollBySizeTime"
size_threshold_kb = 51200           # Roll at 50MB OR daily
time_pattern = "yyyyMMdd"
auto_roll_at_time = "02:00:00"      # Roll at 2 AM
keep_files = 14                     # Keep 14 days of logs
```

### Log Compression

```toml
[logging]
zip_older_than_days = 7            # Compress logs older than 7 days
zip_date_format = "yyyyMM"         # Monthly zip files
```

### Custom Log File Names

```toml
[logging]
stdout_log_file = "application.log"     # Custom stdout log name
stderr_log_file = "application.err.log" # Custom stderr log name
```

### Advanced Logging Example

```toml
[logging]
level = "Information"
log_path = "C:\\Logs\\MyWebApp"
mode = "RollBySizeTime"
size_threshold_kb = 20480        # 20MB
time_pattern = "yyyyMMdd"        # Daily
auto_roll_at_time = "01:00:00"   # 1 AM
keep_files = 30                  # 30 days
zip_older_than_days = 7          # Compress after 1 week
separate_error_log = true
```

## Restart Policies

### Never Restart
```toml
[restart]
policy = "Never"  # Don't restart if process exits
```

### Always Restart
```toml
[restart]
policy = "Always"
delay_seconds = 10       # Wait 10 seconds before restart
max_attempts = -1        # Unlimited attempts (-1)
```

### Restart on Failure Only
```toml
[restart]
policy = "OnFailure"     # Only restart on non-zero exit codes
delay_seconds = 5        # Wait 5 seconds
max_attempts = 3         # Try 3 times
window_seconds = 300     # Within 5-minute window
```

## Health Checks

### HTTP Health Check
```toml
[health_check]
type = "Http"
endpoint = "http://localhost:3000/health"
interval_seconds = 30      # Check every 30 seconds
timeout_seconds = 10       # 10 second timeout
failure_threshold = 3      # Mark unhealthy after 3 failures
```

### TCP Health Check
```toml
[health_check]
type = "Tcp"
endpoint = "localhost:3000"  # Host:port format
interval_seconds = 15
timeout_seconds = 5
failure_threshold = 2
```

### Process Health Check
```toml
[health_check]
type = "Process"             # Just check if process is running
interval_seconds = 60
failure_threshold = 1
```

## Service Accounts

### Local System (Default)
```toml
# No [service_account] section needed - runs as LocalSystem
```

### Local Service Account
```toml
[service_account]
username = "NT AUTHORITY\\LocalService"
```

### Network Service Account
```toml
[service_account]
username = "NT AUTHORITY\\NetworkService"
```

### Custom User Account
```toml
[service_account]
username = ".\\ServiceUser"          # Local account
# password = "SecurePassword123"     # Specify password
allow_service_logon = true           # Auto-grant logon as service right
prompt = "Console"                   # Prompt for password: Console or Dialog
```

### Domain Account
```toml
[service_account]
username = "DOMAIN\\ServiceUser"
# username = "ServiceUser@domain.com"  # Alternative format
allow_service_logon = true
prompt = "Dialog"                     # GUI password prompt
```

## Crash Dump Generation

WinLet can automatically generate crash dumps when monitored processes crash or exit unexpectedly. This is useful for debugging application failures under a production setting.

### Basic Crash Dump Configuration

```toml
[crash_dump]
enabled = true                          # Enable crash dump generation
dump_path = "C:\\Logs\\CrashDumps"     # Directory for dump files
```

### Crash Dump Types

```toml
[crash_dump]
enabled = true
type = "MiniDump"                      # MiniDump, MiniDumpWithData, FullDump, Custom
```

**Available Dump Types:**
- **`MiniDump`** - Minimal information (stack traces, loaded modules) - smallest size
- **`MiniDumpWithData`** - Includes data segments - medium size  
- **`FullDump`** - Complete memory dump - largest size
- **`Custom`** - Balanced dump with handle data, thread info, and process data

### Advanced Crash Dump Configuration

```toml
[crash_dump]
enabled = true
dump_path = "C:\\CrashDumps\\MyService"
type = "MiniDumpWithData"
max_dump_files = 10                    # Keep only 10 most recent dumps
max_age_days = 30                      # Delete dumps older than 30 days
include_heap = false                   # Include heap memory (increases size)
compress_dumps = true                  # Compress dump files with gzip
dump_on_exception = true               # Generate dumps on unhandled exceptions
```

### Crash Dump File Management

Crash dumps are automatically managed to prevent disk space issues:

- **File naming**: `{service-name}_{process-id}_{reason}_{timestamp}.dmp`
- **Compression**: Optional gzip compression (`.dmp.gz` extension)
- **Cleanup**: Automatic removal based on count and age limits
- **Location**: Stored in configured `dump_path` or service directory

### Example Dump Files
```
MyWebApp_1234_ExitCode1_20241201_143022.dmp.gz
MyWebApp_5678_ExitCode255_20241201_150315.dmp.gz
MyWebApp_9012_ProcessCrash_20241201_152010.dmp.gz
```

### Production Crash Dump Example

```toml
[crash_dump]
enabled = true
dump_path = "D:\\Logs\\Production\\CrashDumps"
type = "Custom"                        # Balanced information vs size
max_dump_files = 5                     # Keep recent crashes only
max_age_days = 7                       # Weekly cleanup
include_heap = false                   # Don't include heap for security
compress_dumps = true                  # Save disk space
dump_on_exception = true               # Catch all failure modes
```

### Development Crash Dump Example

```toml
[crash_dump]
enabled = true
dump_path = "C:\\Dev\\Dumps"
type = "FullDump"                      # Maximum debugging information
max_dump_files = 20                    # Keep more for analysis
max_age_days = 30                      # Longer retention
include_heap = true                    # Include all memory
compress_dumps = false                 # Faster generation, more disk space
dump_on_exception = true
```

### Security Considerations

- **Heap memory** may contain sensitive data (passwords, keys) - disable `include_heap` in production
- **Full dumps** contain complete process memory - handle securely
- **File permissions** - ensure dump directory has appropriate access controls
- **Cleanup** - configure appropriate retention policies for your environment

## Complete Examples

### Production Web Server

```toml
[service]
name = "my-web-api"
display_name = "My Web API"
description = "Production web API service with health monitoring"

[process]
executable = "node"
arguments = "server.js"
working_directory = "C:\\Apps\\MyWebAPI"
shutdown_timeout_seconds = 45

[process.environment]
NODE_ENV = "production"
PORT = "3000"
DATABASE_URL = "postgresql://db-server/myapi"
LOG_LEVEL = "info"

[logging]
level = "Information"
log_path = "C:\\Logs\\MyWebAPI"
mode = "RollBySizeTime"
size_threshold_kb = 25600        # 25MB
time_pattern = "yyyyMMdd"        # Daily
auto_roll_at_time = "02:00:00"   # 2 AM
keep_files = 14                  # 2 weeks
zip_older_than_days = 3          # Compress after 3 days
separate_error_log = true

[restart]
policy = "OnFailure"
delay_seconds = 10
max_attempts = 5
window_seconds = 600             # 10 minutes

[health_check]
type = "Http"
endpoint = "http://localhost:3000/health"
interval_seconds = 30
timeout_seconds = 15
failure_threshold = 3

[service_account]
username = "DOMAIN\\WebAPIService"
allow_service_logon = true
prompt = "Console"

[crash_dump]
enabled = true
dump_path = "C:\\Logs\\MyWebAPI\\CrashDumps"
type = "Custom"
max_dump_files = 3
max_age_days = 14
compress_dumps = true
dump_on_exception = true
```

### Background Data Processor

```toml
[service]
name = "data-processor"
display_name = "Data Processing Service"
description = "Processes incoming data files every hour"

[process]
executable = "python"
arguments = "-u processor.py"
working_directory = "C:\\Services\\DataProcessor"
shutdown_timeout_seconds = 120   # Long-running operations

[process.environment]
PYTHONUNBUFFERED = "1"
DATA_DIR = "C:\\Data\\Incoming"
OUTPUT_DIR = "C:\\Data\\Processed"
MAX_WORKERS = "4"

[logging]
level = "Debug"
log_path = "C:\\Logs\\DataProcessor"
mode = "RollByTime"
time_pattern = "yyyyMMdd"
auto_roll_at_time = "00:00:00"
keep_files = 30
zip_older_than_days = 7

[restart]
policy = "Always"
delay_seconds = 30
max_attempts = -1

[service_account]
username = "NT AUTHORITY\\NetworkService"
```

### Simple Development Service

```toml
[service]
name = "dev-app"
display_name = "Development Application"

[process]
executable = "dotnet"
arguments = "MyApp.dll"
working_directory = "C:\\Dev\\MyApp\\bin\\Release\\net8.0"

[logging]
level = "Debug"
log_path = "C:\\Temp\\DevLogs"
mode = "Reset"                   # Clear logs on each start
```

### High-Volume Service with Compression

```toml
[service]
name = "log-aggregator"
display_name = "Log Aggregation Service"

[process]
executable = "LogAggregator.exe"
working_directory = "C:\\Services\\LogAggregator"

[logging]
level = "Information"
log_path = "C:\\Logs\\LogAggregator"
mode = "RollBySize"
size_threshold_kb = 5120         # 5MB files
keep_files = 50                  # Keep many small files
zip_older_than_days = 1          # Compress daily
zip_date_format = "yyyyMMdd"     # Daily zip files

[restart]
policy = "OnFailure"
delay_seconds = 5
max_attempts = 10
window_seconds = 3600            # 1 hour window
```

## Tips and Best Practices

### Paths
- Always use double backslashes (`\\`) in Windows paths
- Use absolute paths for reliability
- Consider using environment variables for flexible deployments

### Logging
- Use `RollBySizeTime` for production services with predictable load
- Set `zip_older_than_days` to manage disk space
- Use `separate_error_log = true` to easily spot errors
- Consider log levels: `Debug` for development, `Information` for production

### Security
- Use dedicated service accounts for production
- Avoid storing passwords in TOML files - use `prompt` instead
- Grant minimal permissions to service accounts

### Performance
- Set appropriate `shutdown_timeout_seconds` for your application
- Use health checks for critical services
- Configure restart policies based on your service's reliability needs

### Monitoring
- Enable health checks for web services
- Use appropriate restart policies and attempt limits
- Monitor log file sizes and rotation

### Crash Dumps
- Use `Custom` type for balanced information vs file size in production
- Enable compression to save disk space
- Set appropriate retention policies (5-10 files, 7-30 days)
- Consider security implications of heap dumps in production environments
- Ensure crash dump directory has adequate disk space 