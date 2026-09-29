# ACCUFEX — Project Brain
*Persistent Project Context, Architecture Map & AI Working Memory*

> **AUTHORITY NOTICE**:  
> `BRAIN.md` is the persistent context layer for ACCUFEX. It documents verified architecture, locked components, technology versions, design decisions, and project rules. It must **not** replace source code or invent unverified features. If source code and `BRAIN.md` diverge, verified source code is authoritative; update `BRAIN.md` accordingly.

---

## 1. Project Overview

- **Project Name**: `ACCUFEX`
- **Primary Product**: Bank Statement PDF → Excel Financial Normalizer & Converter
- **Core Principle**: **Accuracy > Feature Count** (Zero financial hallucinations, verifiable mathematical continuity, complete auditability).
- **Core Workflow Pipeline**:
  $$\text{PDF Upload} \longrightarrow \text{PDF Extraction} \longrightarrow \text{Bank Detection} \longrightarrow \text{Bank Parsing} \longrightarrow \text{Transaction Validation} \longrightarrow \text{Review / Correction} \longrightarrow \text{Excel Export}$$

---

## 2. Technology Stack (Verified)

### Backend
- **Framework**: ASP.NET Core (.NET 10.0, `net10.0`)
- **Language**: C# 13 / .NET 10
- **ORM**: Entity Framework Core 10.0.11 (`Microsoft.EntityFrameworkCore.SqlServer`)
- **Database Engine**: Microsoft SQL Server (Local / Production Connection via `DefaultConnection`)
- **PDF Extraction**: `PdfPig` (Version `0.1.16`) — Low-level digital text, coordinates, bounding box geometry, and glyph extraction.
- **Excel Generation**: `ClosedXML` (Version `0.105.1`, MIT License) — OpenXML `.xlsx` generation, strongly-typed cells, true dates, numeric currency formatting (`#,##0.00`), explicit text formatting, freeze panes, autofilters, and multi-sheet workbooks.
- **Dependency Injection**: Native ASP.NET Core Service Provider

### Frontend
- **Framework**: Angular 22 (`@angular/core` `^22.1.0`, `@angular/cli` `^22.1.3`)
- **Language**: TypeScript (`~6.0.2`)
- **Node Runtime**: `v24.19.0`
- **Package Manager**: npm (`11.17.0`)
- **Styling**: Vanilla CSS with tailored design tokens, scoped component stylesheets.
- **Client Build Tool**: `@angular/build` (Vite-powered development / esbuild production pipeline)

### Testing Frameworks
- **Backend**: `xUnit` (`2.9.3`), `Moq` (`4.20.72`), `Microsoft.AspNetCore.Mvc.Testing` (`10.0.0-preview.*`), `Microsoft.EntityFrameworkCore.InMemory` (`10.0.11`), `coverlet.collector` (`6.0.4`).
- **Frontend**: `Vitest` (`4.0.8`) with `jsdom` (`28.0.0`) via `ng test --watch=false`.

---

## 3. Solution Structure

