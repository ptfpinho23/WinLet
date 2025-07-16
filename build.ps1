#!/usr/bin/env pwsh

Write-Host "Building WinLet..." -ForegroundColor Cyan

Write-Host "Building WinLet.Core..." -ForegroundColor Yellow
dotnet build src/WinLet.Core/WinLet.Core.csproj -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to build WinLet.Core" -ForegroundColor Red
    exit 1
}

# Build and publish the service with dependencies
Write-Host "🔧 Publishing WinLet.Service..." -ForegroundColor Yellow
dotnet publish src/WinLet.Service/WinLet.Service.csproj -c Release -o src/WinLet.CLI/bin/Release/net8.0/service --self-contained false
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to publish WinLet.Service" -ForegroundColor Red
    exit 1
}

# Build the CLI
Write-Host "⚡ Building WinLet.CLI..." -ForegroundColor Yellow
dotnet build src/WinLet.CLI/WinLet.CLI.csproj -c Release
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to build WinLet.CLI" -ForegroundColor Red
    exit 1
}

Write-Host "Build completed successfully!" -ForegroundColor Green
Write-Host "WinLet.exe is available at: src\WinLet.CLI\bin\Release\net8.0\WinLet.exe" -ForegroundColor White 