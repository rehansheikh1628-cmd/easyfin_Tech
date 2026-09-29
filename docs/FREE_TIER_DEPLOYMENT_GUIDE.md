# EasyFin Tech Free-Tier Production Deployment Guide (₹0 Hosting)

## Overview
This guide provides the complete, step-by-step instructions to deploy EasyFin Tech to the internet at **₹0 initial cost** using verified free-tier cloud platforms.

```
                    INTERNET / BROWSER
                            │
                            ▼
                ┌───────────────────────┐
                │     VERCEL (FREE)     │
                │   Angular 22 Client   │
                │ https://app.vercel.app│
                └───────────┬───────────┘
                            │ HTTPS + Credentials
                            ▼
                ┌───────────────────────┐
                │  RENDER / AZURE (FREE)│
                │  ASP.NET Core 10 API  │
                │ https://api.onrender  │
                └───────────┬───────────┘
              ┌─────────────┼─────────────┐
              ▼             ▼             ▼
    ┌────────────────┐ ┌─────────┐ ┌───────────────┐
    │ AZURE SQL (FREE│ │ STORAGE │ │BACKGROUND SVC │
    │ 100k vCore-sec │ │ Blob/R2 │ │ In-Process    │
    │ 32 GB Storage  │ │ 5GB Free│ │ Channel<T>    │
    └────────────────┘ └─────────┘ └───────────────┘
```

---

## 1. Frontend Deployment (Vercel Free Hobby Tier)

