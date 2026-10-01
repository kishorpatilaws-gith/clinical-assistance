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

---

## Part 4 — Medical Document Management ✅ (2026-09-15)

Architecture delivered: `Document → R2 → Python processor → Extracted text → Postgres`.

### 4.0 Terraform introduced
Terraform enters here, not at Part 1, because this is the first part that creates
durable cloud infrastructure. Bedrock is a pure API call and Supabase was made in a
dashboard — there was nothing to declare before now. `infra/` pins
`cloudflare ~> 5.25` and `aws ~> 6.64`; the Cloudflare token comes from
`CLOUDFLARE_API_TOKEN` rather than a variable, so it never reaches tfvars, state or
plan output. Lock file committed on purpose; `.terraform/`, `*.tfstate`, `*.tfvars`
ignored.

### 4.1 Terraform — done, by import
A working Cloudflare API token turned up (`cfut_…`), so this got finished rather than
deferred. The bucket already existed, so Terraform adopted it:

```
terraform import cloudflare_r2_bucket.clinical_documents   <account_id>/clinical-documents/default
terraform plan   ->  No changes. Your infrastructure matches the configuration.
```

Four things worth remembering:

- The import ID takes **three** urlencoded segments — `<account_id>/<bucket>/<jurisdiction>`.
  Two segments fails with `invalid ID`; the jurisdiction is `default`.
- Keep the **`cfut_` prefix**. It is part of the token; stripping it returns
  `Invalid API Token`. An earlier assumption that Cloudflare tokens are a bare 40
  characters was simply wrong — the older `cfat_…` values in the secrets files were
  invalid tokens, not mis-formatted ones.
- The **S3 keys cannot do this job.** They address objects inside a bucket; managing
  the bucket itself needs a Cloudflare API token. Different screens in the dashboard,
  different auth systems, not interchangeable.
- Pointing the **AWS** provider at the R2 endpoint as a workaround does not work:
  `aws_s3_bucket` calls `GetBucketAcl` on refresh, which R2 does not implement (403).

State was checked for leakage — no token, no S3 keys. `terraform.tfstate` and
`terraform.tfvars` are gitignored; the lock file and `.tf` files are committed.

### On S3 appearing in a Cloudflare project
Worth stating plainly, because it reads like a contradiction. "S3" is two things: the
Amazon product, and the de-facto object-storage protocol. R2 implements the protocol
so existing tooling works unchanged. `AWSSDK.S3` here is a *protocol client* aimed at
`r2.cloudflarestorage.com` — no bytes reach Amazon, and there is no AWS storage bill.
Same shape as using Npgsql against Supabase. Bedrock remains the only genuine AWS
dependency in the project.

### 4.2 The actual blocker
`AWSSDK.S3` had never been added to the csproj, which is why the `IAmazonS3`
registration sat commented out — that code could not compile. Added, and the client
is now wired to the R2 endpoint with `ForcePathStyle` and `AuthenticationRegion =
"auto"`.

**Non-obvious:** the v4 AWS SDK attaches checksum headers to every request by
default and R2 does not implement them. Without
`RequestChecksumCalculation = WHEN_REQUIRED` (and the response equivalent), uploads
fail on a checksum/signature error.

### 4.3 R2StorageService + xUnit
Upload/download/delete against `clinical-documents`. Keys are
`{documentType}/{date}/{guid}{ext}` — deliberately **not** keyed by user identity,
unlike the older audio bucket, because keys surface in logs, metrics and error
messages. The original filename travels as object metadata instead.

xUnit finally enters the repo, closing the Part 1 gap: 6 tests, including a real R2
round-trip asserting byte equality. xunit 2.x has no runtime skip, so a custom
`RequiresR2Attribute` sets `Skip` at construction — an unconfigured machine reports
skipped rather than silently passing.

### 4.4 Python document processor
FastAPI on :8000, `/health` and `/extract-text`, dispatching to PyMuPDF, python-docx
or plain decode. PyMuPDF 1.28.2 and python-docx 1.2.0 both install cleanly on Python
3.14 — no downgrade needed. Use `import pymupdf`; `import fitz` is deprecated.

DOCX extraction reads **table cells as well as paragraphs** — clinical documents
carry a lot of content in tables and paragraph-only extraction drops it silently.

### 4.5 DocumentController
`POST /api/document/upload`, `GET /api/document`, `GET /api/document/{id}`, all
`[Authorize(Roles = "doctor")]` with an audit row each. Store to R2 first, then
extract: if extraction fails the row is marked `Failed` and the file is still safe
for reprocessing, rather than 500-ing a request whose upload actually succeeded.

