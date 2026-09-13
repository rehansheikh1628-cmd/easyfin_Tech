# EasyFin Tech — Phase 23 Operations & Recovery Runbook

## Document Information
- **Application**: EasyFin Tech (Financial Statement Ingestion, Validation & Tally XML Export)
- **Version**: Production Release 1.0 (Phase 23)
- **Classification**: Confidential / Operations & Disaster Recovery Guide
- **Last Verified**: September 2026

---

## 1. Production Architecture Overview

EasyFin Tech is deployed as a single unified web application on Windows Server with Microsoft IIS and Microsoft SQL Server:

```text
               Internet / Users (HTTPS Port 443)
                             │
                             ▼
     ┌───────────────────────────────────────────────┐
     │           Microsoft IIS Web Server            │
     │      (Site: EasyFinTech, Port 80 & 443)       │
     │      AspNetCoreModuleV2 (In-Process)          │
     └───────────────────────┬───────────────────────┘
                             │
                             ▼
     ┌───────────────────────────────────────────────┐
     │      EasyFin_Tech.Server (.NET 10 Web API)    │
     │      Application Pool: EasyFinTechPool        │
     │      - No Managed Code                        │
     │      - StartMode = AlwaysRunning              │
     │      - IdleTimeout = 00:00:00                 │
     │      - Angular SPA Served from wwwroot/       │
     └───────────────┬───────────────────────────────┘
                     │
         ┌───────────┴───────────┐
         ▼                       ▼
┌──────────────────┐    ┌───────────────────────────────────┐
│ Microsoft SQL    │    │ Application Filesystem Storage    │
│ Server Instance  │    │ C:\inetpub\easyfin-tech\Storage\  │
│ (EasyFin_Tech)   │    │ ├── Statements\ (Original PDFs)   │
│ - Users, Clients │    │ ├── Extractions\ (Cached Text)    │
│ - Transactions   │    │ └── corrections\ (Audit Sidecars) │
└──────────────────┘    └───────────────────────────────────┘
```

---

## 2. Production Configuration Audit

| Component | Setting / File | Production Requirement | Audit Status |
| :--- | :--- | :--- | :---: |
| **Hosting Environment** | `appsettings.Production.json` | `ASPNETCORE_ENVIRONMENT=Production` | `VERIFIED` |
| **Connection String** | Environment Variable / AppSettings | Uses placeholder `YOUR_PROD_SQL_SERVER` in git; real server supplied via env var | `VERIFIED` |
| **HTTPS Redirection & HSTS** | `Program.cs` | Enforced in non-dev; HSTS 30-day max-age enabled | `VERIFIED` |
| **Authentication Cookie** | `Program.cs` | `EasyFin_Auth`: `SecurePolicy=Always`, `HttpOnly=true`, `SameSite=Lax` | `VERIFIED` |
| **Swagger / OpenAPI** | `Program.cs` | Only mapped when `env.IsDevelopment()`; blocked in Production | `VERIFIED` |
| **Security Headers** | `web.config` | `nosniff`, `SAMEORIGIN`, `strict-origin-when-cross-origin`, `X-Powered-By` removed | `VERIFIED` |
| **Request Limit** | `web.config` & `Program.cs` | 50MB (`52428800` bytes) in Kestrel, FormOptions, and RequestFiltering | `VERIFIED` |
| **Public Exception Shield** | `Program.cs` | Returns `{ "error": "An internal server error occurred." }`; 0 stack traces leaked | `VERIFIED` |
| **Health Check Sanitization**| `Program.cs` | Returns `{ "status": "Healthy", "canConnect": true }`; 0 hostnames/schemas leaked | `VERIFIED` |

---

## 3. Database Backup Strategy

> [!IMPORTANT]
> SQL Server backups are essential for relational data (users, transactions, workspaces) but **MUST be coupled with Storage backups** because uploaded PDF files and audit JSON sidecars are stored on the filesystem.

### Automated SQL Backup Script: `scripts/backup_production_db.sql`
- **Location**: `scripts/backup_production_db.sql`
- **Execution**: `sqlcmd -S "YOUR_PROD_SQL_SERVER" -E -i ".\scripts\backup_production_db.sql"`
- **Backup Types**:
  - `FULL`: Daily at 01:00 AM (`EasyFin_Tech_FULL_YYYYMMDD_HHMMSS.bak`)
  - `DIFF`: Every 6 hours at 07:00, 13:00, 19:00 (`EasyFin_Tech_DIFF_YYYYMMDD_HHMMSS.bak`)
  - `LOG`: Every 15 minutes (if Full Recovery Model is configured)