```
ACCUFEX/
├── Accufex.slnx                          # Solution descriptor (.NET 10 SLNX format)
├── BRAIN.md                              # Persistent project memory & architecture context
│
├── Accufex.Server/                       # ASP.NET Core Web API Backend
│   ├── Controllers/                      # REST API Endpoints
│   │   ├── AuthController.cs             # Registration, Login, Logout, Session state
│   │   ├── DashboardController.cs        # User dashboard metrics & client summary
│   │   └── StatementsController.cs       # Upload, Extraction, Parse, Review, Correct, Validate
│   ├── Data/
│   │   └── AccufexDbContext.cs           # EF Core DbContext (SQL Server source of truth)
│   ├── Models/                           # Database Entities (User, Client, FileRecord, etc.)
│   ├── DTOs/                             # Request/Response Data Transfer Objects
│   ├── Parsing/                          # Multi-Bank Parsing Subsystem
│   │   ├── BankType.cs                   # Bank enum (Hdfc = 1, YesBank = 2, Axis = 3)
│   │   ├── BankDetector.cs               # Deterministic multi-signal bank classifier
│   │   ├── BankParserRegistry.cs         # DI registry resolving parser instances
│   │   ├── BankParsingService.cs         # Pipeline coordinator (Extract -> Parse -> Snapshot -> Persist)
│   │   ├── Interfaces/                   # Parser contracts (IBankStatementParser, etc.)
│   │   ├── Models/                       # ParsedTransaction, BankParsingResult
│   │   └── Parsers/                      # Concrete Bank Parsers (HDFC, YES, AXIS, ICICI, SBI, etc.)
│   ├── Validation/                       # Transaction Validation & Sidecar Audit Subsystem
│   │   ├── Models/                       # ValidationStatus, OriginalTransactionSnapshot, Sidecar
│   │   └── Services/                     # TransactionValidationService, TransactionCorrectionStore
│   ├── Export/                           # Phase 6 Excel Export Subsystem
│   │   ├── Interfaces/                   # IExcelExportService
│   │   └── Services/                     # ExcelExportService (ClosedXML, OpenXML multi-sheet)
│   ├── Services/                         # Core infrastructure services
│   │   ├── CurrentUserService.cs         # HttpContext identity & claim resolver
│   │   ├── LocalFileStorageService.cs    # Sandboxed disk storage with path-traversal guards
│   │   ├── PasswordHasher.cs             # PBKDF2/SHA256 secure password hashing
│   │   ├── PdfExtractionService.cs       # Coordinate-based geometry & line reconstruction
│   │   └── StatementService.cs           # Statement lifecycle management
│   ├── Options/                          # Strongly typed options (FileUploadOptions)
│   └── Program.cs                        # Application bootstrap, auth cookie, pipeline setup
│
├── accufex.client/                       # Angular Single Page Application
│   ├── src/app/
│   │   ├── pages/                        # Feature workspaces
│   │   │   ├── converter/                # Primary 3-step statement converter & review UI
│   │   │   ├── dashboard/                # Analytics & client workspace
│   │   │   ├── login/ & signup/          # Authentication flows
│   │   │   └── supported-banks/          # Bank coverage & specification directory
│   │   ├── services/                     # Client API clients
│   │   │   ├── auth.service.ts           # Cookie auth & session management
│   │   │   ├── auth.interceptor.ts      # HTTP credentials & error interception
│   │   │   └── statement.service.ts      # Statement upload, parsing, review & correction API
│   │   └── guards/                       # Route protection (auth guard)
│   └── package.json                      # Angular dependencies & script runner
│
└── Accufex.Server.Tests/                 # Comprehensive Automated Test Suite
    ├── AxisParserTests.cs                # Axis Bank unit & fixture tests
    ├── HdfcParserTests.cs                # HDFC Bank unit & fixture tests
    ├── YesBankParserTests.cs             # YES BANK unit & fixture tests
    ├── TransactionValidationTests.cs     # Phase 5 validation & correction tests
    ├── ExcelExportTests.cs               # Phase 6 Excel generation & IDOR tests
    ├── ProductionSecurityTests.cs        # Authorization & IDOR prevention tests
    ├── PdfExtractionTests.cs             # Geometry, text blocks & table detection tests
    └── StatementIngestionTests.cs        # Upload, hashing & file storage tests
```

---

## 4. Database Structure & Ownership

### Entity Relationship Model

$$\text{User} \xrightarrow{1:N} \text{Client} \xrightarrow{1:N} \text{FinancialYear} \xrightarrow{1:N} \text{FileRecord} \xrightarrow{1:N} \text{Transaction}$$