**Regression caught and fixed:** `[FromForm]` on an `IFormFile` parameter makes
Swashbuckle's generator throw, which returns 500 for `/swagger/v1/swagger.json`
across the *whole* API, not just that endpoint. `IFormFile` binds from form data
without the attribute.

### 4.6 Frontend
The "Part 4 is unfinished" branch removed; rows now show type, character count and a
status pill.

**Fixed a latent CSS bug** the browser test exposed: `.btn:disabled` (specificity
0,2,0) outranks `.btn-primary` (0,1,0) on `color`, so a disabled Upload button was
painted `--ink-secondary` on an `--ink-secondary` background — an empty grey block
with an invisible label.

### 4.7 Verified end to end
Driven through a real browser (Playwright + Edge), not just curl:
login → patients → documents → upload → row appears `Completed`, zero console errors.
Each hop confirmed independently rather than inferred from the UI:

| Check | Result |
|---|---|
| Object in R2 | `ClinicalGuideline/2026-09-15/6153e9…pdf`, 1469 bytes |
| Postgres row | status=2, 128 chars extracted |
| Extracted text | returned by `GET /api/document/{id}` |
| Audit rows | UploadDocument / ListDocuments / ViewDocument |
| Rejected `.exe` | 400 with a readable reason |
| Unauthenticated | 401 |

### Still open after Part 4
- `WeatherForecast` scaffold leftovers
- HTTPS redirect still disabled from Part 3
- Terraform for the AWS side, which arrives with Part 6

---

---

## Part 5 — Custom RAG (in progress)

### 5.0 Chunking ✅
Boundary-aware splitting in `chunking.py`: paragraph breaks first, sentence ends as
fallback, hard cut only when one sentence exceeds the target. A chunk that starts
mid-sentence embeds a fragment nobody wrote and retrieves badly. Overlap is trimmed
to a word boundary so a passage split across a boundary stays findable from either
side. 12 tests.

**A test caught a real bug.** The abbreviation guard never fired: the lookbehinds sat
before `(?<=[.!?])`, so at the split point they inspected `"r."` rather than `"Dr"`.
"Dr. Nair" was being treated as a sentence end. The lookbehinds must include the
trailing dot.

### 5.1 Embeddings ✅
`all-MiniLM-L6-v2`, 384 dimensions, CPU, via sentence-transformers. 11 tests.

- **CPU-only torch.** The default PyPI wheel bundles CUDA (~2.5GB), useless without an
  NVIDIA GPU — this machine has Intel HD 4000 / AMD Radeon HD 8500M. Installing from
  `https://download.pytorch.org/whl/cpu` gives `torch 2.14.0+cpu` at ~250MB; the venv
  landed at 1.2GB instead of 3GB+.
- **Vectors normalised to unit length**, so cosine similarity is a plain dot product.
  Qdrant uses cosine distance in 5.2; unnormalised vectors rank subtly wrong.
- **Model loads at startup**, not on first request, behind a double-checked lock —
  uvicorn is concurrent and two threads racing the first load would fetch it twice.
- **`/health` reports the model name.** Vectors from different models are not
  comparable; mixing them returns confident nonsense, so the producing model has to
  be visible.

Endpoints added: `POST /embed` (queries) and `POST /process` (extract + chunk + embed
in one call — the ingestion path, so the file bytes stay on the Python side).

**Measured on this hardware** (dual-core, `torch.get_num_threads() == 2`): 5-page PDF
→ 1337 chars → 4 chunks, extract+chunk+embed in ~900ms warm. Cold model load 26.7s
including the 90MB download; ~8s warm at startup.

**Retrieval sanity check** — four natural-language questions against the four chunks
of a synthetic thyroid guideline, all four retrieved the correct chunk:

| Query | Top chunk | Score |
|---|---|---|
| recheck thyroid bloods after dose change | MONITORING | 0.547 |
| drugs stopping levothyroxine absorption | ABSORPTION | 0.602 |
| emergency warning signs | RED FLAGS | 0.197 |
| allergic to antibiotics | PENICILLIN ALLERGY | 0.510 |

Two things to carry into Part 11's evaluation. Absolute scores are **not calibrated** —
the emergency query ranked correctly at 0.197 while an incorrect match elsewhere
scored 0.28, so a fixed similarity threshold would be wrong. And chunk 3 merged RED
FLAGS with PENICILLIN ALLERGY, two unrelated topics, because chunk size was set by
character count rather than document structure.

