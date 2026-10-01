"""
Integration tests against a live Qdrant cluster. Skipped automatically when
QDRANT_URL / QDRANT_API_KEY are absent, so the suite still passes without them.
"""
import os
import uuid

import pytest
from dotenv import load_dotenv

load_dotenv(os.path.join(os.path.dirname(__file__), ".env"))

import chunking
import embedding
import vectorstore as vs

GUIDELINE = (
    "HYPOTHYROIDISM MANAGEMENT\n\n"
    "Levothyroxine is the treatment of choice. Starting dose is typically 1.6 micrograms "
    "per kilogram daily, reduced in older patients or those with ischaemic heart disease.\n\n"
    "MONITORING\n\n"
    "Recheck TSH and free T4 six to eight weeks after any dose change. Steady state is not "
    "reached before six weeks and earlier testing causes unnecessary adjustment.\n\n"
    "ABSORPTION\n\n"
    "Absorption is reduced by calcium carbonate, ferrous sulphate and proton pump inhibitors. "
    "Advise dosing on an empty stomach, four hours apart from these agents.\n\n"
    "RED FLAGS\n\n"
    "Altered mental status, hypothermia or bradycardia may indicate myxoedema coma and "
    "require immediate specialist input.\n\n"
    "PENICILLIN ALLERGY\n\n"
    "Document the reaction type. True IgE-mediated reactions present as urticaria or "
    "anaphylaxis within one hour. Avoid beta-lactams until formally reviewed."
)


def _configured() -> bool:
    return bool(os.getenv("QDRANT_URL") and os.getenv("QDRANT_API_KEY"))


class RequiresQdrantAttribute(pytest.MarkDecorator):
    pass


requires_qdrant = pytest.mark.skipif(
    not _configured(), reason="QDRANT_URL / QDRANT_API_KEY not configured"
)


def _build_payload():
    chunks = chunking.chunk_text(GUIDELINE, chunk_size=300, overlap=60)
    vectors = embedding.embed([c.text for c in chunks])
    return [
        {"index": c.index, "text": c.text, "char_start": c.char_start,
         "char_end": c.char_end, "vector": v}
        for c, v in zip(chunks, vectors)
    ]


@pytest.fixture(scope="module")
def indexed_doc():
    """Index a document under a throwaway id, then remove it again."""
    doc_id = str(uuid.uuid4())
    payload = _build_payload()
    vs.upsert_chunks(doc_id, "test-guideline.pdf", "ClinicalGuideline", payload)
    yield doc_id, payload
    vs.delete_document(doc_id)


def test_point_id_is_deterministic():
    a = vs.point_id("doc-1", 0)
    b = vs.point_id("doc-1", 0)
    assert a == b
    assert a != vs.point_id("doc-1", 1)
    assert a != vs.point_id("doc-2", 0)


@requires_qdrant
def test_collection_matches_the_embedding_model(indexed_doc):
    stats = vs.stats()
    assert stats["exists"]
    assert stats["dimension"] == embedding.EMBEDDING_DIM
    assert stats["distance"] == "Cosine"   # embeddings are unit-length


@requires_qdrant
def test_reindexing_replaces_rather_than_duplicates(indexed_doc):
    doc_id, payload = indexed_doc
    before = vs.stats()["points"]
    vs.upsert_chunks(doc_id, "test-guideline.pdf", "ClinicalGuideline", payload)
    assert vs.stats()["points"] == before


# The chunk each question should land on, verified by reading the stored chunk
# text. Asserting on the index rather than a substring: chunk 2 ends with the
# "RED FLAGS" heading and chunk 3 opens with overlap from it, so a substring
# check passes for the wrong reasons.
@requires_qdrant
@pytest.mark.parametrize("query,expected_chunk", [
    ("When do I recheck bloods after a dose change?", 1),   # MONITORING body
    ("Which medicines interfere with absorption?",     2),   # ABSORPTION body
    ("What signs need urgent specialist input?",       2),   # RED FLAGS body
    ("Can this patient have penicillin?",              3),   # PENICILLIN body
])
def test_query_retrieves_the_right_chunk(indexed_doc, query, expected_chunk):
    doc_id, _ = indexed_doc
    hits = vs.search(query, limit=4, document_id=doc_id)
    assert hits, "no results returned"
    assert hits[0]["chunk_index"] == expected_chunk, (
        f"{query!r} returned chunk {hits[0]['chunk_index']} "
        f"(score {hits[0]['score']:.3f}), expected {expected_chunk}"
    )


@requires_qdrant
def test_results_are_ordered_by_descending_score(indexed_doc):
    doc_id, _ = indexed_doc
    scores = [h["score"] for h in vs.search("thyroid monitoring", limit=4, document_id=doc_id)]
    assert scores == sorted(scores, reverse=True)


@requires_qdrant
def test_filter_isolates_documents(indexed_doc):
    doc_id, _ = indexed_doc
    assert vs.search("thyroid", limit=5, document_id=doc_id)
    assert vs.search("thyroid", limit=5, document_id=str(uuid.uuid4())) == []


@requires_qdrant
def test_payload_carries_enough_for_a_citation(indexed_doc):
    doc_id, _ = indexed_doc
    hit = vs.search("levothyroxine dose", limit=1, document_id=doc_id)[0]
    for field in ("document_id", "file_name", "document_type", "chunk_index",
                  "text", "char_start", "char_end"):
        assert hit[field] is not None, f"{field} missing from payload"


@requires_qdrant
def test_delete_removes_every_chunk_of_a_document():
    doc_id = str(uuid.uuid4())
    vs.upsert_chunks(doc_id, "temp.pdf", "Report", _build_payload())
    assert vs.search("thyroid", limit=5, document_id=doc_id)
    vs.delete_document(doc_id)
    assert vs.search("thyroid", limit=5, document_id=doc_id) == []
