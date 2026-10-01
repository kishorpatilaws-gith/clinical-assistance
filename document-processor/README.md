# Document processor

FastAPI service that extracts, chunks and embeds PDF, DOCX and TXT files. The .NET
API calls it over HTTP during document upload, and at retrieval time to embed
search queries.

It is a **separate application**, not a referenced library — different language
runtime, own dependencies, own lifecycle.

## Run

```powershell
python -m venv .venv
.venv\Scripts\Activate.ps1
pip install -r requirements.txt
uvicorn main:app --port 8000
```

During development three processes run together: `dotnet run` (5258),
`uvicorn` (8000), `npm run dev` (3000).

## Endpoints

| Method | Path            | Purpose                                                |
|--------|-----------------|--------------------------------------------------------|
| GET    | `/health`       | Liveness, PyMuPDF version, embedding model + dimension |
| POST   | `/extract-text` | Multipart `file` upload, returns extracted text        |
| POST   | `/embed`        | `{"texts": [...]}` -> vectors. Used for search queries |
| POST   | `/process`      | Extract + chunk + embed, returns chunks without storing |
| POST   | `/index`        | Extract + chunk + embed + store in Qdrant. The ingestion path |
| POST   | `/search`       | `{"query": "...", "limit": 5}` -> top-K chunks with scores |
| DELETE | `/index/{id}`   | Drop one document's chunks from the index              |

Interactive docs at <http://localhost:8000/docs>.

## Notes

- Use `import pymupdf`. The older `import fitz` spelling still works but is deprecated.
- Password-protected PDFs return 422; unsupported types return 415; files over 20MB return 413.
- DOCX extraction includes table cells — clinical documents carry a lot of content in tables,
  and reading paragraphs alone silently drops it.

## Embeddings

`all-MiniLM-L6-v2`, 384 dimensions, CPU only. The model loads once at startup rather
than on first request, so no clinician pays the ~3s load.

Vectors are **normalised to unit length**, which makes cosine similarity a plain dot
product — Qdrant is configured for cosine distance, and unnormalised vectors are a
quiet source of subtly wrong rankings.

`requirements.txt` pins the PyTorch **CPU** index. The default PyPI wheel bundles CUDA
(~2.5GB) which is dead weight without an NVIDIA GPU; the CPU build is ~250MB.

A query must be embedded by the **same model** as the documents. Vectors from
different models are not comparable, and mixing them returns confident nonsense —
which is why `/health` reports the model name.

Measured on a dual-core CPU: a 5-page PDF extracts, chunks and embeds in ~900ms.

## Vector store

Qdrant Cloud, collection `clinical_chunks`, 384 dimensions, **cosine** distance to
match the unit-length vectors.

Credentials come from `.env` (gitignored); `.env.example` is the committed template.
Get a free cluster at <https://cloud.qdrant.io>.

Point IDs are `uuid5(namespace, "{document_id}:{chunk_index}")` - deterministic, so
re-indexing a document **replaces** its points rather than appending a second copy.
Without that, every re-upload silently doubles a chunk's weight in retrieval.

`document_id` is supplied by the caller (the Postgres row id) so the vector store and
the relational record stay in step, and deleting a document can clear its chunks.

### Retrieval: use top-K, not top-1

Measured on the synthetic thyroid guideline: for *"which drugs reduce levothyroxine
absorption"*, the **introduction** chunk outranks the chunk that actually answers it -
at both 300 and 500 character chunk sizes.

```
chunk_size=500:  0.598 [0] intro     0.557 [2] <- the answer
chunk_size=300:  0.600 [0] intro     0.388 [2] <- the answer
```

Short, topically-central chunks act as attractors for any on-topic query. The answer
was in the top 2 every time, so retrieving K>=3 and letting the model pick is sound;
trusting rank 1 is not. Part 11 should measure recall@k rather than accuracy@1.
