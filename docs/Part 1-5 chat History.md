# Part 1–5 — Chat History

A record of the assisted working sessions that took the platform from "Parts 1–3 done,
Part 4 stalled" to "Part 5 complete with measured baselines".

**Sessions covered:** 13 September – 29 September 2026
**Assistant:** Claude Opus 5 via Claude Code

> **Credentials are redacted throughout.** Several live secrets were pasted into the
> chat during these sessions — R2 access keys, a Qdrant API key, the Supabase database
> password, a Cloudflare API token. This file lives in a tracked directory, so every one
> of them appears here as a placeholder. The real values are in `dotnet user-secrets`,
> `document-processor/.env`, and the gitignored `secrets/` folder.
>
> Those credentials existed in plaintext on disk and in chat transcripts. `.gitignore`
> prevents future exposure but does not undo past exposure — **rotation is still
> outstanding.**

---

## Contents

1. [R2 connectivity check](#1--r2-connectivity-check)
2. [Codebase audit](#2--codebase-audit)
3. [Fixing what the audit found](#3--fixing-what-the-audit-found)
4. [Verifying against the assignment PDF](#4--verifying-against-the-assignment-pdf)
5. [Roadmap and the Terraform question](#5--roadmap-and-the-terraform-question)
6. [Part 4 — document management](#6--part-4--document-management)
7. [Part 4.1 — Terraform import](#7--part-41--terraform-import)
8. [Session expiry bug](#8--session-expiry-bug)
9. [Part 5 — custom RAG](#9--part-5--custom-rag)
10. [Architecture document](#10--architecture-document)
11. [Run scripts](#11--run-scripts)
12. [Recurring themes](#12--recurring-themes)
13. [Still outstanding](#13--still-outstanding)

---

## 1 — R2 connectivity check

**Asked:** test the R2 credentials in `secrets/r2 1.secret` and report folder and file counts.

**Result:** bucket `live-assist-audio` reachable, region APAC. 8 objects, 79.48 MB, 16
distinct key prefixes across 3 top-level folders.

The layout was `{user-email}/{date}/session-{HH-MM-SS}/session.mp3`.

Two observations that mattered later:

- `ListBuckets` returned **AccessDenied** while bucket-scoped operations succeeded —
  expected for a token scoped to one bucket, not a credential problem.
- The bucket was keyed by **real personal email addresses**, which sat badly against the
  project's synthetic-data-only rule. This later shaped the key scheme for the clinical
  documents bucket.

---

## 2 — Codebase audit

**Asked:** understand the notes document and the whole project.

### The blocking finding

**The API did not start at all.**

```
System.InvalidOperationException: An error was generated for warning
'Microsoft.EntityFrameworkCore.Migrations.PendingModelChangesWarning'
   at Program.<Main>$(String[] args) in Program.cs:line 110
```

`DbSet<Document>` had been added to `ClinicalDbContext` with no matching migration. EF
Core 10 treats that as an **error** inside `Database.Migrate()`, so the host threw before
binding a port.

This — not the missing `DocumentController` — was the real cause of the frontend
failures. Every endpoint was failing, not just `/api/document`.

### Documentation drift

The build log had diverged from the code in several places:

| Notes said | Reality |
|---|---|
| Model `amazon.nova-lite-v1:0` | `amazon.nova-pro-v1:0` |
| `Document` entity "not yet added" | Existed and was wired into the DbContext |
| `R2StorageService` "designed, not created" | Existed as an **empty class**, 93 bytes |
| Bucket `clinical-documents` | Credentials pointed at `live-assist-audio` |
| `document-processor/` Python service | **Did not exist anywhere on disk** |
| Swagger abandoned over a version clash | Working fine, 5 endpoints documented |

### Security findings

- **`/api/bedrock/invoke` had no `[Authorize]`** — an open, unauthenticated proxy to
  Bedrock. Anyone reachable could spend account quota on arbitrary prompts, with no
  redaction and no audit trail.
- **Live secrets in the tree** — the Supabase password in `appsettings.Development.json`,
  R2 keys and a Cloudflare token in `secrets/`. The only `.gitignore` was the frontend's.

### The design system was dead code

`app/globals.css` held a complete 174-line design system — palette, `.record-row`,
`.status-pill`, `.summary-box`. None of it ran: `layout.tsx` never imported it, and **no
component used `className` at all**. Every page used inline `style={{...}}`.

The app had been rendering as unstyled browser defaults the entire time.

---

## 3 — Fixing what the audit found

**Asked:** fix the issues and run the app locally.

| Fix | Detail |
|---|---|
| Migration | `dotnet ef migrations add AddDocuments` — applied cleanly, created the table |
| Bedrock endpoint | `[Authorize(Roles = "doctor")]` + audit row. Verified 401 where it had been 200 |
| Secrets | DB connection string and all four R2 values moved to `dotnet user-secrets`; root `.gitignore` added |
| Design system | Imported `globals.css`, converted all four pages onto the existing classes |
| Correctness | `Allergies` now redacted like the other free-text fields; real age calculation; `apiFetch` no longer sends `Bearer null` |
| Layout | `BedrockService` and `R2StorageService` moved into `Application/Services` |

### Verified end to end

Login → patients → summary all worked. A live Bedrock summary returned 1,120 characters,
correctly framed as considerations rather than diagnosis. 81 audit rows in Postgres,
correctly attributed.

### Two environment gotchas worth keeping

- **Windows PowerShell 5.1 cannot load .NET 10 assemblies** — `Add-Type` on `Npgsql.dll`
  fails with `ReflectionTypeLoadException`. Workaround: a .NET 10 file-based app
  (`dotnet run script.cs` with a `#:package` directive) for one-off database queries.
- **`dotnet ef migrations has-pending-model-changes` must not be run with `--no-build`** —
  it reads a stale assembly and reports a false positive immediately after adding a
  migration.

---

## 4 — Verifying against the assignment PDF

**Asked:** verify progress against the assignment, on the understanding that Parts 1–4
were complete.

**Finding: Part 4 was roughly 15% done, not complete.**

| Part | Goal | Stack |
|---|---|---|
| 1 Bedrock connectivity | ✅ verified | ⚠️ Terraform, xUnit absent |
| 2 Patient application | ✅ verified | ✅ complete |
| 3 Auth, privacy, security | ✅ verified | ⚠️ KMS, HTTPS, Terraform absent |
| 4 Document management | ❌ not functional | ❌ mostly missing |

### The root blocker

**`AWSSDK.S3` had never been added to the csproj.** That is why the `IAmazonS3`
registration in `Program.cs` sat commented out — the code could not compile. The notes
listed "Add AWSSDK.S3" as step 2 of Part 4; it never happened.

### Cost optimisation was already the assignment's design

The move off AWS to Supabase, R2 and Qdrant was not a deviation. Page 9 of the PDF
prescribes exactly that:

> **High priority** — Bedrock / Agents / Knowledge Bases / Guardrails / Embeddings /
> Evaluation: *"Spend credits here; these are the main learning targets."*
> **Free/Low** — *"Supabase / R2 / Qdrant / Vercel / Upstash / Grafana / GitHub."*

### Numbering caveat

The PDF is internally inconsistent. **PART 4** is Document Management, but the "Final
Learning Journey" on page 9 lists step 4 as *Privacy + Security* and step 5 as *Document
Processing*. Tracking against the journey list would make "completed part 4" accurate.

---

## 5 — Roadmap and the Terraform question

**Asked:** what is the plan for Part 4, and is Terraform needed for parts 1, 2 and 4?

### Why Terraform had not appeared

Terraform declares *resources that exist*. Parts 1–3 create almost none: Bedrock is a
pure API call with no infrastructure to provision, and Supabase was created by clicking
in a dashboard. There was genuinely nothing to declare.

**Part 4 is the first part that creates durable cloud infrastructure**, which made it the
honest place to start.

### Decisions taken

| Question | Choice |
|---|---|
| R2 bucket | New `clinical-documents`, keeping synthetic documents away from the audio data |
| Terraform scope | All providers, starting at Part 4 |
| Python processor | Local uvicorn over HTTP, matching the assignment |

### Toolchain risks checked up front

- **PyMuPDF on Python 3.14** — installed and imported cleanly (1.28.2). No downgrade
  needed. `import fitz` is deprecated; use `import pymupdf`.
- A roadmap artifact was published covering Parts 4–13 with a verification gate per step.

### 4.0 — Terraform introduced

Terraform 1.16.2 installed (SHA256 verified), `infra/` scaffolded with pinned providers
`cloudflare ~> 5.25` and `aws ~> 6.64`. No resources declared — the step existed so 4.1
would be reproducible.

Two habits established:

- **The Cloudflare token is not a Terraform variable.** It is read from
  `CLOUDFLARE_API_TOKEN`. A variable would land in the tfvars file, the state file and
  plan output.
- **The lock file is committed on purpose.** `.terraform/`, `*.tfstate` and `*.tfvars`
  are ignored; `.terraform.lock.hcl` pins provider versions and hashes.

Git was also added to PATH at this point — it was installed but unreachable, which would
have blocked the commit-at-each-gate loop.

---

## 6 — Part 4 — document management

**Asked:** move on Part 4.

4.1 was blocked on a Cloudflare token, so everything that did not depend on it was built
first.

### What was delivered

| Step | Work | Gate |
|---|---|---|
| 4.2 | `AWSSDK.S3` added, R2 client wired | Build clean, API starts |
| 4.3 | `R2StorageService` implemented + xUnit introduced | **6/6 tests**, real R2 round trip |
| 4.4 | Python FastAPI processor | PDF, DOCX, TXT extract; 415/400/422 correct |
| 4.5 | `DocumentController` | Upload → R2 → extract → Postgres |
| 4.6 | Frontend connected | Status pills, character counts |
| 4.7 | End-to-end in a browser | Zero console errors |

### Non-obvious findings

**The v4 AWS SDK breaks R2 by default.** It attaches checksum headers to every request
that R2 does not implement. Without `RequestChecksumCalculation = WHEN_REQUIRED` (and the
response equivalent), uploads fail on a checksum error.

**A regression was introduced and caught.** Putting `[FromForm]` on an `IFormFile`
parameter makes Swashbuckle's generator throw, which returned **500 for
`/swagger/v1/swagger.json` across the whole API** — not just that endpoint. It briefly
broke the working OpenAPI from Part 2. `IFormFile` binds from form data without the
attribute.

**A latent CSS bug surfaced.** `.btn:disabled` (specificity 0,2,0) outranks `.btn-primary`
(0,1,0) on `color`, so a disabled Upload button rendered `--ink-secondary` text on an
`--ink-secondary` background — an empty grey block with an invisible label. Pre-existing,
only visible once the stylesheet started loading.

### Object key design

Keys are `{documentType}/{yyyy-MM-dd}/{guid}{ext}` — deliberately **not** keyed by user
identity, unlike the audio bucket. Object keys surface in logs, metrics and error
messages, so identity in the path leaks everywhere the key travels. The original filename
travels as object metadata. A test asserts keys never contain `@`, a space, or a patient
surname.

### A process note

`RagServiceTests.cs` was later written to the wrong directory (`backend/` rather than the
nested test project), so the suite silently ran 6 tests instead of 15. **The count caught
it, not an error.**

---

## 7 — Part 4.1 — Terraform import

**Asked:** several rounds of credential testing, then "can you check the Terraform with
this?"

### Two invalid tokens, then a working one

The `cfat_…` values in both secret files returned `Invalid API Token` from
`/user/tokens/verify`. A later `cfut_…` token worked — **with its prefix intact**.

**A correction made during the session:** an earlier claim that valid Cloudflare tokens
are a bare 40 characters was wrong. The working token is 53 characters including its
prefix; stripping the prefix breaks it. The older tokens were simply invalid, not
mis-formatted.

### The workaround that does not work

Pointing the **AWS** provider at the R2 endpoint to manage the bucket with the S3 keys
already in hand fails on import: `aws_s3_bucket` calls `GetBucketAcl` on refresh, which
R2 does not implement (403). Tested, not assumed.

### The import

```
terraform import cloudflare_r2_bucket.clinical_documents \
  <account-id>/clinical-documents/default
→ Import successful!

terraform plan   → No changes. Your infrastructure matches the configuration.
terraform apply  → 0 added, 0 changed, 0 destroyed
```

`0 added` is the number that matters — Terraform **adopted** the existing bucket rather
than creating a second one.

**The import ID needs three segments**, not two: `<account_id>/<bucket>/<jurisdiction>`.
Two fails with `invalid ID`; jurisdiction is `default`.

The provider schema was read with `terraform providers schema -json` rather than guessed,
which is why `location` and `jurisdiction` were handled correctly first time.

State was checked for leakage — no token, no S3 keys.

### A clarification worth recording

Considerable confusion arose over **why S3 appears in a Cloudflare project**. The
resolution:

> "S3" is two things — the Amazon product, and the de-facto object-storage protocol. R2
> implements the protocol so existing tooling works unchanged. `AWSSDK.S3` here is a
> *protocol client* aimed at `r2.cloudflarestorage.com`. No bytes reach Amazon and there
> is no AWS storage bill. The same shape as using Npgsql against Supabase.

Also clarified: the **R2 API token** (S3 access key + secret, from R2 → Manage R2 API
Tokens) and the **Cloudflare API token** (Bearer, from My Profile → API Tokens) are
different auth systems. S3 keys address objects inside a bucket; the API token manages the
bucket itself. They are not interchangeable.

---

## 8 — Session expiry bug

**Reported:** a DevTools screenshot showing repeated `401 (Unauthorized)` on
`/api/patient/…`.

**Diagnosis: the login token had expired.** Supabase access tokens last 60 minutes; the
session had been open for hours. A freshly minted token returned 200 on the same patient.

Two real bugs behind it:

1. **The refresh token was thrown away.** Supabase returns one specifically so a session
   can be renewed; `lib/auth.ts` stored only the access token.
2. **The patient detail page had no 401 handling at all** — and its guard was
   `if (!getToken())`, which an *expired* token passes. The user got a raw status code
   with no way forward.

The three pages each behaved differently on 401. Fixed in one place — `apiFetch` now
renews proactively within 60s of expiry, refreshes and retries once on a 401, and clears
storage and redirects when renewal is impossible.

**11/11 browser assertions passed**, including a reproduction of the reported failure:
the same patient URL with a deliberately corrupted token produced a 1,325-character
summary with no error shown.

Two implementation details:

- **Refresh is a single shared promise.** Supabase refresh tokens are single-use;
  concurrent 401s would otherwise invalidate each other.
- The retry re-sends `options.body` — fine for FormData and strings, not for a
  single-use stream.

**Security note recorded:** the refresh token now sits in `localStorage` too, and it is
longer-lived than the access token it replaces. XSS exposure is somewhat worse, not
better. Production should use httpOnly cookies.

---

## 9 — Part 5 — custom RAG

### 5.0 Chunking

Boundary-aware splitting: paragraph breaks first, sentence ends as fallback, hard cut only
when one sentence exceeds the target. Overlap trimmed forward to a word boundary. **12
tests.**

**A test caught a real bug.** The abbreviation guard never fired — the lookbehinds sat
before `(?<=[.!?])`, so at the split point they inspected `"r."` rather than `"Dr"`. "Dr.
Nair" was being treated as a sentence end. The lookbehinds must include the trailing dot.

### 5.1 Embeddings

`all-MiniLM-L6-v2`, 384 dimensions, CPU. **11 tests.**

**The CPU-only wheel matters.** The default PyPI torch bundles CUDA (~2.5GB), useless
without an NVIDIA GPU — this machine has Intel HD 4000 and AMD Radeon HD 8500M.
Installing from `https://download.pytorch.org/whl/cpu` gives `torch 2.14.0+cpu` at ~250MB;
the venv landed at 1.2GB rather than 3GB+.

> **A correction made during the session:** "~2GB torch download" was stated three times
> before this was checked. That was the CUDA figure, wrong for this machine by an order of
> magnitude.

Vectors are normalised to unit length so cosine similarity is a plain dot product. The
model loads at startup behind a double-checked lock rather than on first request.

Measured on this hardware (`torch.get_num_threads() == 2`): 5-page PDF → 4 chunks,
extract + chunk + embed in ~900ms warm.

### 5.2 / 5.3 Qdrant and retrieval

Free cluster (v1.19.1, eu-central-1), collection `clinical_chunks`, 384d, cosine. **11
tests**, skipped automatically when credentials are absent.

**Point IDs are `uuid5(namespace, "{document_id}:{chunk_index}")`** — deterministic, so
re-indexing replaces rather than appends. Verified: re-indexing held the count at 4.
Without it, every re-upload would silently double a chunk's weight in retrieval.

**A verification lesson.** An early retrieval check asserted the expected section name
appeared in the top chunk's text. It reported 4/4 — but chunk 2 *ends* with the "RED
FLAGS" heading and chunk 3 *opens* with overlap from it, so the substring matched for the
wrong reason. Rewritten to assert on `chunk_index` against hand-verified ground truth. The
result held; the method did not.

### 5.4 RAG endpoint

**Bedrock is called from .NET, not Python** — a deliberate architectural decision.
Generating clinical text has to sit behind the same authorization, audit and redaction as
everything else, and Bedrock was already wired on that side. Doing it in Python would have
needed a second credential path and repeated the unauthenticated `BedrockController`
mistake.

Upload now calls `/index` rather than `/extract-text`, passing the Postgres row id through
as the vector store's `document_id`. Backfill indexed the four existing documents — **16
chunks**, 13 from "Disease and Medicine.docx".

**Live results.** A grounded answer on osteoarthritis vs rheumatoid arthritis, cited to
chunk 12 — where that text actually lives. And the refusal gate:

```
"first-line treatment for malaria?"        → "The provided documents do not address this question."
"what antibiotic dose should I prescribe?" → "The provided documents do not address this question."
```

Both refused **while retrieval returned passages** (top scores 0.348 and 0.428). A refusal
on empty results proves nothing; a refusal with passages in hand proves the prompt
contract holds.

**An orphan caught by accident.** Testing `/index` directly with an invented document id
left 4 vectors with no Postgres row, and they surfaced in a later answer's sources.
Removed. `DELETE /api/document/{id}` clears vectors *before* the R2 object for this
reason.

### 5.5 Ask UI

`/ask` — question, answer, verifiable citations. **16 browser assertions**, no console
errors.

Citation markers are parsed out of the answer and rendered as buttons; clicking one
expands that passage in place with file name, chunk index, offsets and score. Evidence
sits against the claim it supports rather than in a list below.

A refusal is styled as a correct outcome, not an error — neutral rule, not the danger
colour.

**Two bugs caught during testing:**

- The page rendered **"0 indexed"** while the document list was still in flight — stating
  something untrue. Now distinguishes "none" from "not yet known".
- The footer read **"5 passages cited"** when the answer cited only `[1]`. `sources` is
  every *retrieved* passage, not the subset used. Counting it as "cited" overstates how
  well grounded the answer is. Now "1 of 5 passages cited". **No assertion would have
  caught this — the screenshot did.**

### 5.6 Evaluation harness

15 hand-labelled answerable questions plus 6 deliberately unanswerable. Questions were
**hand-written from the chunk text before anything was run through retrieval** —
LLM-generated questions reuse a chunk's own vocabulary and flatter the retriever.

**Retrieval**

| recall@1 | recall@3 | recall@5 | recall@10 | MRR | median |
|---|---|---|---|---|---|
| **0.467** | 0.867 | **0.933** | 1.000 | 0.660 | 469 ms |

The recall@1 → recall@5 gap is the headline: the answering passage is first less than half
the time but in the top five 93% of the time. That is the measured justification for K=5.

**End to end**

| false answer | false refusal | citations valid | cited correct | median | p90 |
|---|---|---|---|---|---|
| **0.000** | 0.067 | 1.000 | 0.800 | 1441 ms | 4109 ms |

Zero hallucinations across all six unanswerable questions; every citation marker resolved.

**The single false refusal is the most instructive result.** *"Does paracetamol bring down
swelling as well as pain?"* was refused despite retrieval supplying *"Acetaminophen:
Tylenol for targeted pain management (does not reduce inflammation)"* at rank 3.

The question says **paracetamol**; the document says **acetaminophen**. Same drug,
different name, and the model would not bridge the synonym because it was told to use only
the passages.

That is the cost of strict grounding made visible. Note which way the setting errs —
toward refusing rather than inventing, which is the right direction for clinical use.

**Caveat recorded in the report itself:** 16 chunks across 4 documents is small enough that
random guessing scores recall@5 ≈ 0.31. The harness is the deliverable.

**Not measured:** answer faithfulness — whether a cited passage *justifies* a claim rather
than merely resolving. That needs an LLM judge and belongs to Part 11.

---

## 10 — Architecture document

**Asked:** a single document covering the RAG architecture, files, communication between
the server, the LLM and the vector database, ingestion, and workflow — with diagrams.

Published as an artifact with four hand-drawn inline SVG figures:

1. **Topology** — every arrow labelled, showing that the .NET API is the only component
   that reaches Bedrock and the Python service the only one that reaches Qdrant.
2. **Ingestion sequence** — nine hops, showing that the original file is stored before
   anything is parsed.
3. **Query sequence** — eleven steps from question to cited answer.
4. **Chunking with overlap** — the 900/150 scheme drawn to scale.

Plus the file map with line counts, both endpoint tables, configuration parameters,
failure modes, and the evaluation results.

One defect was caught by looking at the rendered page: in the chunking figure the
"chunk 2" label rendered *on top of* its own bar, because the bars ran to the edge of the
viewBox. Re-laid out with a dedicated label column.

---

## 11 — Run scripts

Three requests to "run the application" in a row established that **processes started
inside an assistant session are killed when the turn ends** — so anything started that way
is temporary regardless of how healthy it looks when reported.

Two scripts were added at the repo root:

| Script | Usage |
|---|---|
| `run.ps1` | `.\run.ps1` / `.\run.ps1 -Stop` |
| `run.bat` | `run.bat` / `run.bat stop` |

Both open one titled window per service, skip anything already listening, wait for all
three to bind, and report status.

**A bug caught in testing the batch file:** `timeout /t 1 /nobreak` fails with
`ERROR: Input redirection is not supported` whenever stdin is redirected — which happens
any time the script is called from another script or from CI. Because it exits instantly,
the wait loop burned through with no actual waiting. Replaced with
`ping -n 2 127.0.0.1 >nul`, the portable batch sleep.

That masked a second thing: the processor was reported DOWN when it was still loading.
**Cold start is closer to 40 seconds than the ~8s measured earlier**, because Hugging Face
performs hub checks before using the cache.

---

## 12 — Recurring themes

Patterns worth carrying into Part 6 and beyond.

**Verification methods failed more often than the code did.** The substring-based
retrieval check passed for the wrong reason. The test file written to the wrong directory
silently ran a third of the suite. `--no-build` reported a false positive on pending
migrations. In each case the code was fine and the check was not.

**Screenshots caught what assertions could not.** The "5 passages cited" miscount and the
invisible disabled-button label were both invisible to green test suites.

**Working code is not connected code.** The design system existed but was never imported.
The Document entity existed but had no migration. The document processor was described in
the notes but did not exist. "Present in the repo" and "running" are different questions.

**Measure on the real machine.** Torch download size, model load time, thread count and
end-to-end latency all differed materially from published figures.

**Three corrections were made mid-project**, recorded here so the wrong version does not
survive in the notes:

- Cloudflare tokens are *not* a bare 40 characters — the `cfut_` prefix is part of the token.
- Torch is ~250MB from the CPU index, not ~2GB.
- The first 4/4 retrieval result was correct, but the substring check that produced it was not sound.

---

## 13 — Still outstanding

| Item | Note |
|---|---|
| **Rotate credentials** | R2 keys, Qdrant key, Supabase password and Cloudflare token all appeared in plaintext on disk and in chat |
| HTTPS redirect | `app.UseHttpsRedirection()` still commented out from Part 3 |
| AWS KMS | Listed in Part 3's stack, never used |
| `WeatherForecast` scaffold | Leftover controller and model still present |
| Larger eval corpus | 16 chunks is too small for the metrics to be conclusive |
| Answer faithfulness | Needs an LLM judge — Part 11 |
| httpOnly cookies | Both tokens currently in `localStorage` — Part 12 |

**Next:** Part 6 — replace the custom pipeline with Bedrock Knowledge Bases and compare
against the 5.6 baseline (recall@5 0.933, zero hallucinations, 1441 ms median).
