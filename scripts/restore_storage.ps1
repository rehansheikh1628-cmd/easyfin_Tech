# ACCUFEX - Application Storage Restore Automation
[CmdletBinding()]
param (
    [string]$BackupStoragePath = "",
    [string]$TargetStorageDir = "C:\inetpub\accufex\Storage",
    [string]$BackupRootDir = "C:\FileBackups\Accufex"
)

$ErrorActionPreference = "Stop"

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " ACCUFEX - Application Storage Restore Tool" -ForegroundColor Cyan
Write-Host " Target Storage: $TargetStorageDir" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

# If no specific backup specified, find the latest in BackupRootDir
if ([string]::IsNullOrWhiteSpace($BackupStoragePath)) {
    if (Test-Path $BackupRootDir) {
        $latest = Get-ChildItem -Path $BackupRootDir -Directory -Filter "Storage_*" | Sort-Object CreationTime -Descending | Select-Object -First 1
        if ($null -ne $latest) {
            $BackupStoragePath = $latest.FullName
            Write-Host "Selected latest storage backup: $BackupStoragePath" -ForegroundColor Yellow
        }
    }
}

if (-not (Test-Path $BackupStoragePath)) {
    Write-Error "Backup storage path not found: $BackupStoragePath"
}

if (-not (Test-Path $TargetStorageDir)) {
    New-Item -ItemType Directory -Path $TargetStorageDir -Force | Out-Null
}

Write-Host "`nRestoring statement files and audit sidecars using Robocopy..." -ForegroundColor Yellow
$robocopyProcess = Start-Process -FilePath "robocopy.exe" -ArgumentList "`"$BackupStoragePath`" `"$TargetStorageDir`" /E /R:2 /W:1 /NFL /NDL /NP" -Wait -PassThru -NoNewWindow
$exitCode = $robocopyProcess.ExitCode

if ($exitCode -le 7) {
    Write-Host "Storage files restored successfully to $TargetStorageDir!" -ForegroundColor Green
} else {
    Write-Error "Robocopy restore failed with exit code $exitCode."
}

Write-Host "`nVerifying storage directory structure..." -ForegroundColor Yellow
$stmtDir = Join-Path $TargetStorageDir "Statements"
$corrDir = Join-Path $TargetStorageDir "Statements\corrections"

Write-Host "Statements directory exists: $(Test-Path $stmtDir)" -ForegroundColor White
Write-Host "Corrections directory exists: $(Test-Path $corrDir)" -ForegroundColor White
Write-Host "=================================================================" -ForegroundColor Cyan
