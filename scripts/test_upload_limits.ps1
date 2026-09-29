# ACCUFEX - Upload Limits and Rejection Verification
$ErrorActionPreference = "Stop"
$BaseUrl = "https://localhost:5001"

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " Testing Upload Resource Protection (50MB Limit)" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

# 1. Unauthenticated upload -> 401 Unauthorized
$tempDummy = [System.IO.Path]::GetTempFileName()
[System.IO.File]::WriteAllText($tempDummy, "sample")
$unauthCode = curl.exe -k -s -w "%{http_code}" -o NUL -X POST "$BaseUrl/api/statements/upload" -F "file=@$tempDummy"
Remove-Item $tempDummy -Force -ErrorAction SilentlyContinue

Write-Host "Unauthenticated Upload Status: $unauthCode (Expected 401)" -ForegroundColor $(if ($unauthCode -eq "401") { "Green" } else { "Red" })

# 2. Authenticated user uploading 51MB file -> 413 Payload Too Large
$testEmail = "limit_test_" + (Get-Random) + "@accufex.local"
$testPass = "Password#2026!"
$cookieFile = [System.IO.Path]::GetTempFileName()
$regJson = [System.IO.Path]::GetTempFileName()

@{ Email = $testEmail; Password = $testPass; FullName = "Limit User" } | ConvertTo-Json -Compress | Out-File -FilePath $regJson -Encoding utf8
$null = curl.exe -k -s -c $cookieFile -X POST "$BaseUrl/api/auth/register" -H "Content-Type: application/json" --data-binary "@$regJson"
Remove-Item $regJson -Force -ErrorAction SilentlyContinue

# Create a 51MB file (53,477,376 bytes)
$oversizedFile = [System.IO.Path]::GetTempFileName() + ".pdf"
$stream = [System.IO.File]::Create($oversizedFile)
$stream.SetLength(53477376)
$stream.Close()

$oversizedCode = curl.exe -k -s -b $cookieFile -w "%{http_code}" -o NUL -X POST "$BaseUrl/api/statements/upload" -F "file=@$oversizedFile"
Remove-Item $oversizedFile -Force -ErrorAction SilentlyContinue
Remove-Item $cookieFile -Force -ErrorAction SilentlyContinue

Write-Host "Oversized (51MB) Upload Status: $oversizedCode (Expected 413)" -ForegroundColor $(if ($oversizedCode -eq "413") { "Green" } else { "Red" })
Write-Host "=================================================================" -ForegroundColor Cyan