### 5.2 Qdrant Cloud + 5.3 retrieval ✅
Free cluster (v1.19.1, eu-central-1), collection `clinical_chunks`, 384d, cosine.
Credentials in a gitignored `.env`; `.env.example` committed. 11 tests, skipped
automatically when the cluster is not configured.

- **Point IDs are `uuid5(ns, "{document_id}:{chunk_index}")`** — deterministic, so
  re-indexing replaces rather than appends. Verified: re-indexing held the count at 4.
  Without this every re-upload silently doubles a chunk's weight in retrieval.
- **`document_id` comes from the caller** (the Postgres row id) so the vector store and
  the relational record stay in step, and deleting a document can clear its chunks.
- Payload carries file name, type, chunk index and offsets — enough to render a
  citation without a second trip to Postgres.
- Endpoints: `POST /index`, `POST /search`, `DELETE /index/{id}`; `/health` reports
  collection stats. Qdrant being down degrades to extraction-only rather than failing
  startup.

**Retrieval is real but top-1 is not trustworthy.** For *"which drugs reduce
levothyroxine absorption"*, the introduction chunk outranks the chunk that answers it,
at both chunk sizes tested:

```
chunk_size=500:  0.598 [0] intro     0.557 [2] <- the answer
chunk_size=300:  0.600 [0] intro     0.388 [2] <- the answer
```

Short, topically-central chunks act as attractors for any on-topic query. The answer
was in the top 2 in both cases, so retrieving K>=3 and letting the model choose is
sound — trusting rank 1 is not. Part 11 should measure **recall@k**, not accuracy@1.

**A verification lesson.** An earlier check asserted the expected section name appeared
in the top chunk's text. It passed 4/4 — but chunk 2 ends with the "RED FLAGS" heading
and chunk 3 opens with overlap from it, so the substring matched for the wrong reason.
Rewritten to assert on `chunk_index` against hand-verified ground truth. The result
still held, but the method was unsound.

### 5.4 RAG endpoint ✅
Question -> retrieve top-K -> context -> Bedrock -> cited answer. 9 new xUnit tests
(15 total on the .NET side).

**Bedrock is called from .NET, not Python.** Deliberate: generating clinical text has
to sit behind the same authorization, audit and redaction as everything else, and
Bedrock was already wired up on that side since Part 1. Doing it in Python would have
needed a second credential path and repeated the unauthenticated `BedrockController`
mistake from Part 4. Python stays a focused extraction and retrieval service.

**Upload now indexes.** `DocumentProcessorClient` calls `/index` instead of
`/extract-text`, passing the Postgres row id through as the vector store's
`document_id` so the two stay in step. `/index` returns the extracted text as well,
so one round trip does extraction, chunking, embedding, storage and persistence.

Added `POST /api/document/reindex` (backfill for documents predating the pipeline)
and `DELETE /api/document/{id}` (clears vectors *before* the R2 object - a stale index
citing deleted text is worse than an orphaned object).

**The prompt contract** is where trustworthiness is decided, so it is unit-tested
independently of whether the model behaves on a given day. Three rules, each guarding
a failure: answer only from the passages; say so explicitly when they do not answer;
considerations not diagnosis. Citations are numbered markers mapped back to file name
and character offsets.

**Backfill result** — 4 documents, 16 chunks:

| Document | Chunks |
|---|---|
| guideline.pdf | 1 |
| lab-report.docx | 1 |
| notes.txt | 1 |
| Disease and Medicine.docx | 13 |

**Live results.** A grounded answer, correctly cited:

> *"What is the difference between osteoarthritis and rheumatoid arthritis?"*
> "Osteoarthritis is a wear-and-tear condition with morning stiffness typically
> lasting under 30 minutes... Rheumatoid arthritis is an autoimmune condition with
> prolonged morning stiffness lasting over 1 hour, frequently affecting symmetrical
> joints bilaterally [1]." — cited to chunk 12, which is where that text lives.

And the refusal gate, which is the more important result:

> *"What is the recommended first-line treatment for malaria?"* -> refused
> *"What antibiotic dose should I prescribe for this patient?"* -> refused

Both refused **while retrieval returned passages** (top scores 0.348 and 0.428). The
refusal is the prompt contract working, not an artifact of an empty result set — worth
distinguishing, because an empty-result refusal proves nothing.

**An orphan caught by accident.** Testing `/index` directly with a made-up document id
left 4 vectors with no Postgres row, and they surfaced in a later answer's sources.
Removed via `delete_document`. The index and the relational store can drift, and only
the delete path keeps them together — which is why `DELETE /api/document/{id}` clears
vectors first.

