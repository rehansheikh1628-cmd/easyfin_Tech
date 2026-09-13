# EasyFin Tech - Multi-User Data Isolation Verification Script
$ErrorActionPreference = "Stop"
$BaseUrl = "https://localhost:5001"

$results = @()
$cookieFileA = [System.IO.Path]::GetTempFileName()
$cookieFileB = [System.IO.Path]::GetTempFileName()

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
Write-Host " EasyFin Tech - Multi-User Isolation Verification Suite" -ForegroundColor Cyan
Write-Host " Target URL: $BaseUrl" -ForegroundColor Cyan
Write-Host "=================================================================" -ForegroundColor Cyan

try {
    # 1. Register User A
    $userA_Email = "usera_" + (Get-Random) + "@easyfintest.local"
    $userA_Password = "PasswordA#2026!"
    $tempFileA = [System.IO.Path]::GetTempFileName()
    $bodyA = @{ Email = $userA_Email; Password = $userA_Password; FullName = "User Alpha" } | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText($tempFileA, $bodyA)

    $regRespA = curl.exe -k -s -c $cookieFileA -X POST "$BaseUrl/api/auth/register" -H "Content-Type: application/json" --data-binary "@$tempFileA"
    Remove-Item $tempFileA -Force -ErrorAction SilentlyContinue

    $jsonA = $null
    try { $jsonA = $regRespA | ConvertFrom-Json } catch {}
    $regAOk = ($null -ne $jsonA) -and ($jsonA.success -eq $true)
    Record-Test "Register User A" $regAOk "Created User A ($userA_Email)"

    # 2. Register User B
    $userB_Email = "userb_" + (Get-Random) + "@easyfintest.local"
    $userB_Password = "PasswordB#2026!"
    $tempFileB = [System.IO.Path]::GetTempFileName()
    $bodyB = @{ Email = $userB_Email; Password = $userB_Password; FullName = "User Beta" } | ConvertTo-Json -Compress
    [System.IO.File]::WriteAllText($tempFileB, $bodyB)

    $regRespB = curl.exe -k -s -c $cookieFileB -X POST "$BaseUrl/api/auth/register" -H "Content-Type: application/json" --data-binary "@$tempFileB"
    Remove-Item $tempFileB -Force -ErrorAction SilentlyContinue

    $jsonB = $null
    try { $jsonB = $regRespB | ConvertFrom-Json } catch {}
    $regBOk = ($null -ne $jsonB) -and ($jsonB.success -eq $true)
    Record-Test "Register User B" $regBOk "Created User B ($userB_Email)"

    # 3. User A uploads a statement file
    $fileAId = $null
    $samplePdfPath = [System.IO.Path]::GetTempFileName() + ".pdf"

    try {
        $pdfHeader = "%PDF-1.4`n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj 2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj 3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R>>endobj 4 0 obj<</Length 44>>stream`nBT /F1 12 Tf 50 700 Td (User A Secret Statement) Tj ET`nendstream`nendobj`nxref`n0 5`n0000000000 65535 f `n0000000009 00000 n `n0000000058 00000 n `n0000000115 00000 n `n0000000206 00000 n `ntrailer<</Size 5/Root 1 0 R>>`nstartxref`n301`n%%EOF"
        [System.IO.File]::WriteAllText($samplePdfPath, $pdfHeader)

        $uploadOutput = curl.exe -k -s -b $cookieFileA -X POST "$BaseUrl/api/statements/upload" -F "file=@$samplePdfPath"
        $uploadJson = $null
        try { $uploadJson = $uploadOutput | ConvertFrom-Json } catch {}

        if ($null -ne $uploadJson -and $uploadJson.success -eq $true) {
            $fileAId = $uploadJson.fileId
            Record-Test "User A Statement Ingestion" $true "Uploaded successfully, FileId=$fileAId"
        } else {
            Record-Test "User A Statement Ingestion" $false "Upload failed: $uploadOutput"
        }
    } finally {
        Remove-Item $samplePdfPath -Force -ErrorAction SilentlyContinue
    }

    # 4. User B Queries Statement List -> User A's statement must NOT appear
    $listBStr = curl.exe -k -s -b $cookieFileB "$BaseUrl/api/statements"
    $listBJson = $null
    try { $listBJson = $listBStr | ConvertFrom-Json } catch {}
    
    $userAFileFoundInB = $false
    if ($null -ne $listBJson) {
        foreach ($stmt in $listBJson) {
            if ($stmt.id -eq $fileAId) {
                $userAFileFoundInB = $true
            }
        }
    }
    Record-Test "User B Statements List Isolation" (-not $userAFileFoundInB) "User A FileId ($fileAId) not visible in User B list (Count=$($listBJson.Count))"

    # 5. User B Direct GET /api/statements/{FileA_Id} -> Must return 404 (IDOR Protection)
    if ($null -ne $fileAId) {
        $idorCode = curl.exe -k -s -b $cookieFileB -w "%{http_code}" -o NUL "$BaseUrl/api/statements/$fileAId"
        Record-Test "User B Direct IDOR Read Blocked" ($idorCode -eq "404") "Returned HTTP $idorCode Not Found"

        # 6. User B Download Attempt /api/statements/{FileA_Id}/download -> Must return 404
        $downloadCode = curl.exe -k -s -b $cookieFileB -w "%{http_code}" -o NUL "$BaseUrl/api/statements/$fileAId/download"
        Record-Test "User B Direct File Download Blocked" ($downloadCode -eq "404") "Returned HTTP $downloadCode Not Found"

        # 7. User B Excel Export Attempt /api/statements/{FileA_Id}/export/excel -> Must return 404
        $exportCode = curl.exe -k -s -b $cookieFileB -w "%{http_code}" -o NUL "$BaseUrl/api/statements/$fileAId/export/excel"
        Record-Test "User B Statement Excel Export Blocked" ($exportCode -eq "404") "Returned HTTP $exportCode Not Found"
    }

    # 8. User B Dashboard Stats -> Must reflect only User B metrics (0 statements)
    $statsBStr = curl.exe -k -s -b $cookieFileB "$BaseUrl/api/dashboard/stats"
    $statsBJson = $null
    try { $statsBJson = $statsBStr | ConvertFrom-Json } catch {}
    $bStatementsCount = if ($null -ne $statsBJson) { $statsBJson.totalStatements } else { -1 }
    Record-Test "User B Dashboard Metric Isolation" ($bStatementsCount -eq 0) "User B totalStatements = $bStatementsCount (User A data not leaked into aggregate)"

} finally {
    Remove-Item $cookieFileA -Force -ErrorAction SilentlyContinue
    Remove-Item $cookieFileB -Force -ErrorAction SilentlyContinue
}

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host " Multi-User Data Isolation Results Summary" -ForegroundColor Cyan
$passCount = ($results | Where-Object { $_.Status -eq "PASS" }).Count
$failCount = ($results | Where-Object { $_.Status -eq "FAIL" }).Count
Write-Host " Total Tests: $($results.Count)" -ForegroundColor White
Write-Host " Passed: $passCount" -ForegroundColor Green
Write-Host " Failed: $failCount" -ForegroundColor Red
Write-Host "=================================================================" -ForegroundColor Cyan
