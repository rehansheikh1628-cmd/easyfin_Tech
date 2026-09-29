# ACCUFEX — Phase 23 Production E2E Verification Script
# Tests the live production instance running from the publish package.
# Uses curl.exe for reliable cross-platform TLS handling on Windows.
# ============================================================================

$ErrorActionPreference = "Stop"

$baseUrl = "https://localhost:5001"
$httpUrl = "http://localhost:5000"
$cookieFile = [System.IO.Path]::GetTempFileName()

$results = @()

function Record-Test([string]$name, [bool]$passed, [string]$details) {
    $script:results += [PSCustomObject]@{
        Test = $name
        Status = if ($passed) { "PASS" } else { "FAIL" }
        Details = $details
    }
    $color = if ($passed) { "Green" } else { "Red" }
    $tag = if ($passed) { "[PASS]" } else { "[FAIL]" }
    Write-Host "$tag $name : $details" -ForegroundColor $color
}

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " ACCUFEX - Phase 23 Production End-to-End Test Suite" -ForegroundColor Cyan
Write-Host " Target URL: $baseUrl" -ForegroundColor Cyan
Write-Host "=================================================================`n" -ForegroundColor Cyan

try {
    # ----------------------------------------------------------------------------
    # 1. Production Security and Health Check
    # ----------------------------------------------------------------------------
    Write-Host "--- 1. Production Health and Security Verification ---" -ForegroundColor Yellow

    # Database Health
    $healthJsonStr = curl.exe -k -s "$baseUrl/api/health/database"
    try {
        $healthJson = $healthJsonStr | ConvertFrom-Json
        $isHealthy = ($null -ne $healthJson) -and ($healthJson.status -eq "Healthy") -and ($healthJson.canConnect -eq $true)
        $hasLeak = ($null -ne $healthJson.server) -or ($null -ne $healthJson.database) -or ($null -ne $healthJson.tables)
        Record-Test "Production Health Check" ($isHealthy -and -not $hasLeak) "Status Healthy, canConnect=true, internal server leak=$hasLeak"
    } catch {
        Record-Test "Production Health Check" $false "Health check response invalid: $healthJsonStr"
    }

    # Swagger UI Disabled
    $swaggerBody = curl.exe -k -s "$baseUrl/swagger/index.html"
    $noSwaggerUi = -not ($swaggerBody -match "swagger-ui")
    Record-Test "Swagger UI Unavailable" ($noSwaggerUi) "Swagger UI is not accessible in production"

    # OpenAPI v1 Spec Disabled
    $openApiResp = curl.exe -k -s "$baseUrl/openapi/v1.json"
    $noOpenApi = -not ($openApiResp -match '"openapi"')
    Record-Test "OpenAPI Spec Unavailable" $noOpenApi "OpenAPI endpoint is disabled in production"

    # ----------------------------------------------------------------------------
    # 2. Authentication and Authorization Lifecycle
    # ----------------------------------------------------------------------------
    Write-Host "`n--- 2. Authentication and Authorization Lifecycle ---" -ForegroundColor Yellow

    # Protected Endpoint returns 401 without cookie
    $unauthCode = curl.exe -k -s -w "%{http_code}" -o NUL "$baseUrl/api/dashboard/stats"
    Record-Test "Protected Route 401 Without Auth" ($unauthCode -eq "401") "Returned HTTP $unauthCode Unauthorized"

    # Register New Production Test User
    $testEmail = "prod_user_" + (Get-Random) + "@accufex.test"
    $testPassword = "SecurePassword#2026!"

    $regJsonFile = [System.IO.Path]::GetTempFileName()
    $regPayload = @{
        Email = $testEmail
        Password = $testPassword
        FullName = "Production Verification User"
    } | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText($regJsonFile, $regPayload)

    $regResp = curl.exe -k -s -c $cookieFile -X POST "$baseUrl/api/auth/register" -H "Content-Type: application/json" --data-binary "@$regJsonFile"
    Remove-Item $regJsonFile -Force -ErrorAction SilentlyContinue

    $regJson = $null
    try { $regJson = $regResp | ConvertFrom-Json } catch {}

    $regSuccess = ($null -ne $regJson) -and ($regJson.success -eq $true)
    Record-Test "User Registration (/api/auth/register)" $regSuccess "Status OK, registered $testEmail"

    # Verify Authentication Cookie in cookie jar
    $cookieContent = [System.IO.File]::ReadAllText($cookieFile)
    $hasAuthCookie = $cookieContent -match "Accufex_Auth"
    Record-Test "Authentication Cookie Issued" $hasAuthCookie "Found Accufex_Auth cookie in jar"

    # Authenticated Identity Check (/api/auth/me)
    $meResp = curl.exe -k -s -b $cookieFile "$baseUrl/api/auth/me"
    $meJson = $null
    try { $meJson = $meResp | ConvertFrom-Json } catch {}
    $meValid = ($null -ne $meJson) -and ($null -ne $meJson.user) -and ($meJson.user.email -eq $testEmail)
    Record-Test "Identity Resolver (/api/auth/me)" $meValid "Authenticated identity confirmed for $($meJson.user.email)"

    # ----------------------------------------------------------------------------
    # 3. Dashboard Workspace Metrics
    # ----------------------------------------------------------------------------
    Write-Host "`n--- 3. Dashboard Workspace Metrics ---" -ForegroundColor Yellow

    $statsResp = curl.exe -k -s -b $cookieFile "$baseUrl/api/dashboard/stats"
    $statsJson = $null
    try { $statsJson = $statsResp | ConvertFrom-Json } catch {}
    $statsValid = ($null -ne $statsJson) -and ($null -ne $statsJson.totalStatements)
    Record-Test "Dashboard Stats (/api/dashboard/stats)" $statsValid "TotalStatements=$($statsJson.totalStatements), TotalTransactions=$($statsJson.totalTransactions)"

    $summaryResp = curl.exe -k -s -b $cookieFile "$baseUrl/api/dashboard/summary"
    $summaryJson = $null
    try { $summaryJson = $summaryResp | ConvertFrom-Json } catch {}
    $summaryValid = ($null -ne $summaryJson) -and ($null -ne $summaryJson.status)
    Record-Test "Dashboard Summary (/api/dashboard/summary)" $summaryValid "Status=$($summaryJson.status), ClientProfiles=$($summaryJson.clientProfiles)"

    # ----------------------------------------------------------------------------
    # 4. Direct SPA Route Fallbacks (Deep Linking)
    # ----------------------------------------------------------------------------
    Write-Host "`n--- 4. Direct SPA URLs and Deep Linking ---" -ForegroundColor Yellow

    $spaRoutes = @("/", "/features", "/how-it-works", "/supported-banks", "/pricing", "/login", "/signup", "/dashboard", "/files", "/converter", "/excel-to-tally", "/settings")

    foreach ($route in $spaRoutes) {
        $spaHtml = curl.exe -k -s "$baseUrl$route"
        $isHtml = ($spaHtml -match "<app-root>") -or ($spaHtml -match "accufex")
        Record-Test "Direct SPA Route: $route" $isHtml "HTTP 200 OK, rendered SPA index shell"
    }

    # ----------------------------------------------------------------------------
    # 5. Logout and Session Termination
    # ----------------------------------------------------------------------------
    Write-Host "`n--- 5. Session Termination ---" -ForegroundColor Yellow

    $logoutCode = curl.exe -k -s -b $cookieFile -c $cookieFile -w "%{http_code}" -X POST "$baseUrl/api/auth/logout" -o NUL
    $postLogoutCode = curl.exe -k -s -b $cookieFile -w "%{http_code}" -o NUL "$baseUrl/api/dashboard/stats"
    $logoutOk = ($logoutCode -eq "200") -and ($postLogoutCode -eq "401")
    Record-Test "User Logout & Invalidation" $logoutOk "Logout returned HTTP $logoutCode; subsequent protected API returned HTTP $postLogoutCode"

} finally {
    Remove-Item $cookieFile -Force -ErrorAction SilentlyContinue
}

Write-Host "`n=================================================================" -ForegroundColor Cyan
Write-Host " Phase 23 Automated E2E Verification Summary" -ForegroundColor Cyan
$passCount = ($results | Where-Object { $_.Status -eq "PASS" }).Count
$failCount = ($results | Where-Object { $_.Status -eq "FAIL" }).Count
Write-Host " Total Tests Run: $($results.Count)" -ForegroundColor White
Write-Host " Passed: $passCount" -ForegroundColor Green
Write-Host " Failed: $failCount" -ForegroundColor Red
Write-Host "=================================================================" -ForegroundColor Cyan