### Prerequisites:
- A free Vercel account ([vercel.com](https://vercel.com)).
- The EasyFin Tech repository pushed to GitHub (`https://github.com/rehansk28/EasyFin_Tech`).

### Step-by-Step Instructions:
1. **Log in to Vercel** and click **Add New... > Project**.
2. **Import Git Repository**: Select `rehansk28/EasyFin_Tech`.
3. **Configure Project Settings**:
   - **Project Name**: `easyfin-tech`
   - **Framework Preset**: `Angular`
   - **Root Directory**: Click `Edit` and select `accufex.client`.
   - **Build Command**: `npm run build` (or leave default: `ng build`)
   - **Output Directory**: `dist/accufex.client/browser`
   - **Install Command**: `npm install`
4. **Deploy**:
   - Click **Deploy**.
   - Vercel will build the Angular SPA and assign a free HTTPS URL: `https://easyfin-tech.vercel.app`.
5. **SPA Routing**:
   - The included `vercel.json` ensures that route refreshes (e.g. `/converter`, `/dashboard`, `/excel-to-tally`) rewrite cleanly to `/index.html` without 404 errors.

---

## 2. Backend Deployment (Render Free Web Service)

### Prerequisites:
- A free Render account ([render.com](https://render.com)).

### Step-by-Step Instructions:
1. **Log in to Render Dashboard** and click **New + > Web Service**.
2. **Connect Repository**: Select `rehansk28/EasyFin_Tech`.
3. **Configure Web Service**:
   - **Name**: `easyfin-api`
   - **Region**: Choose closest to target users (e.g., `Singapore` or `Frankfurt`).
   - **Branch**: `master`
   - **Root Directory**: Leave blank (repo root).
   - **Runtime**: `Docker`
   - **Dockerfile Path**: `Accufex.Server/Dockerfile`
   - **Instance Type**: `Free` (0.1 CPU, 512 MB RAM).
4. **Environment Variables**:
   Under **Environment Variables**, add the following:
   | Key | Value | Description |
   | :--- | :--- | :--- |
   | `ASPNETCORE_ENVIRONMENT` | `Production` | Activates production validation and security |
   | `ASPNETCORE_URLS` | `http://+:8080` | Internal listen port for Render |
   | `PORT` | `8080` | Render HTTP port |
   | `ConnectionStrings__DefaultConnection` | `Server=tcp:YOUR-AZURE-SQL...` | Full SQL Server connection string |
   | `Cors__AllowedOrigins__0` | `https://easyfin-tech.vercel.app` | Your Vercel frontend URL |
   | `Jwt__Key` | `YourSecretSigningKeyAtLeast32CharactersLong!` | Minimum 32-character key |
   | `FileUpload__StorageProvider` | `Local` | Or `AzureBlob` if using cloud storage |
   | `FileUpload__MaxPdfSizeBytes` | `104857600` | 100 MB (Render free proxy body limit) |
5. **Deploy**:
   - Click **Create Web Service**.
   - Render will build the Docker container and deploy to `https://easyfin-api.onrender.com`.

---

## 3. Database Setup (Azure SQL Database Free Offer)

### Prerequisites:
- Microsoft Azure Account (Free Tier / Pay-As-You-Go with Free Offer).

### Step-by-Step Instructions:
1. **Log in to Azure Portal** ([portal.azure.com](https://portal.azure.com)).
2. Search for **SQL databases** and click **Create**.
3. Under **Pricing Tier**, select **Apply free offer** (100,000 vCore seconds/month, 32 GB storage).
4. **Database Name**: `EasyFinTechDb`.
5. **Server**: Create a new logical server (e.g. `easyfin-sql-free`).
6. **Authentication**: Set SQL Server Authentication (Admin username & password).
7. **Networking / Firewall**:
   - Under **Exceptions**, check **"Allow Azure services and resources to access this server"** (allows Render / App Service connections).
   - Add your local IP address for administration.
8. **Connection String**:
   Copy the ADO.NET connection string:
   ```
   Server=tcp:easyfin-sql-free.database.windows.net,1433;Initial Catalog=EasyFinTechDb;Persist Security Info=False;User ID=<username>;Password=<password>;MultipleActiveResultSets=False;Encrypt=True;TrustServerCertificate=False;Connection Timeout=30;
   ```
9. **Schema Creation**:
   Execute the existing `scripts/initialize_production_db.sql` script once against the database using Azure Query Editor or SQL Server Management Studio (SSMS).

---

## 4. File Storage Setup (Azure Blob Storage or Cloudflare R2)

### Option A: Azure Blob Storage (5 GB Free for 12 months)
1. In Azure Portal, create a standard Storage Account (e.g. `easyfinstorage`).
2. Create a Blob Container named `statements` with **Public access level: Private (no anonymous access)**.
3. Obtain the Connection String from **Access keys**.
4. Set backend environment variables:
   - `FileUpload__StorageProvider`: `AzureBlob`
   - `FileUpload__AzureBlob__ConnectionString`: `<Storage Connection String>`
   - `FileUpload__AzureBlob__ContainerName`: `statements`

### Option B: Local Disk (Persistent Volume or Mount)
- If running on a dedicated node or VPS, set `FileUpload__StorageProvider=Local`. Files remain tenant-isolated in `Storage/Statements/{UserId}/`.

---

## 5. Free-Tier Limits & Operational Constraints Audit

| Provider / Layer | Free Plan Limits | Impact on EasyFin Tech | Workaround / Best Practice |
| :--- | :--- | :--- | :--- |
| **Vercel (Frontend)** | 100 GB Bandwidth/mo, 100 deploys/day | Ample for thousands of users | Static asset caching via CDN |
| **Render (Backend)** | Inactive spin-down after 15 min | Cold start takes 50-70 seconds; background jobs freeze while sleeping | First request wakes service; Keep-alive ping (e.g. UptimeRobot) can prevent sleep |
| **Render (Backend)** | 512 MB RAM, 0.1 CPU | Heavy 100 MB PDF parsing may approach memory limits | Process statements in streams; keep concurrency bounded at 2 jobs |
| **Render (Backend)** | 100 MB request body limit | Statements > 100 MB will be rejected at the proxy | Uploads under 100 MB work reliably |
| **Azure SQL (Free)** | 100k vCore-s/mo, 32 GB storage | Auto-pauses when idle; 30s resume delay | EF Core retry policy (`EnableRetryOnFailure`) handles reconnection |
| **Local Disk (Cloud)**| Ephemeral on free containers | Files deleted on container sleep/restart | Use Azure Blob / Cloudflare R2 for true persistent storage |

---

## 6. Pre-Flight Verification & Health Check

1. **Verify Backend Health**:
   ```bash
   curl -i https://easyfin-api.onrender.com/health
   # Expected: HTTP/1.1 200 OK
   # Content: {"status":"Healthy"}
   ```
2. **Verify Database Connectivity**:
   ```bash
   curl -i https://easyfin-api.onrender.com/api/health/database
   # Expected: HTTP/1.1 200 OK
   # Content: {"status":"Healthy","canConnect":true}
   ```
3. **Verify CORS & Frontend Access**:
   Open `https://easyfin-tech.vercel.app` in browser.
   Open Developer Tools Network tab and verify requests to `https://easyfin-api.onrender.com/api/auth/me` return 401 Unauthorized (normal unauthenticated state) with valid CORS headers and credentials enabled.
