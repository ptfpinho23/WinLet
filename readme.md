<div align="center">

# WinLet

A simple Windows Service wrapper for any application.

</div>

## Installation and usage

1. Download the latest release [here](https://github.com/ptfpinho23/WinLet/releases)
2. Extract it to your preferred location
3. Create a TOML configuration file for your application (see examples below)
5. Install your service: `WinLet.exe install --config your-app.toml`
6. Start your service: `WinLet.exe start --name your-service-name`

**Note:** WinLet automatically handles UAC elevation when needed for service operations.

## Service Management

```powershell
# Check status
WinLet.exe status --name my-web-app

# View logs
WinLet.exe logs --name my-web-app

# Stop service
WinLet.exe stop --name my-web-app

# Uninstall service
WinLet.exe uninstall --name my-web-app
```


## Configuration Examples

**Node.js Web Server:**
```toml
[service]
name = "my-web-app"
display_name = "My Web Application"

[process]
executable = "node"
arguments = "server.js"
working_directory = "C:\\Apps\\MyWebApp"

[process.environment]
NODE_ENV = "production"
PORT = "3000"

[logging]
log_path = "C:\\Logs\\MyWebApp"

[restart]
policy = "OnFailure"
max_attempts = 3
```

**Python Script:**
```toml
[service]
name = "data-processor"
display_name = "Data Processing Service"

[process]
executable = "python"
arguments = "processor.py"
working_directory = "C:\\Scripts\\DataProcessor"

[logging]
log_path = "C:\\Logs\\DataProcessor"
```

**[Complete Configuration Guide](CONFIGURATION.md)** - Docs covering all configuration options including log rotation, service accounts, health checks, and other logging features.

## Logging

WinLet creates logs in your configured `log_path`:
- `service-name.out.log` - Application stdout with timestamps
- `service-name.err.log` - Application stderr with timestamps  
- `winlet.log` - Service management events

## Features

✅ Any executable as Windows Service  
✅ Auto-restart policies  
✅ Logging Management  
✅ Handle Env Variables 
✅ UAC elevation handling  
✅ TOML configuration  

## Development

### Requirements 
- Windows 10/Server 2016+
- .NET 8.0 SDK
- Administrator privileges (for service operations)

### Building from Source
```powershell
git clone https://github.com/ptfpinho23/WinLet.git
cd WinLet
.\build.ps1

# Clean build artifacts
.\clean.ps1
```

## Runtime Requirements
- Windows 10/Server 2016+
- Administrator privileges (for service operations)

## Roadmap

🚧 **Planned Features:**

### Monitoring & Observability
- [ ] Prometheus scrapable metrics (CPU, memory, restarts, uptime)
- [ ] Windows Performance Counters integration
- [ ] Structured logging with JSON output
- [ ] Health check endpoints

### Extensibility & Automation  
- [ ] Plugin system for custom rules and hooks
- [ ] Auto-discovery of common application types
- [ ] Template-based configuration generation
- [ ] Hot-reload configuration changes

### Enhanced Service Management
- [ ] Bulk service operations (start/stop multiple services)
- [ ] Service dependency management
- [ ] Rolling updates and blue-green deployments
- [ ] Backup and restore service configurations

### Dev Experience
- [ ] Web dashboard for service management
- [ ] PowerShell module with cmdlets
- [ ] VS Code extension for config editing
- [ ] Automatic log rotation and cleanup

## License

MIT License