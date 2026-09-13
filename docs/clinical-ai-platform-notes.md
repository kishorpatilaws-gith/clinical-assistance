# Privacy-First AI Clinical Decision Support Platform — Build Log

A learning project building an end-to-end clinical AI platform: React/Next.js frontend, ASP.NET Core backend, Amazon Bedrock for AI, Supabase (Postgres + Auth), Cloudflare R2, and a Python document-processing microservice. Built one layer at a time per the assignment's learning rule: *build → deploy → test → understand → document → next layer.*

**Safety boundary:** synthetic data only, no autonomous diagnosis, AI output is always framed as considerations for a licensed clinician — never a diagnosis or prescription.

---

## Tech Stack Summary

| Layer | Technology |
|---|---|
| Frontend | React / Next.js (App Router, TypeScript) |
| Backend | ASP.NET Core (.NET, net10.0 SDK) |
| AI | Amazon Bedrock — `amazon.nova-lite-v1` via the Converse API |
| Database | Supabase Postgres + Entity Framework Core |
| Auth | Supabase Auth (JWT, ES256/JWKS-based signing) |
| Document storage | Cloudflare R2 (S3-compatible) |
| Document processing | Python (FastAPI + PyMuPDF + python-docx), standalone microservice |
| Guardrails | Amazon Bedrock Guardrails (configured, currently detached — revisit at Part 10) |

---

## Repository Layout

```
clinical-ai-platform/
├── backend/
│   └── ClinicalAI.Api/
│       └── ClinicalAI.Api/                  ← actual project root
│           ├── Program.cs
│           ├── appsettings.Development.json
│           ├── Domain/Entities/
│           │   ├── Patient.cs
│           │   ├── AuditEvent.cs
│           │   └── Document.cs
│           ├── Infrastructure/Data/
│           │   └── ClinicalDbContext.cs
│           ├── Application/Services/
│           │   ├── PatientSummaryService.cs
│           │   ├── PiiRedactionService.cs
│           │   ├── AuditService.cs
│           │   └── R2StorageService.cs
│           ├── Services/
│           │   └── BedrockService.cs        ← outlier namespace, not yet moved into Application/Services
│           ├── Controllers/
│           │   ├── BedrockController.cs
│           │   ├── PatientController.cs
│           │   └── DocumentController.cs    ← pending creation (Part 4 in progress)
│           └── Migrations/
│               ├── InitialCreate.cs
│               ├── AddAuditEvents.cs
│               └── AddDocuments.cs          ← pending, once Part 4 finishes
├── frontend/
│   └── clinical-ai-ui/
│       ├── .env.local
│       ├── lib/auth.ts
│       └── app/
│           ├── page.tsx
│           ├── globals.css
│           ├── login/page.tsx
│           ├── patients/page.tsx
│           ├── patients/[id]/page.tsx
│           └── documents/page.tsx
└── document-processor/                       ← standalone Python microservice, NOT inside the .NET project
    ├── main.py
    └── requirements.txt
```

**Key point:** `document-processor` is a fully separate application (different language runtime) that the .NET API calls over plain HTTP — not a referenced library. During development, three processes run simultaneously: `dotnet run` (5258), `uvicorn main:app` (8000), `npm run dev` (3000).

---

## Part 1 — End-to-End Bedrock Connectivity ✅

Architecture: `React → ASP.NET Core → Amazon Bedrock → Model → Response`

- Scaffolded `dotnet new webapi --use-controllers`, added `AWSSDK.BedrockRuntime`.
- Registered `IAmazonBedrockRuntime` as a singleton in `Program.cs`.
- `BedrockService.InvokeModelAsync` uses the **Converse API** (unified across Nova/Claude/Titan):

```csharp
public async Task<string> InvokeModelAsync(string userPrompt)
{
    var request = new ConverseRequest
    {
        ModelId = "amazon.nova-lite-v1:0",
        Messages = new List<Message> {
            new Message {
                Role = ConversationRole.User,
                Content = new List<ContentBlock> { new ContentBlock { Text = userPrompt } }
            }
        },
        InferenceConfig = new InferenceConfiguration { MaxTokens = 512, Temperature = 0.3f }
    };
    var response = await _client.ConverseAsync(request);
    return response.Output.Message.Content[0].Text;
}
```

- `BedrockController` exposes `POST /api/bedrock/invoke`.
- Minimal React test page confirmed a real round-trip response.

---

## Part 2 — Patient Application ✅

Architecture: `Doctor → Patient → .NET API → Bedrock → Clinical Summary`

