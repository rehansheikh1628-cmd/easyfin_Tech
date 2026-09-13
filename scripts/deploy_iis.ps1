<#
================================================================================
 EasyFin Tech — IIS Production Deployment & Configuration Script
 Run in an Elevated PowerShell Prompt (Run as Administrator)
================================================================================
#>

[CmdletBinding()]
param (
    [string]$SiteName = "EasyFinTech",
    [string]$AppPoolName = "EasyFinTechPool",
    [string]$PhysicalPath = "C:\inetpub\easyfin-tech",
    [string]$DomainName = "localhost",
    [int]$HttpPort = 80,
    [int]$HttpsPort = 443,
    [string]$SourcePublishDir = "..\publish"
)

$ErrorActionPreference = "Stop"

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " EasyFin Tech — Production IIS Deployment & Setup Tool" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

# 1. Administrator Check
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Error "This script must be executed in an Elevated PowerShell window (Run as Administrator)."
}

# 2. Check and Enable IIS Features
Write-Host "`n[1/6] Checking IIS prerequisites..." -ForegroundColor Yellow
$iisFeature = Get-WindowsOptionalFeature -Online -FeatureName "IIS-WebServerRole" -ErrorAction SilentlyContinue
if ($iisFeature -and $iisFeature.State -ne "Enabled") {
    Write-Host "Enabling IIS Web Server Role..." -ForegroundColor Green
    Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole, IIS-WebServer, IIS-CommonHttpFeatures, IIS-StaticContent, IIS-DefaultDocument, IIS-HttpErrors, IIS-HttpRedirect, IIS-ApplicationDevelopment -All -NoRestart
} else {
    Write-Host "IIS Web Server Role is already enabled." -ForegroundColor Green
}

# 3. Verify ASP.NET Core Hosting Bundle (AspNetCoreModuleV2)
Write-Host "`n[2/6] Checking ASP.NET Core Module (AspNetCoreModuleV2)..." -ForegroundColor Yellow
$ancmPath32 = "$env:SystemRoot\System32\inetsrv\aspnetcore.dll"
$ancmPath64 = "$env:SystemRoot\SysWOW64\inetsrv\aspnetcore.dll"
if (-not (Test-Path $ancmPath32) -and -not (Test-Path $ancmPath64)) {
    Write-Warning "AspNetCoreModuleV2 was not detected in inetsrv!"
    Write-Warning "Please download and install the .NET 10 Hosting Bundle from https://dotnet.microsoft.com/download/dotnet/10.0"
} else {
    Write-Host "AspNetCoreModuleV2 is installed." -ForegroundColor Green
}

# 4. Prepare Target Production Directory
Write-Host "`n[3/6] Setting up production directory at '$PhysicalPath'..." -ForegroundColor Yellow
if (-not (Test-Path $PhysicalPath)) {
    New-Item -ItemType Directory -Path $PhysicalPath -Force | Out-Null
}

$storageStatements = Join-Path $PhysicalPath "Storage\Statements"
$storageCorrections = Join-Path $PhysicalPath "Storage\corrections"
$logsDir = Join-Path $PhysicalPath "logs"

foreach ($dir in @($storageStatements, $storageCorrections, $logsDir)) {
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
        Write-Host "Created folder: $dir" -ForegroundColor DarkGray
    }
}

# 5. Copy Published Files if Available
if (Test-Path $SourcePublishDir) {
    Write-Host "`nDeploying publish package from '$SourcePublishDir' to '$PhysicalPath'..." -ForegroundColor Green
    Copy-Item -Path "$SourcePublishDir\*" -Destination $PhysicalPath -Recurse -Force
    Write-Host "Package deployed successfully." -ForegroundColor Green
} else {
    Write-Host "Source publish directory '$SourcePublishDir' not found. Skipping file copy (deploy manually)." -ForegroundColor DarkYellow
}

