#!/usr/bin/env pwsh

Write-Host "Building WinLet (Windows) ..." -ForegroundColor Cyan

# Publish the CLI as a single, self-contained exe (service host is embedded by the CLI project)
Write-Host "Publishing WinLet.CLI (single-file, self-contained) ..." -ForegroundColor Yellow
dotnet publish src/WinLet.CLI/WinLet.CLI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:IncludeNativeLibrariesForSelfExtract=true
if ($LASTEXITCODE -ne 0) {
    Write-Host "Failed to publish WinLet.CLI" -ForegroundColor Red
    exit 1
}

# Copy the single executable to ./bin
Write-Host "Copying executable to bin folder..." -ForegroundColor Yellow
New-Item -ItemType Directory -Force -Path "bin" | Out-Null
$publishedExe = "src\WinLet.CLI\bin\Release\net8.0\win-x64\publish\WinLet.exe"
if (!(Test-Path $publishedExe)) {
    Write-Host "Error: Published executable not found at $publishedExe" -ForegroundColor Red
    exit 1
}
Copy-Item $publishedExe "bin\WinLet.exe" -Force

Write-Host "Build completed successfully!" -ForegroundColor Green
Write-Host "WinLet.exe is available at: .\bin\WinLet.exe" -ForegroundColor White