### 5.5 Ask UI ✅
`/ask` — question, answer, verifiable citations. 16 browser assertions, no console errors.

**Citation markers are the mechanism, not decoration.** The answer's `[1]` markers are
parsed out and rendered as buttons; clicking one expands that passage in place with
file name, chunk index, character offsets and score. Evidence sits against the claim
it supports rather than in a list below that the eye skips — which is what makes an
answer checkable rather than merely readable.

The parser handles `[1]`, `[1][2]` and `[1, 2]`, and falls back to plain text rather
than mangling an answer it cannot parse.

**A refusal is styled as a correct outcome, not an error** — neutral grey rule, not the
danger colour, with a note explaining that passages were retrieved but none answered.
Given how thin the corpus is, refusals are common and the UI has to make that read as
a working system.

Retrieved passages sit behind a collapsed disclosure ("5 passages retrieved — show what
the model was given"). Since retrieval measurably does not always rank the answering
passage first, seeing the full context is diagnostic when an answer looks thin — but it
must not compete with the answer for attention.

Optional per-document scoping uses the `documentId` filter. Verified: scoping to
`notes.txt` refuses an arthritis question the unscoped search answers.

**Two bugs the tests and screenshot caught:**

- The page rendered **"0 indexed"** before the document list arrived — stating something
  untrue during the fetch. Now distinguishes "none" from "not yet known", and the scope
  dropdown stays disabled until loaded.
- The footer read **"5 passages cited"** when the answer cited only `[1]`. `sources` is
  every *retrieved* passage numbered for the model to choose from, not the subset it
  used. Counting it as "cited" overstates how well grounded the answer is — now "1 of 5
  passages cited", counting distinct markers actually present in the text.

### 5.6 Evaluation harness ✅
`document-processor/eval/` — 15 answerable questions with hand-verified ground truth
plus 6 deliberately unanswerable. Two runners: retrieval (free, hits `/search`) and
end-to-end (one Bedrock call per question, hits the real .NET endpoint so it measures
the system a clinician uses, not a reimplementation).

Questions were **hand-written from the chunk text before anything was run through
retrieval**. LLM-generated questions reuse a chunk's own vocabulary and flatter the
retriever.

**Retrieval**

| recall@1 | recall@3 | recall@5 | recall@10 | MRR | median |
|---|---|---|---|---|---|
| **0.467** | 0.867 | **0.933** | 1.000 | 0.660 | 469 ms |

The recall@1→@5 gap is the headline: the answering passage is first less than half the
time, but in the top five 93% of the time. That is the measured justification for
K=5 — retrieving one passage would be wrong more often than right. The hunch from 5.3
now has a number behind it.

**End to end**

| false answer | false refusal | citations valid | cited correct | median | p90 |
|---|---|---|---|---|---|
| **0.000** | 0.067 | 1.000 | 0.800 | 1441 ms | 4109 ms |

**Zero hallucinations** across all six unanswerable questions, and every citation
marker resolved to a supplied passage. Those are the two numbers that matter for
clinical use.

**The single false refusal is the most interesting result.** `a14` — *"Does paracetamol
bring down swelling as well as pain?"* — was refused despite retrieval supplying
*"Acetaminophen: Tylenol for targeted pain management (does not reduce inflammation)"*
at rank 3. The question says **paracetamol**, the document says **acetaminophen**. Same
drug, different name; the model would not bridge the synonym because it was told to use
only the passages.

That is the cost of strict grounding, made visible: a clinician was denied information
sitting in their own library. Options later are query expansion over drug synonyms, or
a softer instruction accepting some hallucination risk. Worth noting which way the
current setting errs — toward refusing rather than inventing, which is the right
direction here.

**Caveat recorded in the report itself:** 16 chunks across 4 documents is small enough
that random guessing scores recall@5 ≈ 0.31. The harness is the deliverable; Part 11
re-runs it on a larger corpus.

**Not measured:** answer faithfulness — whether a cited passage *justifies* the claim,
as opposed to merely *resolving*. That needs an LLM judge and belongs to Part 11.

---

## Part 5 complete

Documents → chunking → embeddings → Qdrant → similarity search → top-K context →
Bedrock → cited answer, with a UI and measured baselines. Part 6 replaces the custom
pipeline with Bedrock Knowledge Bases and now has real numbers to compare against.

---

## Next up: Part 6 — AWS-native RAG with Bedrock Knowledge Bases, compared against the 5.6 baseline.
