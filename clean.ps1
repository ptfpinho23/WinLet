#!/usr/bin/env pwsh

Write-Host "Cleaning WinLet build artifacts..." -ForegroundColor Cyan

# Remove the main bin folder
if (Test-Path "bin") {
    Write-Host "Removing bin folder..." -ForegroundColor Yellow
    Remove-Item "bin" -Recurse -Force
}

# Clean dotnet projects
Write-Host "Running dotnet clean..." -ForegroundColor Yellow
dotnet clean WinLet.sln -c Release

# Remove obj and bin folders from all projects
Write-Host "Removing obj and bin folders..." -ForegroundColor Yellow
Get-ChildItem -Path "src" -Recurse -Directory | Where-Object { $_.Name -in @("bin", "obj") } | ForEach-Object {
    Write-Host "  Removing $($_.FullName)" -ForegroundColor Gray
    Remove-Item $_.FullName -Recurse -Force
}

# Remove any published outputs
$publishDirs = @(
    "src\WinLet.CLI\bin\Release\net8.0\win-x64\publish",
    "src\WinLet.CLI\bin\Release\net8.0\service"
)

foreach ($dir in $publishDirs) {
    if (Test-Path $dir) {
        Write-Host "Removing $dir..." -ForegroundColor Yellow
        Remove-Item $dir -Recurse -Force
    }
}

# Remove any log files in test-output
if (Test-Path "test-output") {
    Write-Host "Cleaning test-output..." -ForegroundColor Yellow
    Get-ChildItem "test-output" -File | Remove-Item -Force
}

Write-Host "Clean completed successfully!" -ForegroundColor Green
Write-Host "Ready for a fresh build." -ForegroundColor White 