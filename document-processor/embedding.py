"""
Local embeddings for the RAG pipeline (Part 5).

Runs on CPU via sentence-transformers - no API call, no AWS credits. Part 6 swaps
this for Bedrock embeddings so the two can be compared directly; keeping the model
behind a small interface here is what makes that swap cheap.

Model choice: all-MiniLM-L6-v2. 384 dimensions, ~90MB, quick on CPU. A larger model
would score slightly better on benchmarks and make every upload noticeably slower on
modest hardware. Whether that trade is worth it is a question for Part 11's
evaluation harness to answer with numbers, not for a guess now.
"""

from __future__ import annotations

import logging
import threading
import time

from sentence_transformers import SentenceTransformer

log = logging.getLogger("document-processor.embedding")

MODEL_NAME = "sentence-transformers/all-MiniLM-L6-v2"
EMBEDDING_DIM = 384

# Vectors are normalised to unit length, which makes cosine similarity equal to a
# plain dot product. Qdrant is configured for cosine distance in 5.2, and feeding
# it unnormalised vectors is a quiet source of subtly wrong rankings.
NORMALIZE = True

_model: SentenceTransformer | None = None
_lock = threading.Lock()


def get_model() -> SentenceTransformer:
    """Load once and reuse. Loading costs seconds; doing it per request would put
    that on every upload. Double-checked under a lock because uvicorn serves
    concurrently and two threads racing the first load would fetch it twice."""
    global _model
    if _model is None:
        with _lock:
            if _model is None:
                started = time.perf_counter()
                log.info("Loading embedding model %s ...", MODEL_NAME)
                _model = SentenceTransformer(MODEL_NAME, device="cpu")
                log.info("Model ready in %.1fs", time.perf_counter() - started)
    return _model


def embed(texts: list[str], batch_size: int = 16) -> list[list[float]]:
    """Embed a batch of texts. Order of the result matches the input."""
    if not texts:
        return []
    if any(not isinstance(t, str) for t in texts):
        raise TypeError("embed() expects a list of strings")

    vectors = get_model().encode(
        texts,
        batch_size=batch_size,
        normalize_embeddings=NORMALIZE,
        convert_to_numpy=True,
        show_progress_bar=False,
    )
    return [v.tolist() for v in vectors]


def embed_one(text: str) -> list[float]:
    return embed([text])[0]


def cosine_similarity(a: list[float], b: list[float]) -> float:
    """Both vectors are unit length, so this is just the dot product. Kept explicit
    because it is the thing the retrieval quality actually rests on."""
    return sum(x * y for x, y in zip(a, b))


def model_info() -> dict:
    """Reported by /health so a caller can confirm which model produced a vector -
    embeddings from different models are not comparable."""
    return {
        "model": MODEL_NAME,
        "dimension": EMBEDDING_DIM,
        "normalized": NORMALIZE,
        "loaded": _model is not None,
    }
