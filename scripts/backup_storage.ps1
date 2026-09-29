# ACCUFEX - Application Storage Backup Automation (Robocopy Enterprise Standard)
[CmdletBinding()]
param (
    [string]$StorageSourceDir = "C:\inetpub\accufex\Storage",
    [string]$BackupTargetDir = "C:\FileBackups\Accufex",
    [int]$RetentionDays = 30
)

$ErrorActionPreference = "Stop"

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " ACCUFEX - Storage Backup Automation" -ForegroundColor Cyan
Write-Host " Source: $StorageSourceDir" -ForegroundColor Cyan
Write-Host " Target: $BackupTargetDir" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

if (-not (Test-Path $StorageSourceDir)) {
    if (Test-Path ".\publish\Storage") {
        $StorageSourceDir = (Resolve-Path ".\publish\Storage").Path
    } elseif (Test-Path ".\Accufex.Server\Storage") {
        $StorageSourceDir = (Resolve-Path ".\Accufex.Server\Storage").Path
    } else {
        Write-Error "Storage source directory not found: $StorageSourceDir"
    }
}

if (-not (Test-Path $BackupTargetDir)) {
    New-Item -ItemType Directory -Path $BackupTargetDir -Force | Out-Null
}

$timestamp = (Get-Date).ToString("yyyyMMdd_HHmmss")
$destinationPath = Join-Path $BackupTargetDir "Storage_$timestamp"

Write-Host "`nReplicating storage directories using Robocopy..." -ForegroundColor Yellow
$robocopyProcess = Start-Process -FilePath "robocopy.exe" -ArgumentList "`"$StorageSourceDir`" `"$destinationPath`" /E /R:2 /W:1 /NFL /NDL /NP" -Wait -PassThru -NoNewWindow
$exitCode = $robocopyProcess.ExitCode

# Robocopy exit codes 0-7 indicate success (0 = no change, 1 = files copied, 2 = extra files, 4 = mismatches)
if ($exitCode -le 7) {
    Write-Host "Storage backup completed successfully!" -ForegroundColor Green
    Write-Host "  Destination: $destinationPath" -ForegroundColor White
} else {
    Write-Error "Robocopy failed with exit code $exitCode."
}

Write-Host "`nCleaning up storage backups older than $RetentionDays days..." -ForegroundColor Yellow
$cutoff = (Get-Date).AddDays(-$RetentionDays)
Get-ChildItem -Path $BackupTargetDir -Directory -Filter "Storage_*" | Where-Object { $_.CreationTime -lt $cutoff } | ForEach-Object {
    Write-Host "Removing expired storage backup: $($_.Name)" -ForegroundColor DarkGray
    Remove-Item $_.FullName -Recurse -Force
}

Write-Host "Storage backup and retention policy execution complete." -ForegroundColor Green