- **Supabase Postgres** set up (free tier). Connection required the **Session pooler** (IPv4) rather than Direct connection (IPv6-only by default, timed out on this network), with `SSL Mode=Require;Trust Server Certificate=true` and the dotted pooler username `postgres.<project-ref>`.
- `Patient` entity (synthetic fields only): FirstName, LastName, DOB, Sex, Allergies, CurrentMedications, Symptoms, MedicalHistory.
- Clean-Architecture-style folders: `Domain/Entities`, `Infrastructure/Data`, `Application/Services`, `Controllers`.
- Seeded two synthetic patients (Jordan Rivera, Priya Nair) on startup.
- `PatientSummaryService` builds a structured prompt instructing the model to produce a summary + considerations + red flags, explicitly forbidding diagnosis/prescription language.
- `PatientController`: `GET /api/patient`, `POST /api/patient`, `GET /api/patient/{id}/summary`.

---

## Part 3 — Authentication, Privacy & Security ✅

Architecture: `User → Authentication → Authorization → Privacy Layer → AI`

### Auth
- Supabase Auth enabled; test users created via dashboard.
- Tokens obtained via `POST {supabase_url}/auth/v1/token?grant_type=password` with the `apikey` header — in PowerShell, use `Invoke-RestMethod` (not the `curl` alias, which maps to `Invoke-WebRequest` with incompatible syntax).
- **Important discovery:** this Supabase project signs JWTs with **ES256** (asymmetric/JWKS), not the older HS256 shared-secret scheme. Fix was to set `options.Authority` instead of a static `IssuerSigningKey`, letting the JWT Bearer middleware auto-discover Supabase's public keys:

```csharp
.AddJwtBearer(options =>
{
    options.Authority = $"{supabaseUrl}/auth/v1";
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidIssuer = $"{supabaseUrl}/auth/v1",
        ValidateAudience = true,
        ValidAudience = "authenticated",
        ValidateIssuerSigningKey = true,
        ValidateLifetime = true
    };
});
```

### RBAC
- App-level role stored in Supabase `app_metadata.app_role` (e.g. `"doctor"`), set via SQL:
  ```sql
  update auth.users set raw_app_meta_data = raw_app_meta_data || '{"app_role": "doctor"}'::jsonb
  where email = 'doctor@test.com';
  ```
- `OnTokenValidated` event parses the `app_metadata` claim and maps `app_role` to a standard `ClaimTypes.Role` claim.
- Controllers protected with `[Authorize(Roles = "doctor")]`.
- Confirmed both directions: doctor token → `200`; no-role token → `403 Forbidden`.

### Privacy layer (PII/PHI redaction)
- `PiiRedactionService` regex-redacts email/phone/SSN patterns from free-text fields.
- **Bigger structural choice:** `PatientSummaryService` never sends `FirstName`/`LastName`/direct identifiers into the Bedrock prompt at all — only de-identified clinical facts. Identity is re-attached only after the AI call, on the trusted backend, for the authenticated doctor's response.

### Audit logging
- `AuditEvent` entity + `AuditEvents` table: `UserEmail`, `Action`, `PatientId`, `Timestamp`, `Details`.
- Logged on `ListPatients`, `GenerateSummary`, document actions.
- Confirmed rows appear correctly in Supabase Table Editor.
- Enabled **Row Level Security (RLS)** on `Patients` and `AuditEvents` tables — safe because the .NET API connects via the Postgres superuser role (bypasses RLS); this just blocks any hypothetical direct client-side Supabase API access, which the architecture doesn't use anyway.

### Guardrail incident (resolved)
- A pre-existing Bedrock Guardrail (`HealthcareOnlyGuardrail`) was configured as an **account-level enforcement guardrail** — applies to every Bedrock call across the whole AWS account regardless of application code. This caused clinical summary prompts to be blocked with a custom denial message, even though no code referenced any guardrail.
- Diagnosed by process of elimination: confirmed nothing in the codebase referenced "Guardrail" (global search, 0 matches), then found the account-level enforcement setting in Bedrock console → Guardrails → Account-level enforcement configuration.
- Disabled account-level enforcement for now; plan to properly reintroduce and tune guardrails deliberately at **Part 10**.

---

## Frontend — React/Next.js UI ✅

Built after Part 3, covering login through document upload:

- `lib/auth.ts` — `login()`, `getToken()`, `getUserEmail()`, `logout()`, `apiFetch()` wrapper that attaches the Bearer token to every request. Tokens stored in `localStorage` (fine for this learning project; flagged that production should use httpOnly cookies to avoid XSS exposure).
- `app/login/page.tsx` — clinician sign-in form.
- `app/patients/page.tsx` — patient list, logout, link to documents.
- `app/patients/[id]/page.tsx` — dynamic route; "Generate Clinical Summary" button calling `/api/patient/{id}/summary`.
- `app/documents/page.tsx` — file upload (multipart/form-data) + document list with status.
- `app/page.tsx` — root redirect to `/login` or `/patients` based on token presence.

