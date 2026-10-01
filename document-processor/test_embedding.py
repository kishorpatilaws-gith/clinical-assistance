import pytest
from embedding import (
    EMBEDDING_DIM, MODEL_NAME, cosine_similarity, embed, embed_one, model_info,
)

# Passages of the kind the pipeline actually indexes.
TSH_MONITORING = (
    "Check TSH and free T4 six to eight weeks after any levothyroxine dose change. "
    "Persistent symptoms with a normal TSH warrant review of adherence and absorption."
)
ALLERGY_NOTE = (
    "Patient has a documented penicillin allergy presenting as urticaria. "
    "Avoid beta-lactam antibiotics and record the reaction in the chart."
)
FOOTBALL = "The away side equalised in the eighty-ninth minute after a corner."


def test_dimension_matches_declared_constant():
    assert len(embed_one("thyroid")) == EMBEDDING_DIM


def test_vectors_are_unit_length():
    # Cosine distance in Qdrant assumes this; unnormalised vectors rank subtly wrong.
    for v in embed([TSH_MONITORING, ALLERGY_NOTE, FOOTBALL]):
        assert abs(sum(x * x for x in v) ** 0.5 - 1.0) < 1e-5


def test_embedding_is_deterministic():
    assert embed_one(TSH_MONITORING) == embed_one(TSH_MONITORING)


def test_batch_matches_individual():
    batched = embed([TSH_MONITORING, ALLERGY_NOTE])
    individual = [embed_one(TSH_MONITORING), embed_one(ALLERGY_NOTE)]
    for b, i in zip(batched, individual):
        assert cosine_similarity(b, i) > 0.9999


def test_order_is_preserved():
    texts = [f"Clinical section number {i}" for i in range(8)]
    vectors = embed(texts)
    assert len(vectors) == 8
    for text, vector in zip(texts, vectors):
        assert cosine_similarity(vector, embed_one(text)) > 0.9999


# ---------------------------------------------------------------------------
# The gate that matters. Dimensions and determinism only prove the code runs;
# this proves the vectors carry meaning. If it fails, retrieval in 5.3 returns
# plausible-looking nonsense and the cause is invisible.
# ---------------------------------------------------------------------------

def test_query_retrieves_the_relevant_passage():
    query = embed_one("thyroid function test")
    to_tsh = cosine_similarity(query, embed_one(TSH_MONITORING))
    to_allergy = cosine_similarity(query, embed_one(ALLERGY_NOTE))

    assert to_tsh > to_allergy, (
        f"'thyroid function test' scored {to_tsh:.3f} against the TSH passage but "
        f"{to_allergy:.3f} against the allergy note - embeddings are not carrying meaning"
    )


def test_clinical_text_outranks_unrelated_text():
    query = embed_one("thyroid function test")
    assert cosine_similarity(query, embed_one(TSH_MONITORING)) > cosine_similarity(
        query, embed_one(FOOTBALL)
    )


def test_paraphrase_scores_above_unrelated():
    # Semantic, not keyword: no shared vocabulary with the passage to speak of.
    query = embed_one("when should thyroid bloods be rechecked after adjusting the dose")
    assert cosine_similarity(query, embed_one(TSH_MONITORING)) > 0.4


def test_empty_input_returns_empty():
    assert embed([]) == []


def test_non_string_input_is_rejected():
    with pytest.raises(TypeError):
        embed(["fine", 42])  # type: ignore[list-item]


def test_model_info_reports_the_loaded_model():
    info = model_info()
    assert info["model"] == MODEL_NAME
    assert info["dimension"] == EMBEDDING_DIM
    assert info["normalized"] is True