1. **`User`**: Account owner (`Id`, `Email`, `PasswordHash`, `FullName`, `CreatedAt`).
2. **`Client`**: Organization or individual entity (`Id`, `UserId`, `Name`, `PanNumber`, etc.).
3. **`FinancialYear`**: Accounting period (`Id`, `ClientId`, `StartDate`, `EndDate`, `DisplayName`).
4. **`FileRecord`**: Uploaded bank statement PDF (`Id`, `ClientId`, `FinancialYearId`, `StoredFileName`, `FileHash`, `ProcessingStatus`).
5. **`Transaction`**: Normalized financial entry (`Id`, `SourceFileId`, `ClientId`, `FinancialYearId`, `TransactionDate`, `Description`, `Debit`, `Credit`, `Amount`, `Balance`, `Reference`, `UTR`, `BankCode`, `Account`, `ProcessingWarning`, `CreatedAt`).
6. **`TransactionImportResult`**: Summary log of statement parse operation (1:1 with `FileRecord`).
7. **`PdfProcessingResult`**: Summary metadata of raw PDF extraction (1:1 with `FileRecord`).

### Strict Database Governance
- **SQL Server is the source of truth** for operational transactions.
- **Zero Casual Schema Changes**: Existing table structures must remain intact unless approved.
- **No Unapproved EF Migrations**: If a schema gap is discovered, **STOP** and report before creating migrations.
- **`Transaction.ProcessingWarning` Isolation**: This field is strictly reserved for parser warnings (e.g. OCR notice, running balance mismatch warning). It is **never** used to store validation status or correction audit JSON.

---

## 5. Authentication & Security Architecture