### Design system (`app/globals.css`)
Deliberate visual direction for a clinical tool — calm, record-like, not a generic AI-SaaS look:
- **Palette:** `#F6F8F9` background, `#FFFFFF` surface, `#182430` ink, `#5B6B78` secondary ink, `#0F6E6A` teal accent, `#DDE4E8` hairline borders, `#8A4A3A` error, `#2E7D5B` success.
- **Type:** IBM Plex Sans (UI/body) + IBM Plex Mono (used specifically for data values — DOB, timestamps, status — not decoratively).
- **Layout:** single 680px column, left-aligned, hairline dividers instead of card shadows, slim persistent top bar.
- Avoided common AI-generated-page tells: no cream+terracotta or dark+neon defaults, no SaaS card-grid with shadows, no ALL-CAPS eyebrow labels, no arrow-suffixed buttons/links.

### Notable bugs hit & fixed
- **Hydration mismatch**: `getUserEmail()` called directly in JSX read `localStorage` during SSR (empty) vs. client render (populated) → mismatch. Fixed by moving the read into `useEffect` and storing in state, so both the server and first client render agree (empty), then update after mount.
- **`net::ERR_CONNECTION_REFUSED`**: turned out to simply be the .NET API not running — not a CORS or code issue.

---

## Part 4 — Medical Document Management 🚧 (in progress)

Architecture: `Document → R2 → Document Processor → Extracted Text → PostgreSQL metadata`

Planned/partially complete:
- Cloudflare R2 bucket `clinical-documents` (free tier, required subscribing to the free plan first) + scoped API token (Object Read & Write).
- `AWSSDK.S3` package added to .NET (R2 is S3-API-compatible; point the standard S3 client at `https://<account-id>.r2.cloudflarestorage.com` with `ForcePathStyle = true`).
- `Document` entity (`DocumentType`, `ProcessingStatus` enums, `ExtractedText`, `R2Key`) — **not yet added to the codebase**.
- `R2StorageService` (upload/download via `IAmazonS3`) — designed, not yet confirmed created.
- Python `document-processor` microservice (FastAPI, `/extract-text`, `/health`) using PyMuPDF for PDFs and python-docx for Word docs — designed, not yet confirmed running.
- `DocumentController` (`POST /api/document/upload`, `GET /api/document`, `GET /api/document/{id}`) — **confirmed NOT yet created** (frontend built ahead of this, causing a `404` on `/api/document` until this is completed).

### Remaining steps to close out Part 4
1. Finish R2 bucket + API token creation.
2. Add `AWSSDK.S3`, store R2 credentials via `dotnet user-secrets`.
3. Create `Document` entity + add `DbSet<Document>` to `ClinicalDbContext`.
4. Create `R2StorageService.cs`, register the R2-pointed `IAmazonS3` client in `Program.cs`.
5. Build and run the Python `document-processor` (`uvicorn main:app --port 8000`).
6. Create `DocumentController.cs` (upload → R2 → call Python processor → save metadata + extracted text).
7. `dotnet ef migrations add AddDocuments` → `dotnet ef database update`.
8. End-to-end test: upload a synthetic `.pdf`/`.txt` via the already-built React Documents page, confirm status reaches `Completed`, extracted text lands in Postgres, file object lands in R2.

---

## Environment & Tooling Notes (recurring gotchas)

- **Windows PowerShell execution policy** blocked `npm` initially — fixed with `Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned`.
- **PowerShell's `curl` is aliased to `Invoke-WebRequest`**, with completely different syntax from real curl — use `Invoke-RestMethod` instead, or `curl.exe` explicitly to bypass the alias.
- **`dotnet ef` migration commands run their own internal build** — a build failure there can be caused by a **stale/locked running process** (`ClinicalAI.Api.exe`) holding `obj/`/`bin/` files. This bit multiple times across the session: kill the process (`Get-Process ClinicalAI.Api | Stop-Process -Force`), then rebuild.
- **`dotnet run` port is randomized per project scaffold** (check `Properties/launchSettings.json`), not reliably `5001`/`5000`. Also easy to accidentally run only the `http` profile (no HTTPS at all) — check the actual "Now listening on" line every time rather than assuming.
- **`dotnet dev-certs https --trust`** needed once to get Chrome to trust the local HTTPS dev cert (optional — this project ultimately settled on plain HTTP for local dev to sidestep cert friction entirely).
- **Swashbuckle/Microsoft.OpenApi version mismatches**: `Swashbuckle.AspNetCore` 10.x pulls a breaking-change `Microsoft.OpenApi` 2.x, incompatible with .NET 10 SDK previews at time of writing. Resolution abandoned the custom Swagger Authorize-button code entirely in favor of Postman/`Invoke-RestMethod` with manual `Authorization: Bearer` headers for testing protected endpoints.
- **Supabase Direct connection defaults to IPv6**, which failed on this network; the **Session pooler** (IPv4) was used instead, with explicit `SSL Mode=Require;Trust Server Certificate=true` for the TLS/SNI handshake Supavisor needs to route to the correct tenant.
- **`.env.local` isn't auto-created** by `create-next-app` — must be created manually, and Next.js only reads it at server startup (restart required after changes).
- Real secrets (Supabase JWT secret, DB password) were pasted into this chat during troubleshooting — flagged for rotation as a security hygiene practice, since this is a learning project (low real-world stakes, but good habit to build).

