"""
Qdrant vector store for the RAG pipeline (Part 5.2).

Holds one point per chunk: the embedding plus enough payload to render a citation
without a second round trip to Postgres. Part 6 replaces this with a Bedrock
Knowledge Base so the two can be compared; keeping the surface small is what makes
that swap cheap.
"""

from __future__ import annotations

import logging
import os
import uuid

from qdrant_client import QdrantClient
from qdrant_client.models import (
    Distance, FieldCondition, Filter, MatchValue, PointStruct, VectorParams,
)

import embedding

log = logging.getLogger("document-processor.vectorstore")

COLLECTION = os.getenv("QDRANT_COLLECTION", "clinical_chunks")

# Fixed namespace so a chunk's point ID is reproducible across runs. Re-indexing a
# document must overwrite its existing points, not append a second copy - otherwise
# every re-upload silently doubles the chunk's weight in retrieval.
_NAMESPACE = uuid.UUID("6f8c2a1e-5b3d-4c7a-9e2f-1a4b6c8d0e2f")

_client: QdrantClient | None = None


def get_client() -> QdrantClient:
    global _client
    if _client is None:
        url = os.getenv("QDRANT_URL")
        api_key = os.getenv("QDRANT_API_KEY")
        if not url or not api_key:
            raise RuntimeError(
                "QDRANT_URL and QDRANT_API_KEY must be set. Copy .env.example to .env "
                "and fill them in from https://cloud.qdrant.io"
            )
        _client = QdrantClient(url=url, api_key=api_key, timeout=60)
    return _client


def point_id(document_id: str, chunk_index: int) -> str:
    return str(uuid.uuid5(_NAMESPACE, f"{document_id}:{chunk_index}"))


def ensure_collection() -> bool:
    """Create the collection if absent. Returns True if it was created now.

    Cosine distance, matching the unit-length vectors embedding.py produces."""
    client = get_client()
    if client.collection_exists(COLLECTION):
        return False

    client.create_collection(
        collection_name=COLLECTION,
        vectors_config=VectorParams(size=embedding.EMBEDDING_DIM, distance=Distance.COSINE),
    )
    # Indexed so a per-document filter stays cheap as the collection grows.
    client.create_payload_index(COLLECTION, field_name="document_id", field_schema="keyword")
    log.info("Created collection %s (%dd, cosine)", COLLECTION, embedding.EMBEDDING_DIM)
    return True


def upsert_chunks(document_id: str, file_name: str, document_type: str,
                  chunks: list[dict]) -> int:
    """Store chunks for one document. Deterministic IDs mean this is idempotent:
    re-indexing the same document replaces its points rather than duplicating them."""
    if not chunks:
        return 0

    ensure_collection()
    points = [
        PointStruct(
            id=point_id(document_id, c["index"]),
            vector=c["vector"],
            payload={
                "document_id": document_id,
                "file_name": file_name,
                "document_type": document_type,
                "chunk_index": c["index"],
                "text": c["text"],
                "char_start": c["char_start"],
                "char_end": c["char_end"],
            },
        )
        for c in chunks
    ]
    get_client().upsert(collection_name=COLLECTION, points=points, wait=True)
    log.info("Upserted %d chunk(s) for document %s", len(points), document_id)
    return len(points)


def search(query: str, limit: int = 5, document_id: str | None = None,
           score_threshold: float | None = None) -> list[dict]:
    """Embed the query with the same model as the documents and return top matches.

    A different model would produce vectors in an unrelated space and every result
    would be confident nonsense, so this deliberately calls embedding.embed_one
    rather than accepting a caller-supplied vector."""
    ensure_collection()

    query_filter = None
    if document_id:
        query_filter = Filter(
            must=[FieldCondition(key="document_id", match=MatchValue(value=document_id))]
        )

    hits = get_client().query_points(
        collection_name=COLLECTION,
        query=embedding.embed_one(query),
        limit=limit,
        query_filter=query_filter,
        score_threshold=score_threshold,
        with_payload=True,
    ).points

    return [
        {
            "score": h.score,
            "document_id": h.payload.get("document_id"),
            "file_name": h.payload.get("file_name"),
            "document_type": h.payload.get("document_type"),
            "chunk_index": h.payload.get("chunk_index"),
            "text": h.payload.get("text"),
            "char_start": h.payload.get("char_start"),
            "char_end": h.payload.get("char_end"),
        }
        for h in hits
    ]


def delete_document(document_id: str) -> None:
    """Remove every chunk of one document - needed when a document is deleted, or
    the index would keep citing text that no longer exists."""
    get_client().delete(
        collection_name=COLLECTION,
        points_selector=Filter(
            must=[FieldCondition(key="document_id", match=MatchValue(value=document_id))]
        ),
        wait=True,
    )
    log.info("Deleted chunks for document %s", document_id)


def stats() -> dict:
    client = get_client()
    if not client.collection_exists(COLLECTION):
        return {"collection": COLLECTION, "exists": False, "points": 0}
    info = client.get_collection(COLLECTION)
    return {
        "collection": COLLECTION,
        "exists": True,
        "points": info.points_count,
        "dimension": info.config.params.vectors.size,
        "distance": str(info.config.params.vectors.distance),
    }
