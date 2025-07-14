# WinLet

**WinLet** is a modern Windows service manager designed to replace tools like WinSW and NSSM. It wraps any application (executables, scripts, Node.js, etc.) as a native Windows Service, using simple TOML configuration.

## Features

- **Modern CLI** – Intuitive command-line interface (System.CommandLine)
- **TOML Configuration** – Simple, readable config files
- **Auto-restart** – Flexible restart policies
- **Structured Logging** – File rotation, console output
- **Prometheus Metrics** – Built-in metrics server
- **Alerting** – Email, Slack, webhook notifications
- **Performance Monitoring** – Process, system, and custom metrics
- **Graceful Shutdown** – Proper process lifecycle management
- **Windows Native** – Deep integration with Windows Services & Event Log

## Quick Start

### 1. Build

```bash
dotnet build --configuration Release
```

### 2. Create a Service Config

Example (`my-service.toml`):

```toml
name = "my-web-app"
display_name = "My Web Application"
description = "A Node.js web application"

[process]
executable = "node"
arguments = "app.js"
working_directory = "C:\\Apps\\MyWebApp"

[restart]
policy = "OnFailure"
max_attempts = 3
```

### 3. Install & Manage

```bash
winlet install --config my-service.toml
winlet start --name my-web-app
winlet status --name my-web-app
winlet stop --name my-web-app
winlet uninstall --name my-web-app
```

## Configuration Reference

**Service Info**
```toml
name = "service-name"
display_name = "Display Name"
description = "Service description"
```

**Process**
```toml
[process]
executable = "path/to/executable"
arguments = "args"
working_directory = "C:\\work\\dir"
shutdown_timeout_seconds = 30

[process.environment]
VAR1 = "value1"
```

**Logging**
```toml
[logging]
level = "Information"
log_to_console = true
log_path = "C:\\Logs\\MyService"
mode = "append"
size_threshold_kb = 10240
keep_files = 8
```

**Restart Policy**
```toml
[restart]
policy = "OnFailure"  # Never, Always, OnFailure
delay_seconds = 5
max_attempts = 3
```

**Service Account**
```toml
[service_account]
username = "NT AUTHORITY\\NetworkService"
password = "SecurePassword"
allow_service_logon = true
```

**Metrics**
```toml
[metrics]
enabled = true
port = 9090
host = "localhost"
collection_interval_seconds = 15
```

**Alerting**
```toml
[alerting]
enabled = true

[alerting.slack]
webhook_url = "https://hooks.slack.com/services/..."
channel = "#alerts"

[alerting.email]
smtp_server = "smtp.company.com"
from_address = "winlet@company.com"
to_addresses = ["team@company.com"]

[[alerting.rules]]
name = "High Memory Usage"
metric = "winlet_process_memory_bytes"
condition = ">"
threshold = 1073741824
duration_seconds = 120
severity = "Warning"
```

**Health Checks (Planned)**
```toml
[health_check]
type = "Http"
endpoint = "http://localhost:3000/health"
interval_seconds = 30
```

## Examples

**Node.js**
```toml
name = "my-web-server"
[process]
executable = "node"
arguments = "server.js"
working_directory = "C:\\Apps\\MyWebServer"
[process.environment]
NODE_ENV = "production"
PORT = "3000"
[restart]
policy = "OnFailure"
```

**Python**
```toml
name = "data-processor"
[process]
executable = "python"
arguments = "processor.py --config prod.json"
working_directory = "C:\\Scripts\\DataProcessor"
[restart]
policy = "Always"
```

**.NET**
```toml
name = "background-worker"
[process]
executable = "C:\\Apps\\Worker\\Worker.exe"
arguments = "--environment Production"
working_directory = "C:\\Apps\\Worker"
[logging]
level = "Warning"
log_file = "C:\\Logs\\worker.log"
log_to_console = false
[restart]
policy = "OnFailure"
```

## Architecture

- **Core** – Config loading, process management, Windows service integration
- **CLI** – Command interface
- **Service Layer** – Background service/process runner
- **Config Loader** – TOML parsing and validation

## Status

- **Done:** TOML config, CLI, process runner, Windows service, logging, metrics, alerting, performance monitoring
- **In Progress:** Service-mode process management, log viewing, error handling
- **Planned:** Health checks, dependency management, web UI, PowerShell module

## Requirements

- Windows 10/Server 2016+
- .NET 8.0 Runtime
- Administrator privileges (for install)

## License

[MIT License](LICENSE)

## Comparison

| Feature                | WinLet | WinSW | NSSM |
|------------------------|--------|-------|------|
| Config Format          | TOML   | XML   | GUI/Registry |
| CLI                    | Modern | Basic | GUI   |
| Prometheus Metrics     | ✅     | ❌    | ❌    |
| Alerting               | ✅     | ❌    | ❌    |
| Performance Monitoring | ✅     | ❌    | ❌    |
| Health Checks          | Planned| ❌    | ❌    |
| Logging                | Advanced| Basic| Basic |
| Service Accounts       | Full   | Basic | Basic |

**Advantages:**  
- Built-in observability (metrics, alerting)  
- Modern config (TOML)  
- CLI-first, developer-friendly  
- Real-time metrics and notifications  