---

---

## Maintenance pass — 2026-09-13

A correctness/security pass over the state of the tree at the end of Part 3. Notes above this
line are the original build log and were partly stale; this section records the actual state.

### Startup was broken
`DbSet<Document>` had been added to `ClinicalDbContext` without a matching migration. EF Core 10
raises `PendingModelChangesWarning` **as an error** inside `Database.Migrate()`, so `Program.cs`
threw before Kestrel ever bound a port:

```
System.InvalidOperationException: An error was generated for warning
'Microsoft.EntityFrameworkCore.Migrations.PendingModelChangesWarning'
```

This — not the missing `DocumentController` — was the real cause of the frontend failures. The
whole API was down, so *every* endpoint failed, not just `/api/document`.

Fixed with `dotnet ef migrations add AddDocuments`. The migration applied cleanly on next run and
created the `Documents` table. Useful check for next time, no DB connection needed:

```
dotnet ef migrations has-pending-model-changes
```

Note it builds first by default — with `--no-build` it reads a stale assembly and reports a false
positive right after adding a migration.

### Security fixes
- **`BedrockController` had no `[Authorize]`.** `POST /api/bedrock/invoke` was an open,
  unauthenticated proxy to Bedrock — any caller could spend account quota on arbitrary prompts,
  with no redaction and no audit trail. Now `[Authorize(Roles = "doctor")]` like
  `PatientController`, and it writes an `InvokeBedrock` audit row. Verified: `401` unauthenticated
  (was `200`).
- **Secrets moved out of the tree.** The Supabase connection string (with password) was in
  `appsettings.Development.json`; R2 keys and a live Cloudflare API token were in
  `secrets/r2 1.secret`. All now in `dotnet user-secrets` alongside the JWT secret that was
  already there. Added a root `.gitignore` (there was none outside `frontend/`) covering
  `secrets/`, `*.secret`, `bin/`, `obj/`, `node_modules/`, `.env*`.
- **Still to do: rotate.** These credentials existed in plaintext on disk; `.gitignore` prevents
  future exposure but does not undo past exposure.

### Correctness fixes
- `PatientSummaryService` sent `Allergies` to the model **unredacted** while every other free-text
  field went through `RedactFreeText`. Now all four go through a `Field()` helper that also
  restores the "None recorded" fallback for empty values (previously they rendered as a blank line).
- Age was `UtcNow.Year - DateOfBirth.Year` — a year subtraction, wrong by one before a birthday.
  Now a real age calculation.
- `apiFetch` sent the literal header `Authorization: Bearer null` when no token was present; the
  header is now omitted entirely so the API sees a missing token rather than a malformed one.

### The design system was never loaded
`app/globals.css` (the full palette, `.record-row`, `.status-pill`, `.summary-box`, …) was never
imported — `layout.tsx` had no `import './globals.css'`, and **no component used `className` at
all**; all four pages used inline `style={{…}}`. The app had been rendering as unstyled browser
defaults the whole time. Added the import and converted all four pages onto the existing classes.
Added `.page-narrow` (sign-in) and `.empty-note` (empty states) to cover cases the original CSS
didn't have.

### Layout correction
`BedrockService.cs` and `R2StorageService.cs` moved from `Services/` into `Application/Services/`;
the `Services/` folder is gone and the namespace outlier noted in Part 1 is resolved.

### Still open
- **The R2 bucket does not match Part 4's description.** Part 4 describes `clinical-documents` for
  PDFs/Word docs. The only credentials in the repo point at **`live-assist-audio`**, which holds 8
  MP3 session recordings keyed by real-looking personal email addresses
  (`{email}/{date}/session-{HH-MM-SS}/session.mp3`, 79.5 MB). That sits badly against the
  synthetic-data-only boundary. `R2StorageService` is still an empty stub and the `IAmazonS3`
  registration in `Program.cs` is still commented out, pending this decision.
- `DocumentController` still does not exist; `/api/document` returns `404`. The Documents page now
  detects the 404 and says so rather than silently showing an empty list.
- The `document-processor/` Python microservice does not exist on disk.
- `WeatherForecastController.cs` / `WeatherForecast.cs` scaffold leftovers are still present.

---

## Next up: finish Part 4, then Part 5 (Custom RAG with chunking, embeddings, and Qdrant Cloud).