### Authentication Model
- **Mechanism**: ASP.NET Core Cookie Authentication (`EasyFin_Auth`).
- **Cookie Settings**: `HttpOnly = true`, `SameSite = Lax`, `SecurePolicy = SameAsRequest`.
- **Identity Claim**: `ClaimTypes.NameIdentifier` contains `User.Id` (GUID string).
- **Session Resolution**: [CurrentUserService.cs](file:///e:/BrandNew%20Day/EasyFin%20Tech/EasyFin_Tech/EasyFin_Tech.Server/Services/CurrentUserService.cs) retrieves the authenticated user ID from `HttpContext.User`.

### Security Rules & IDOR Protection
1. **The Server is the Sole Authority**: Frontend values (`userId`, `clientId`, `financialYearId`, `fileId`, `transactionId`) are treated as untrusted hints.
2. **Ownership Chain Enforcement**: Every read or write must verify:
   $$\text{Authenticated User} \longrightarrow \text{Authorized Client} \longrightarrow \text{Authorized FileRecord} \longrightarrow \text{Authorized Transaction}$$
   If any link does not belong to the user, return `404 Not Found` (or `403 Forbidden`).
3. **Private Controlled Storage**:
   - Original PDFs: Stored in `Storage/statements/` outside `wwwroot`.
   - Structural Extraction JSON: Stored in private storage, inaccessible via static URLs.
   - Sidecar Audit JSON: Stored in `Storage/corrections/statement_audit_{fileId}.json`.
   - Path Traversal Guard: Full directory resolution enforced; attempts to escape root trigger security alerts.
4. **Password & Credential Safety**:
   - Passwords hashed with PBKDF2/SHA256 (10,000 iterations, 128-bit salt, 256-bit subkey).
   - User ID is a standard GUID (never hashed).
   - Plaintext passwords must **never** be logged, cached, or returned in API responses.

---

## 6. Supported Banks & Scope

The current bank parser scope is **strictly limited to 3 banks**. Expansion is intentionally paused.

| Bank | Parser Identifier | Code | Status | Format Scope & Details |
| :--- | :--- | :---: | :---: | :--- |
| **HDFC Bank** | `HDFC-v1` | 1 | **COMPLETE / LOCKED** | Generic digital parser for standard retail & corporate statements. Dynamic header calibration, multi-line narration, cross-page narration continuation across physical page boundaries ($N \to N+1$), balance continuity. |
| **YES BANK** | `YES-v1` | 2 | **COMPLETE / LOCKED** | Standard validated format. Corporate Format 2 remains unvalidated until real sample is provided. |
| **Axis Bank** | `AXIS-v1` | 3 | **COMPLETE / LOCKED** | Standard 9-column format (`S.No`, `Tran Date`, `Value Date`, `Particulars`, `Amount`, `Debit/Credit`, `Balance`, `Cheque No`, `Branch`). Dynamic clustering, sign mapping. |

> [!CAUTION]
> **DO NOT IMPLEMENT**: ICICI Bank, Bank of India, Canara Bank, or any other bank without explicit user authorization.

---

## 7. Shared Parser Architecture

```
PDF Document
    │
    ▼
[IPdfExtractionService] ──> Extracted Words, Coordinates, Bounding Boxes, Candidate Rows
    │
    ▼
[IBankDetector]         ──> Multi-signal scoring (IFSC, Bank names, header signatures)
    │
    ▼
[IBankParserRegistry]   ──> Resolves IBankStatementParser (Hdfc, YesBank, Axis)
    │
    ▼
[IBankStatementParser]  ──> Dynamic column calibration, row clustering, narration wrapping,
    │                       running balance calculation
    ▼
[BankParsingService]    ──> Coordinates parse, creates pristine snapshot, persists to SQL
```

### Core Parser Classes & Interfaces
- **`BankType`**: Supported bank enumeration (`Hdfc = 1`, `YesBank = 2`, `Axis = 3`).
- **`IBankDetector` / `BankDetector`**: Evaluates keyword presence, IFSC patterns, and column header configurations to detect bank origin deterministically.
- **`IBankStatementParser`**: Contract for bank-specific extraction logic (`BankCode`, `BankName`, `ParserVersion`, `CanParse`, `ParseStatement`).
- **`IBankParserRegistry` / `BankParserRegistry`**: Maps `BankType` to registered parser service.
- **`ParsedTransaction`**: Normalized intermediate DTO representing an extracted row before database storage.
- **`BankParsingResult`**: Comprehensive parser output containing extracted transactions, detected account metadata, warnings, and summary metrics.

---

## 8. PDF Extraction & Genericity Rules

1. **Source of Truth**: Extracted words with visual coordinates $(X, Y, \text{Width}, \text{Height})$ are the sole source of truth. Raw page text strings are diagnostic only.
2. **Column Gaps vs Transaction Boundaries**:
   - Horizontal gaps between words represent column boundaries.
   - Vertical proximity with identical column alignment represents continuation (wrapped particulars/narration).
   - Multiple visual lines form a single transaction until the next transaction date/marker.
3. **No Hardcoding of Fixture Values**:
   - Never hardcode customer names, account numbers, branch names, statement periods, transaction amounts, reference formats, page counts, or fixed coordinate offsets.
   - Algorithms must dynamically locate column headers and calculate bounding intervals per page.
4. **Scanned PDF Handling**: Digital PDFs with selectable text are supported. Scanned image PDFs produce honest `NoDigitalTextDetected` status and await future OCR.
5. **Robust Repeated Header & Footer Detection**:
   - In `PdfExtractionService.DetectRepeatedHeadersAndFooters`: Candidate footers must have substance ($\ge 15$ characters or standard page number format), strictly reside near the bottom margin ($Y \ge 0.90 \times \text{Height}$), have consistent vertical placement ($\text{Max} - \text{Min} \le 20.0\text{pt}$), and exclude lines with currency amounts, transaction dates, or payment keywords (`UPI-`, `IMPS-`, `@`, `PAYMENT FROM`, etc.).
   - Recurrence threshold requires at least 2 pages (for small documents) or 20% of pages (capped at 5).
   - In `HdfcStatementParser.IsPageFooterLine`: Substring matching `text.Contains(f)` is prohibited. Matching enforces normalized whole-line or substantial prefix ($\ge 20$ chars) comparison against multi-token signatures, strictly verifies footer region geometry ($Y \ge \max(\text{TableTopY} + 50, 0.75 \times \text{Height})$), and explicitly rejects any line starting with a valid transaction date.

---

## 9. Validation & Correction Architecture (Phase 5)

Phase 5 operates under a **Zero-Migration Sidecar Architecture**:

```
+-------------------------------------------------------------------------------+
| SQL Server `Transaction` Table                                               |
| - Current Operational Values (Updated only after validation passes)           |
| - `ProcessingWarning` (Strictly parser warnings only)                         |
+-------------------------------------------------------------------------------+
                                      ▲
                                      │ Synchronized
                                      ▼
+-------------------------------------------------------------------------------+
| Private Sidecar File: Storage/corrections/statement_audit_{fileId}.json       |
| - OriginalTransactionSnapshot (19 immutable parsed attributes captured at import)|
| - CorrectionHistory [ { TimestampUtc, UserId, Reason, Changes: [Old, New] } ] |
+-------------------------------------------------------------------------------+
```

### Validation Engine (`TransactionValidationService`)
Validation status is computed **dynamically on read** across 6 core rules:
- **Rule A (Required Fields)**: Non-default date, non-empty description, valid transaction type.
- **Rule B (Date Logic)**: Future transaction dates flagged as `INVALID`; realistic historical range enforced.
- **Rule C (Monetary Logic)**: Positive amounts required; strict mutual exclusivity between Debit and Credit ($D > 0 \iff C = \text{null}$).
- **Rule D (Running Balance Continuity)**: Sequential check:
  $$\text{Balance}_{k-1} - \text{Debit}_k + \text{Credit}_k = \text{Balance}_k$$
  Discrepancies trigger `REVIEW` status without breaking pipeline execution.
- **Rule E (Duplicate Detection)**: Normalized composite hash matching on `(Date, Amount, Type, Reference, Description)`.
- **Rule F (Suspicious / High-Risk Checks)**: Large round-number transactions (> ₹1,00,000) and missing references flagged for attention.

### Dynamic Status Hierarchy
- `VALID`: Passes all rules, balance consistent, no active warnings.
- `REVIEW`: Non-fatal warning (balance gap, possible duplicate, suspicious pattern).
- `INVALID`: Fatal error (missing date, negative amount, both debit and credit present).
- `CORRECTED`: Validated transaction with at least one historical user correction.

### Concurrency & Data Safety
- **Per-File Thread Locking**: `ConcurrentDictionary<Guid, SemaphoreSlim>` ensures safe serialization of concurrent corrections for the same statement.
- **Atomic Write-Ahead**: Writes to a temporary `.tmp` file and performs atomic `File.Move(..., overwrite: true)`.
- **Corrupt Sidecar Guard**: Missing or malformed sidecars are **never** silently recreated from SQL. An explicit recoverable error is returned.

---

## 10. Phase Status & Progress Map

| Phase | Description | Status | Verification & Artifacts |
| :--- | :--- | :---: | :--- |
| **Phase 1** | Foundation + UI Design System | **COMPLETE** | Angular shell, responsive layout, tokens |
| **Phase 2** | PDF Ingestion & Controlled Storage | **COMPLETE** | Sandboxed disk storage, hash deduplication |
| **Phase 3** | PDF Coordinate-Based Extraction | **COMPLETE** | PdfPig text blocks, candidate rows, tabs |
| **Cleanup** | Dead-Code & Redundant Script Cleanup | **COMPLETE** | Codebase pruned, imports consolidated |
| **Security** | User Isolation & IDOR Protection | **COMPLETE** | 16 security tests passing, cookie auth |
| **Phase 4** | Bank Parsing (HDFC, YES, Axis) | **COMPLETE / LOCKED** | `HDFC-v1`, `YES-v1`, `AXIS-v1` verified (3 banks) |
| **Phase 5** | Validation + Preview + Correction | **COMPLETE** | Sidecar audit store, 27 validation tests |
| **Phase 6** | Excel Generator Engine | **COMPLETE** | ClosedXML multi-sheet export, 24 tests |
| **Phase 20** | Launch / GO Hardening & E2E Validation | **COMPLETE** | Enterprise redesign, 335 backend / 123 frontend tests |
| **Phase 21** | Production Deployment & Domain Config | **COMPLETE / VERIFIED** | IIS automation, Let's Encrypt, SQL schema DDL, security hardening |
| **Phase 22** | Production Environment & E2E Validation | **COMPLETE / VERIFIED** | Live production E2E validated, real statement PDF->XLSX, Excel->Tally XML, zero leaks |

---

## 11. Test & Build Baseline (Current Verified State)

- **Latest Verified Backend Tests (`dotnet test`)**: **338 Passed, 0 Failed, 0 Skipped** (Duration: ~25.0s)
- **Latest Verified Frontend Tests (`npm test`)**: **123 Passed, 0 Failed** (Duration: ~7.4s)
- **TypeScript Static Verification (`npx tsc --noEmit`)**: **0 Errors (Clean)**
- **Frontend Production Build (`npm run build`)**: **Clean (Exit code 0, 0 Warnings, 0 Errors)**
- **Backend Build (`dotnet build`)**: **0 Warnings, 0 Errors (Exit code 0)**
- **Production Publish (`dotnet publish -c Release`)**: **Clean (Exit code 0, self-contained wwwroot, pre-compressed assets, IIS 50MB web.config, initialize_production_db.sql, deploy_iis.ps1)**

### Real Statement Regression Fixtures
- **HDFC Bank**: 47-page and 65-page production statements.
- **YES BANK**: Multi-page retail/business statements (Iris).
- **Axis Bank**: 20-page statement (`AXIS APRIL TO MARCH.PDF`, 615 transactions, 100% balance parity).

---

## 12. UI & Product Principles

- **Visual Identity**: Professional financial-technology palette:
  - Deep Navy/Slate backgrounds (`#0f172a`, `#1e293b`).
  - Subtle Borders & Glassmorphism (`rgba(255, 255, 255, 0.08)`).
  - Mint / Teal Accents (`#10b981`, `#14b8a6`).
  - Excel Forest Green (`#16a34a`).
- **Simplicity Outside, Depth Inside**: Present a clean, distraction-free interface for standard workflows; offer granular diagnostic tools (raw text, candidate row inspection, immutable audit history) on demand.
- **Accuracy > Visual Decoration**: Never compromise precision or table readability for decorative effects.

---

## 13. Known Limitations

313: 1. **OCR for Scanned Documents**: Image-only or non-digital PDFs are identified honestly with `NoDigitalTextDetected`. Scanned OCR pipeline is deferred to a future phase.
314: 2. **YES BANK Corporate Format 2**: Format 2 layout heuristics are implemented but unvalidated against real corporate production samples.
315: 3. **Axis Legacy/Unformatted Layouts**: Only standard 9-column statements are validated; older passbook-style formats may require future calibration.
316: 4. **Bank Expansion Locked**: Bank support is strictly calibrated and frozen at 8 institutions / 9 profiles: HDFC-v1, YES-v1, AXIS-v1, ICICI-v1, ICICI-v2, SBI-v1, BOI-v1, Kotak-v1, CentralBank-v1.
317: 
318: ---
319: 
320: ## 14. Non-Negotiable Project Rules
321: 
322: 1. **Do Not Destroy Working Functionality**: Never regress existing parsers, authentication, or test suites.
323: 2. **Keep Locked Parsers Untouched**: All 9 calibrated bank profiles must not be altered casually.
324: 3. **Never Hardcode Fixture Details**: Parsing must be format-driven, not sample-driven.
325: 4. **Never Trust Browser IDs**: Enforce complete server-side authorization on every request.
326: 5. **No Database Migrations Without Approval**: Inspect schema first; report any discovered gaps.
327: 6. **Never Pollute `ProcessingWarning`**: Keep it exclusively for parser warnings.
328: 7. **Preserve Immutable Originals**: Original parsed values must never be overwritten or reconstructed from edited data.
329: 8. **No Silent Financial Hallucinations**: Calculate mathematical balances strictly; report discrepancies clearly.
330: 9. **Single-Task Focus**: Execute one major phase at a time; do not implement future phases prematurely.
331: 10. **Zero Secrets in Code or Context**: Passwords, connection secrets, and private tokens must never be recorded in documentation.
332: 
333: ---
334: 
335: ## 15. Current Next Step
336:  
337: - **Current Status**: **Phase 24 (Final Product Completion & Pre-Launch Quality Audit) Completed & Verified 🎯**.
338:   - Final Verdict: **🟢 READY FOR PRODUCTION LAUNCH**.
339:   - Pre-Launch Audit & Hardening Completed:
340:     - Dead Developer Links Cleaned: Removed dead Swagger `/swagger` and `/openapi/v1.json` links from sidebar and settings in production, replaced with verified documentation links (`/how-it-works`, `/supported-banks`).
341:     - Dashboard Connectivity Card Sanitized: Labeled as "System Connectivity", database name sanitized to "Production", server instance sanitized to "Protected" in production mode.
342:     - Verified Factual Counts: 8 Supported Banking Institutions • 9 Calibrated Profiles. Zero artificial or fabricated claims across marketing pages.
343:     - Operations & Disaster Recovery Runbook verified: Live backup, restore verification test on recovery DB, Robocopy storage replication, and multi-tenant isolation tests.
344:   - Full Automated Regression Suite:
345:     - Backend: 338 passed, 0 failed, 0 skipped (`dotnet test` across net10.0).
346:     - Frontend: 123 passed, 0 failed across 10 test suites (`ng test --watch=false`).
347:     - TypeScript Compile: 0 errors (`npx tsc --noEmit`).
348:     - Production Client Build: `npm run build` PASS (979 kB bundle, 189 kB gzip transfer).
349:     - Production Release Build: `dotnet build -c Release` PASS (0 warnings, 0 errors).
350:     - Production Release Publish: `dotnet publish EasyFin_Tech.Server -c Release -o ./publish` PASS.
351:   - Strict Protection Scope: 100% Preserved. Zero modifications to bank parsers, PDF extraction geometry, ClosedXML, Tally XML, or database schema.
352:   - Deferred Scope: Bills & Invoices $\to$ Excel remains strictly DEFERRED.
353: - **Next Step**: Awaiting user explicit directive before starting Phase 25 (Bills & Invoices $\to$ Excel). Work STOPPED at Phase 24 as required.
354: - **Rule**: Maintain locked status of all parsers (`HDFC-v1`, `YES-v1`, `AXIS-v1`, `ICICI-v1`, `ICICI-v2`, `SBI-v1`, `BOI-v1`, `Kotak-v1`, `CentralBank-v1`). Zero unapproved database schema changes.

---

## 16. AI Working Instructions

Future AI assistants working on EasyFin_Tech must adhere to this workflow:
1. **Read `BRAIN.md` First**: Familiarize with the current phase, locked parsers, database entities, and non-negotiable rules.
2. **Do Not Perform Full Codebase Re-Analysis**: If the required context is documented here, inspect only the specific files relevant to your task.
3. **Source Code is Authoritative**: If code diverges from `BRAIN.md`, trust verified source code and update `BRAIN.md`.
4. **Maintain Test Baseline**: Before concluding any phase, execute `dotnet test`, `npx ng test --watch=false`, `npx tsc --noEmit`, and `dotnet build`.
5. **Update `BRAIN.md` on Phase Completion**: Record test counts, architectural additions, and phase transitions.
