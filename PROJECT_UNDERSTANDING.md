# Clinical AI Platform: A Simple Guide

## What this project does

This is a learning project for a clinical support tool. A clinician can sign in, view synthetic patient records, ask for an AI-generated patient summary, upload clinical documents, and ask questions about those documents.

The AI is intended to help a licensed clinician review information. It is not intended to diagnose a patient or prescribe treatment. The project notes say to use synthetic data only.

## How the pieces fit together

The project has three applications that run separately during local development:

- **Web app** (`frontend/clinical-ai-ui`): the screens the clinician uses. It is built with Next.js and TypeScript.
- **Main API** (`backend/ClinicalAI.Api`): checks access, reads and writes patient/document records, records audit events, and calls the AI or document processor. It is built with ASP.NET Core and C#.
- **Document processor** (`document-processor`): reads PDF, DOCX and text files, splits text into smaller pieces, turns those pieces into searchable number patterns (embeddings), and searches them. It is built with Python and FastAPI.

The services use external systems: Supabase for login and PostgreSQL, Cloudflare R2 for original uploaded files, Qdrant for document search, and Amazon Bedrock for AI text generation. Terraform files manage some infrastructure, including the R2 bucket; the infrastructure README describes additional parts as future work.

## What happens in the main user flows

### 1. Signing in

The browser sends the email and password directly to Supabase. Supabase returns a token, which the web app stores in browser local storage. Later, the web app sends that token with API requests. The API checks that the token is valid and that the user has the `doctor` role.

Signing in and having permission are separate checks: an account can sign in but still be denied access to doctor-only API features.

### 2. Viewing patients

The patient page asks the API for a list. The API returns a small set of fields for each synthetic patient and writes an audit record that the list was viewed. A clinician can open a patient page and request an AI summary.

### 3. Generating a patient summary

The API reads the patient's record, calculates their age, and sends clinical details to Bedrock. It leaves the patient's first and last name out of the prompt and tries to remove email, phone-number, and some US Social Security number patterns from free text. Bedrock returns a summary, considerations, and possible red flags for clinician review. The API records a successful summary request in its audit log.

This summary uses the patient record directly. It does not search uploaded documents or provide document citations.

### 4. Uploading a document

The web app sends a file and its document type to the API. The API checks the file, stores the original in R2, and saves document information in PostgreSQL. It then sends the file to the Python processor. The processor extracts text, splits it into overlapping sections, creates embeddings, and stores searchable sections in Qdrant. The API saves the extracted text and reports whether processing completed or failed.

The supported formats include PDF, DOCX, and plain text formats. The code enforces a 20 MB limit by default.

### 5. Asking a question about documents

The clinician submits a question. The API removes some identifying patterns from the question, and the processor searches Qdrant for relevant document sections. The API sends those sections and the question to Bedrock with instructions to use only the supplied text, cite claims, and say when the documents do not contain an answer. The response includes source details and the passages that were retrieved so the clinician can inspect them.

This document question flow is different from patient summaries: it is grounded in retrieved passages and returns citations, while the patient summary flow is based on one patient record.

## Privacy, access, and safety controls in the code

- Patient, document, and document-question endpoints require a valid token with the doctor role.
- Some user actions are written to an audit table, including patient listing, summaries, and document operations.
- The patient summary prompt excludes first and last names and redacts a few common identifier patterns in free text.
- Original files are stored in R2; document text and metadata are stored in PostgreSQL; searchable document sections are stored in Qdrant.
- The AI prompts ask for clinician-facing support, not diagnoses or prescriptions. The document question prompt also restricts answers to retrieved passages.

These controls are limited. Pattern-based redaction will not catch every identifier, and prompt instructions alone cannot guarantee safe or correct AI output. The project should be treated as a learning system using synthetic data, not as a production clinical product.

## What the attached document adds

The attached walkthrough explains the browser-to-service steps in more detail. It notes that password reset is not implemented, signing out clears the browser's local session but does not revoke it with Supabase, and the detailed patient page currently has little patient information until the summary is requested. Those details are consistent with the inspected frontend and authentication code.

The attached document also points out that the patient summary's audit entry is written after AI generation, so failed model calls may not leave a summary-attempt record. Its redaction discussion is also important: names or other identifying details typed into free-text history may not match the limited patterns that are removed.

## Current document-search evaluation

The repository includes an evaluation run over a small synthetic collection: four documents and 16 text sections. In the recorded run, the correct section appeared in the top five for about 93% of answerable questions. The end-to-end run recorded no false answers across six deliberately unanswerable questions, but it did record one answerable question that was refused. These results are useful early measurements, not proof of clinical accuracy; the sample is small, and the notes say answer faithfulness has not been measured.

## Where to look in the code

- Web pages: `frontend/clinical-ai-ui/app/`
- Browser login and API requests: `frontend/clinical-ai-ui/lib/auth.ts`
- API setup and authentication: `backend/ClinicalAI.Api/ClinicalAI.Api/Program.cs`
- API endpoints: `backend/ClinicalAI.Api/ClinicalAI.Api/Controllers/`
- Patient summary, redaction, AI, and RAG logic: `backend/ClinicalAI.Api/ClinicalAI.Api/Application/Services/`
- Database entities and context: `backend/ClinicalAI.Api/ClinicalAI.Api/Domain/Entities/` and `Infrastructure/Data/`
- File extraction and search: `document-processor/`
- Infrastructure configuration: `infra/`
- Development history and evaluation notes: `docs/clinical-ai-platform-notes.md` and `document-processor/eval/README.md`

## In one sentence

This project connects a clinician-facing web app to a protected API and several data/AI services, so clinicians can review synthetic patient records and search uploaded documents with AI assistance and source references.