# 6. Configure IIS AppPool and Site
Write-Host "`n[4/6] Configuring IIS Web Server & Application Pool..." -ForegroundColor Yellow
Import-Module WebAdministration -ErrorAction SilentlyContinue

# Create AppPool if not present
if (-not (Test-Path "IIS:\AppPools\$AppPoolName")) {
    Write-Host "Creating IIS Application Pool: $AppPoolName..." -ForegroundColor Green
    New-WebAppPool -Name $AppPoolName
}

# Configure AppPool for ASP.NET Core (.NET CLR = No Managed Code)
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "managedRuntimeVersion" -Value ""
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "enable32BitAppOnWin64" -Value $false
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "startMode" -Value "AlwaysRunning"
Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name "processModel.idleTimeout" -Value ([TimeSpan]::FromMinutes(0))

# Create or Update Web Site
if (Test-Path "IIS:\Sites\$SiteName") {
    Write-Host "Updating existing IIS Site '$SiteName'..." -ForegroundColor Green
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name "physicalPath" -Value $PhysicalPath
    Set-ItemProperty "IIS:\Sites\$SiteName" -Name "applicationPool" -Value $AppPoolName
} else {
    Write-Host "Creating new IIS Site '$SiteName'..." -ForegroundColor Green
    New-WebSite -Name $SiteName -Port $HttpPort -PhysicalPath $PhysicalPath -ApplicationPool $AppPoolName
}

# 7. Configure NTFS Permissions
Write-Host "`n[5/6] Setting NTFS Folder Permissions for AppPool Identity..." -ForegroundColor Yellow
$appPoolUser = "IIS AppPool\$AppPoolName"

# Grant Read & Execute on Root
$acl = Get-Acl $PhysicalPath
$ruleRead = New-Object System.Security.AccessControl.FileSystemAccessRule($appPoolUser, "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")
$acl.SetAccessRule($ruleRead)
Set-Acl $PhysicalPath $acl

# Grant Modify on Storage and logs
foreach ($writableDir in @($storageStatements, $storageCorrections, $logsDir)) {
    $dirAcl = Get-Acl $writableDir
    $ruleModify = New-Object System.Security.AccessControl.FileSystemAccessRule($appPoolUser, "Modify", "ContainerInherit,ObjectInherit", "None", "Allow")
    $dirAcl.SetAccessRule($ruleModify)
    Set-Acl $writableDir $dirAcl
    Write-Host "Granted Modify permissions on: $writableDir" -ForegroundColor DarkGray
}

# 8. Start AppPool and Site
Write-Host "`n[6/6] Starting Application Pool and Website..." -ForegroundColor Yellow
Restart-WebAppPool -Name $AppPoolName
Start-WebSite -Name $SiteName -ErrorAction SilentlyContinue

Write-Host "`n=================================================================" -ForegroundColor Cyan
Write-Host " IIS Production Deployment Configuration Completed!" -ForegroundColor Green
Write-Host " Website Path: $PhysicalPath" -ForegroundColor White
Write-Host " Application Pool: $AppPoolName (No Managed Code, AlwaysRunning)" -ForegroundColor White
Write-Host " Storage Root: $storageStatements (Writeable)" -ForegroundColor White
Write-Host " Corrections Root: $storageCorrections (Writeable)" -ForegroundColor White
Write-Host " Logs Root: $logsDir (Writeable)" -ForegroundColor White
Write-Host "`nNext Steps for Domain & SSL:" -ForegroundColor Yellow
Write-Host " 1. Set DNS A Record pointing your domain to this server's public IP." -ForegroundColor White
Write-Host " 2. In IIS Manager, add domain binding to port 80/443 for '$DomainName'." -ForegroundColor White
Write-Host " 3. Run win-acme (wacs.exe) to automatically issue & bind Let's Encrypt SSL certificate." -ForegroundColor White
Write-Host " 4. Initialize production database using: scripts\initialize_production_db.sql" -ForegroundColor White
Write-Host "=================================================================" -ForegroundColor Cyan