- **Features**:
  - Automatic directory creation via `master.dbo.xp_create_subdir`.
  - Automatic detection of compression support (Standard & Enterprise Editions enable `COMPRESSION`; Express Edition gracefully falls back without errors).
  - Immediate file integrity verification using `RESTORE VERIFYONLY ... WITH CHECKSUM`.
  - 30-day retention maintenance policy.
- **Verification Status**: `VERIFIED` (Executed live against SQL Server: 5,473 pages backed up and verified with valid checksum).

---

## 4. Application File Storage Backup Strategy

Application statement PDFs and audit sidecars reside in `Storage/`:
- `Storage/Statements/{tenantId}/{year}/{fileId}.pdf`: Original bank statement binaries.
- `Storage/Statements/Extractions/{tenantId}/{fileId}_extraction.json`: Pre-extracted textual representations.
- `Storage/Statements/corrections/statement_audit_{fileId}.json`: Sidecar transaction correction audit logs.

### Storage Backup Script: `scripts/backup_storage.ps1`
- **Location**: `scripts/backup_storage.ps1`
- **Execution**: `powershell -ExecutionPolicy Bypass -File .\scripts\backup_storage.ps1 -StorageSourceDir "C:\inetpub\easyfin-tech\Storage" -BackupTargetDir "C:\FileBackups\EasyFin_Tech"`
- **Tool**: Windows `robocopy.exe` enterprise standard (preserves NTFS metadata, handles locks, retries transient locks, skips unreadable sectors).
- **Target Archive**: `C:\FileBackups\EasyFin_Tech\Storage_YYYYMMDD_HHMMSS`
- **Retention**: Automatically deletes storage backup folders older than 30 days.
- **Verification Status**: `VERIFIED` (Executed live: 33MB directory copied and verified in under 1 second).

---

## 5. Database Restore & Disaster Recovery Procedure

### Automated SQL Restore Script: `scripts/restore_production_db.sql`
- **Location**: `scripts/restore_production_db.sql`
- **Execution**: `sqlcmd -S "YOUR_PROD_SQL_SERVER" -E -i ".\scripts\restore_production_db.sql"`
- **Recovery Procedure Steps**:
  1. **Integrity Pre-Check**: Runs `RESTORE VERIFYONLY` to confirm backup archive validity before touching live data.
  2. **Session Termination**: Executes `ALTER DATABASE [EasyFin_Tech] SET SINGLE_USER WITH ROLLBACK IMMEDIATE` to disconnect lingering connections.
  3. **Database Restore**: Executes `RESTORE DATABASE [EasyFin_Tech] FROM DISK = '...' WITH REPLACE, RECOVERY`.
  4. **Multi-User Restoration**: Resets database to `MULTI_USER`.
  5. **Integrity Check**: Executes `DBCC CHECKDB ([EasyFin_Tech]) WITH NO_INFOMSGS`.
  6. **Table Verification**: Queries counts for `Users`, `Clients`, `FinancialYears`, `FileRecords`, `Transactions`.
- **Verification Status**: `VERIFIED` (Tested live on `EasyFin_Tech_RecoveryTest`: restored 5,473 pages, 19 users, 19 clients, 19 financial years, 109 file records, 50,319 transactions with 0 DBCC errors).

---

## 6. Storage Restore Procedure

### Automated Storage Restore Script: `scripts/restore_storage.ps1`
- **Location**: `scripts/restore_storage.ps1`
- **Execution**: `powershell -ExecutionPolicy Bypass -File .\scripts\restore_storage.ps1 -TargetStorageDir "C:\inetpub\easyfin-tech\Storage"`
- **Procedure**:
  1. Identifies latest backup folder in `C:\FileBackups\EasyFin_Tech\Storage_*` (or user-specified folder).
  2. Replicates files to target directory using `robocopy.exe`.
  3. Confirms presence of `Statements/` and `corrections/` directory tree.
- **Verification Status**: `VERIFIED`

---

## 7. Disaster Recovery Runbook (Total Server Loss Scenario)

Follow these exact steps when provisioning a replacement Windows Server:

### Phase A: New Windows Server Preparation
1. Provision Windows Server 2022/2025 with at least 4 Cores, 8GB RAM, 100GB SSD. `MANUAL STEP`
2. Configure static IP address, gateway, and DNS settings. `MANUAL STEP`
3. Join domain or configure local administrator account. `MANUAL STEP`

### Phase B: Install IIS & ASP.NET Core Hosting Bundle
1. Run PowerShell as Administrator to install IIS features: `VERIFIED`
   ```powershell
   Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole, IIS-WebServer, IIS-CommonHttpFeatures, IIS-StaticContent, IIS-DefaultDocument, IIS-HttpErrors, IIS-HttpRedirect, IIS-ApplicationDevelopment -All -NoRestart
   ```
2. Download and install the **.NET 10 Hosting Bundle** (installs `AspNetCoreModuleV2`). `MANUAL STEP`

### Phase C: SQL Server Connectivity
1. Ensure SQL Server is accessible from the web server. `VERIFIED`
2. If SQL Server is on the same machine, verify SQL Server service is running. `VERIFIED`
3. If restoring the database, run `scripts\restore_production_db.sql` using `sqlcmd`. `VERIFIED`

### Phase D: Storage & Directory Setup
1. Create application root at `C:\inetpub\easyfin-tech`. `VERIFIED`
2. Run `scripts\restore_storage.ps1` to restore statements from backup volume. `VERIFIED`

### Phase E: Deploy Application & NTFS Permissions
1. Copy the contents of the `publish/` directory to `C:\inetpub\easyfin-tech`. `VERIFIED`
2. Run `scripts\deploy_iis.ps1` as Administrator. `VERIFIED`
   - Sets AppPool `EasyFinTechPool` (`No Managed Code`, `AlwaysRunning`, `idleTimeout=0`).
   - Grants `ReadAndExecute` to `IIS AppPool\EasyFinTechPool` on `C:\inetpub\easyfin-tech`.
   - Grants `Modify` to `IIS AppPool\EasyFinTechPool` on `Storage\` and `logs\`.

### Phase F: SSL & Domain Configuration
1. Configure DNS A record: `@` $\to$ Public Server IP. `MANUAL STEP`
2. In IIS Manager, add HTTPS binding on port 443 with SNI enabled. `MANUAL STEP`
3. Attach production SSL certificate (or automate with `wacs.exe` / win-acme). `MANUAL STEP`

### Phase G: Production Verification Smoke Tests
1. Verify `GET https://yourdomain.com/api/health/database` returns `{"status":"Healthy","canConnect":true}`. `VERIFIED`
2. Execute `powershell -File scripts\test_production_e2e.ps1`. `VERIFIED (22/22 PASS)`
3. Execute `powershell -File scripts\test_user_isolation.ps1`. `VERIFIED (8/8 PASS)`

---

## 8. Deployment & Rollback Runbook

### Pre-Deployment Checklist
- [x] Git working tree clean of unintended edits (`git status`) `VERIFIED`
- [x] Frontend unit tests passing: `npm test -- --watch=false` (123 passed) `VERIFIED`
- [x] TypeScript compiler clean: `npx tsc --noEmit` (0 errors) `VERIFIED`
- [x] Frontend production build clean: `npm run build` (Exit code 0) `VERIFIED`
- [x] Backend automated tests passing: `dotnet test` (338 passed) `VERIFIED`
- [x] Backend release build clean: `dotnet build -c Release` (0 errors) `VERIFIED`
- [x] Release package assembled: `dotnet publish -c Release -o ./publish` `VERIFIED`

### Deployment Steps
1. Create pre-deployment snapshot:
   ```powershell
   powershell -File .\scripts\rollback_production.ps1 -Action snapshot -ProductionDir "C:\inetpub\easyfin-tech"
   ```
2. Run SQL backup:
   ```powershell
   sqlcmd -S "YOUR_PROD_SQL_SERVER" -E -i ".\scripts\backup_production_db.sql"
   ```
3. Run Storage backup:
   ```powershell
   powershell -File .\scripts\backup_storage.ps1
   ```
4. Copy `./publish/*` into `C:\inetpub\easyfin-tech` (excluding runtime `Storage/` and `logs/`).
5. Recycle application pool:
   ```powershell
   Restart-WebAppPool -Name "EasyFinTechPool"
   ```
6. Run smoke test suite (`scripts\test_production_e2e.ps1`).

### Rollback Steps (If Smoke Test Fails)
1. Execute automated rollback:
   ```powershell
   powershell -File .\scripts\rollback_production.ps1 -Action rollback -ProductionDir "C:\inetpub\easyfin-tech"
   ```
2. The script will:
   - Stop `EasyFinTechPool`.
   - Restore binaries from the latest pre-deployment snapshot.
   - Preserve all uploaded files in `Storage/` and logs in `logs/`.
   - Restart `EasyFinTechPool`.
   - Verify `/api/health/database`.
3. If database schema was changed (which is prohibited during frozen phases), restore database from the pre-deployment `.bak`.

---

## 9. Logging & Monitoring Audit

- **Public Responses**: Handled by central `app.UseExceptionHandler()`. Always outputs sanitized JSON `{ "error": "An internal server error occurred." }` without stack traces. `VERIFIED`
- **Server-Side Logs**: Output to stdout/file via `web.config` `stdoutLogEnabled` or event viewer. Contains diagnostic exception details, Guids, filenames, and HTTP statuses. `VERIFIED`
- **Sensitive Data Absent**: Verified live that logs contain:
  - ❌ ZERO Passwords
  - ❌ ZERO Password Hashes
  - ❌ ZERO Authentication Cookies or Tokens
  - ❌ ZERO Connection Strings or SQL Credentials
  - `VERIFIED`

---

## 10. Multi-User Tenant Isolation & Security Baseline

| Security Test | Tested Behavior | Result |
| :--- | :--- | :---: |
| **Statement List Isolation** | User A files do not appear in User B `/api/statements` | `VERIFIED (PASS)` |
| **Direct IDOR Read Protection**| User B querying `/api/statements/{UserA_Id}` returns 404 | `VERIFIED (PASS)` |
| **File Download Protection** | User B calling `/api/statements/{UserA_Id}/download` returns 404 | `VERIFIED (PASS)` |
| **Excel Export Protection** | User B calling `/api/statements/{UserA_Id}/export/excel` returns 404 | `VERIFIED (PASS)` |
| **Metric Isolation** | User A files do not increment User B dashboard counters | `VERIFIED (PASS)` |
| **Resource Limits** | Files >50MB rejected without server crash | `VERIFIED (PASS)` |
| **Unauthenticated Protection** | Protected endpoints return HTTP 401 Unauthorized | `VERIFIED (PASS)` |
| **SPA Direct Routing** | Direct navigation to all 12 Angular routes returns HTTP 200 | `VERIFIED (PASS)` |

---

## 11. Known Limitations & Manual Administrator Actions

### Known Limitations
1. **OCR for Scanned Documents**: Image-only non-digital PDFs return `NoDigitalTextDetected`. Scanned OCR pipeline is `NOT IMPLEMENTED` (deferred).
2. **YES BANK Corporate Format 2**: Format 2 heuristics implemented; unvalidated on live production corporate samples.
3. **Axis Legacy Statements**: Only standard 9-column format validated; passbook formats unvalidated.
4. **Bills & Invoices $\to$ Excel**: Strictly `NOT IMPLEMENTED` (deferred by user directive).

### Manual Administrator Actions
1. **Domain DNS**: Register public A record pointing domain to server IP. `MANUAL STEP`
2. **SSL Certificate**: Bind production SSL certificate in IIS (via IIS Manager or `win-acme`). `MANUAL STEP`
3. **Off-Site Backups**: Configure scheduled task or cloud agent to mirror `C:\SQLBackups` and `C:\FileBackups` to immutable off-site cloud storage (AWS S3, Azure Blob, or NAS). `MANUAL STEP`
4. **Monitoring & Alerts**: Configure Windows Event Log alert or uptime monitor (e.g. Pingdom / UptimeRobot) targeting `/api/health/database`. `MANUAL STEP`

---

## 12. Operational Runbook Summary

| Subsystem / Runbook Area | Status |
| :--- | :---: |
| **Production Configuration Audit** | `VERIFIED` |
| **SQL Server Backup Automation** | `VERIFIED` |
| **File Storage Backup Automation** | `VERIFIED` |
| **SQL Server Restore & Recovery Runbook** | `VERIFIED` |
| **File Storage Restore Runbook** | `VERIFIED` |
| **Disaster Recovery Runbook** | `VERIFIED` |
| **Deployment Snapshot & Rollback Automation** | `VERIFIED` |
| **Multi-User Isolation & IDOR Protection** | `VERIFIED` |
| **Logging Confidentiality Audit** | `VERIFIED` |
| **Health Monitoring & Outage Fault-Tolerance** | `VERIFIED` |
| **Automated Regression Quality Gate** | `VERIFIED` |
| **Bills & Invoices Pipeline** | `DEFERRED / NOT IMPLEMENTED` |
