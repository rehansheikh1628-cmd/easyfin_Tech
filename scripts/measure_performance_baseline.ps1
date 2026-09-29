# ACCUFEX - Performance Baseline Measurement Script
$ErrorActionPreference = "Stop"
$BaseUrl = "https://localhost:5001"

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " ACCUFEX - Production Performance Baseline Metrics" -ForegroundColor Cyan
Write-Host " Target URL: $BaseUrl" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

$cookieFile = [System.IO.Path]::GetTempFileName()
$measurements = @()

function Measure-Endpoint([string]$name, [string]$url, [string]$method = "GET", [string]$postFile = "", [string]$cookieParam = "") {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    if ($method -eq "GET") {
        if ($cookieParam -ne "") {
            $null = curl.exe -k -s -b $cookieParam -o NUL $url
        } else {
            $null = curl.exe -k -s -o NUL $url
        }
    } elseif ($method -eq "POST") {
        if ($postFile -ne "") {
            $null = curl.exe -k -s -c $cookieParam -X POST $url -H "Content-Type: application/json" --data-binary "@$postFile" -o NUL
        }
    }
    $sw.Stop()
    $ms = $sw.ElapsedMilliseconds
    $script:measurements += [PSCustomObject]@{
        Operation = $name
        DurationMs = $ms
    }
    Write-Host "  - $name : ${ms} ms" -ForegroundColor Green
}

try {
    # 1. Database Health Check
    Measure-Endpoint "Database Health Check (/api/health/database)" "$BaseUrl/api/health/database"

    # 2. Registration and Session Establishment
    $regFile = [System.IO.Path]::GetTempFileName()
    $email = "perf_" + (Get-Random) + "@accufex.local"
    @{ Email = $email; Password = "Password#2026!"; FullName = "Perf User" } | ConvertTo-Json -Compress | Out-File -FilePath $regFile -Encoding utf8
    Measure-Endpoint "User Registration and Auth Cookie Issue" "$BaseUrl/api/auth/register" "POST" $regFile $cookieFile
    Remove-Item $regFile -Force -ErrorAction SilentlyContinue

    # 3. Identity Resolution
    Measure-Endpoint "Identity Resolver (/api/auth/me)" "$BaseUrl/api/auth/me" "GET" "" $cookieFile

    # 4. Dashboard Stats Query
    Measure-Endpoint "Dashboard Statistics (/api/dashboard/stats)" "$BaseUrl/api/dashboard/stats" "GET" "" $cookieFile

    # 5. Dashboard Summary Query
    Measure-Endpoint "Dashboard Summary (/api/dashboard/summary)" "$BaseUrl/api/dashboard/summary" "GET" "" $cookieFile

    # 6. Statements Listing
    Measure-Endpoint "Statements Listing (/api/statements)" "$BaseUrl/api/statements" "GET" "" $cookieFile

    # 7. Angular SPA Root Shell
    Measure-Endpoint "Angular SPA Root Shell (/)" "$BaseUrl/"

    # 8. Angular Client Deep Link (/excel-to-tally)
    Measure-Endpoint "Angular Deep Link (/excel-to-tally)" "$BaseUrl/excel-to-tally"

} finally {
    Remove-Item $cookieFile -Force -ErrorAction SilentlyContinue
}

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " Performance Baseline Captured Successfully" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan
