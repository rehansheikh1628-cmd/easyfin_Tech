# EasyFin Tech — Production Configuration, Secrets & Environment Guide

## Document Overview
- **Application**: EasyFin Tech (Financial Statement Ingestion, Validation & Tally XML Export)
- **Target Platform**: ASP.NET Core 10.0 Web API + Angular 22.1.5 Client SPA
- **Classification**: Confidential / Production Deployment & Hardening Manual
- **Status**: Production Release Ready

---

## 1. Environment Separation Architecture

EasyFin Tech enforces strict configuration separation between **Development** and **Production**:

| Component | Development Environment | Production Environment |
| :--- | :--- | :--- |
| **`ASPNETCORE_ENVIRONMENT`** | `Development` | `Production` |
| **SQL Database** | Local SQL Server Express (`localhost\SQLEXPRESS`) | Managed SQL Server / Azure SQL (`Encrypt=True;TrustServerCertificate=False;`) |
| **Connection String Source** | `appsettings.Development.json` | Environment Variable: `ConnectionStrings__DefaultConnection` / Key Vault |
| **CORS Policy** | Permits `http://localhost:64991`, `https://localhost:64991` with credentials | Strict explicitly configured origins from `Cors:AllowedOrigins` |
| **Cookie Security** | `SecurePolicy = CookieSecurePolicy.SameAsRequest` | `SecurePolicy = CookieSecurePolicy.Always` (strictly HTTPS only) |
| **Error Handling** | Detailed developer diagnostics in Swagger / JSON | Sanitized RFC 7807 problem details / generic JSON (zero stack traces or internal paths) |
| **Health Check (`/api/health/database`)** | Returns server name, database name, and entity counts | Returns sanitized `{ "status": "Healthy", "canConnect": true }` |
| **API Documentation** | OpenAPI `/openapi/v1.json` & Swagger UI `/swagger` | Disabled in production |
| **Security Headers** | Emitted | Emitted (`nosniff`, `DENY`, `strict-origin-when-cross-origin`, `X-XSS-Protection: 0`, `Permissions-Policy`) |
| **Forwarded Headers** | Inactive | Active (`XForwardedFor | XForwardedProto` with cleared IP network filters) |
| **Angular Client Build** | Development mode with sourcemaps (`sourceMap: true`) | Optimized, minified, license-extracted (`fileReplacements` to `environment.prod.ts`) |

---

## 2. Required Production Environment Variables & Secrets

In production, **NEVER** store real credentials or secrets in `appsettings.json` or source control. Supply them via your hosting platform's secure environment variable injector, Azure Key Vault, or Docker secret store.

### Configuration Variable Mapping

```ini
# ==============================================================================
# DATABASE CONFIGURATION (MANDATORY)
# ==============================================================================
# Must point to an encrypted SQL Server instance with TrustServerCertificate=False.
# NEVER commit credentials into source control.
ConnectionStrings__DefaultConnection=Server=sql.easyfin.tech,1433;Database=EasyFin_Tech;User Id=easyfin_app_user;Password=<STRONG_SECRET_PASSWORD>;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;

# ==============================================================================
# CORS ALLOWED ORIGINS (MANDATORY)
# ==============================================================================
# Semicolon or indexed array of allowed frontend HTTPS origins.
# Wildcard '*' is strictly rejected by ConfigurationValidator when cookie credentials are used.
Cors__AllowedOrigins__0=https://app.easyfin.tech
Cors__AllowedOrigins__1=https://easyfin.tech

# ==============================================================================
# FILE STORAGE CONFIGURATION
# ==============================================================================
# "Local" for local filesystem outside web root, or "AzureBlob" for cloud storage.
FileUpload__StorageProvider=Local
FileUpload__StorageRoot=Storage/Statements

# If using Azure Blob Storage:
# FileUpload__StorageProvider=AzureBlob
# FileUpload__AzureBlob__ConnectionString=DefaultEndpointsProtocol=https;AccountName=easyfinstorage;AccountKey=<ACCOUNT_KEY>;EndpointSuffix=core.windows.net
# FileUpload__AzureBlob__ContainerName=statements

# ==============================================================================
# BACKGROUND PROCESSING WORKER CONFIGURATION
# ==============================================================================
# Worker concurrency (number of parallel statements processed) and queue depth
BackgroundProcessing__MaxConcurrentJobs=2
BackgroundProcessing__QueueCapacity=100
BackgroundProcessing__JobTimeoutMinutes=15

# ==============================================================================
# HOSTING & REVERSE PROXY CONFIGURATION
# ==============================================================================
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://+:5000;https://+:5001
AllowedHosts=*
```

---

## 3. Database Security Guidelines

1. **Least-Privilege Database Account**:
   - Create a dedicated SQL user (`easyfin_app_user`) granted only `SELECT`, `INSERT`, `UPDATE`, and `DELETE` on the `EasyFin_Tech` database tables.
   - Do NOT use `sa` or `db_owner` in production.
   - Prohibit `DROP TABLE`, `ALTER TABLE`, and DDL privileges to this service account.

2. **TLS / SSL Connection Encryption**:
   - Enforce `Encrypt=True;TrustServerCertificate=False;` in the connection string.
   - Ensure the SQL Server presents a trusted CA-signed SSL/TLS certificate.

3. **Firewall & Network Hardening**:
   - Only allow connections from the application server private IP address or Azure Virtual Network.
   - Block public Internet access to SQL Server port 1433.

4. **Connection Resilience**:
   - EF Core is configured with `EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: 30s)` and `CommandTimeout(60s)` to automatically handle transient network blips and failovers without throwing unhandled exceptions.

---

## 4. File Storage Hardening

1. **Local Filesystem Storage (Default)**:
   - Statements are stored in `Storage/Statements/` completely outside `wwwroot/`.
   - Directory hierarchy: `Storage/Statements/tenants/{userId}/clients/{clientId}/statements/{year}/{fileId}.pdf`.
   - File IDs are cryptographically random GUIDs; original customer filenames are never used on disk.
   - `ValidateWithinRoot` enforces path traversal protection, throwing `UnauthorizedAccessException` on any path tampering (`../`).
   - File streams are served exclusively via authorized API streaming endpoints (`GET /api/statements/{id}/download`).

2. **Azure Blob Storage**:
   - When configured, blobs must be stored in a private container with all public anonymous access disabled.
   - Blobs are partitioned by tenant prefix: `tenants/{userId}/clients/{clientId}/{year}/{fileId}.pdf`.
   - Prefer Managed Identity / Microsoft Entra authentication over connection strings with shared keys.

---

## 5. Reverse Proxy & HTTPS Redirection

When hosting behind a reverse proxy (IIS, Azure App Service, Nginx, Cloudflare, or AWS ALB):

1. **Forwarded Headers Middleware**:
   - `Program.cs` activates `UseForwardedHeaders()` configured for `X-Forwarded-For` and `X-Forwarded-Proto`.
   - `KnownIPNetworks.Clear()` and `KnownProxies.Clear()` ensure proxy headers are accepted across cloud load balancers.

2. **HSTS & HTTPS Redirection**:
   - In Production, `UseHsts()` emits `Strict-Transport-Security: max-age=2592000` (30 days).
   - `UseHttpsRedirection()` forces HTTP requests to redirect to HTTPS.

3. **Security Headers**:
   Every response includes:
   - `X-Content-Type-Options: nosniff`
   - `X-Frame-Options: DENY`
   - `Referrer-Policy: strict-origin-when-cross-origin`
   - `X-XSS-Protection: 0`
   - `Permissions-Policy: camera=(), microphone=(), geolocation=()`

---

## 6. Angular Client Production Build

To compile the Angular client for production:

```bash
cd easyfin_tech.client
npm run build
```

- Target: `dist/easyfin_tech.client`
- Configuration: `production`
- Optimization: Enabled (minification, tree-shaking, dead-code removal)
- Source Maps: Disabled
- Environment: Swaps `environment.ts` for `environment.prod.ts` via `fileReplacements` in `angular.json`.
- When deploying as a unified application, copy `dist/easyfin_tech.client/browser/*` into `EasyFin_Tech.Server/wwwroot/`.

---

## 7. Logging & Data Privacy Policy

Production logs are configured for `Information`, `Warning`, `Error`, and `Critical` levels.

### Strictly Prohibited from Logs:
- Plaintext user passwords or hashes
- PDF statement unlock passwords
- Bank statement textual content or candidate lines
- Customer transaction details, account numbers, or balances
- Connection strings, API keys, or SAS tokens
- Authentication cookie tokens or values

---

## 8. Startup Pre-Flight Checklist

Before launching EasyFin Tech in production, verify:

- [ ] `ASPNETCORE_ENVIRONMENT` is set to `Production`.
- [ ] `ConnectionStrings__DefaultConnection` is set and points to an encrypted SQL Server with `TrustServerCertificate=False`.
- [ ] `Cors__AllowedOrigins__0` is set to the real production HTTPS frontend URL.
- [ ] `FileUpload__StorageRoot` points to a persistent volume with read/write permissions for the application identity.
- [ ] Swagger and OpenAPI endpoints are disabled and not accessible.
- [ ] Database health check `/api/health/database` does not expose hostnames or schema details.
- [ ] Client application bundle was built using `npm run build` with `production` configuration.
- [ ] Git repository verifies zero tracking of `.env`, `Storage/`, or local secret files.
