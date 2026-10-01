import pytest
from chunking import chunk_text, DEFAULT_CHUNK_SIZE

GUIDELINE = (
    "Hypothyroidism is managed with levothyroxine replacement.\n\n"
    "Monitor TSH six to eight weeks after any dose change. "
    "Persistent fatigue despite a normal TSH warrants review of adherence and absorption.\n\n"
    "Red flags include altered mental status, hypothermia and bradycardia, "
    "which may indicate myxoedema and require urgent assessment."
)


def test_empty_text_yields_no_chunks():
    assert chunk_text("") == []
    assert chunk_text("   \n\n  ") == []


def test_short_text_is_a_single_chunk():
    chunks = chunk_text(GUIDELINE)
    assert len(chunks) == 1
    assert chunks[0].index == 0
    assert "levothyroxine" in chunks[0].text


def test_long_text_splits_and_respects_size():
    text = "\n\n".join(f"Paragraph {i}. " + "clinical detail " * 30 for i in range(20))
    chunks = chunk_text(text, chunk_size=500, overlap=80)

    assert len(chunks) > 1
    # Body must fit the target; overlap is prepended on top of it, so allow for it.
    for c in chunks:
        assert c.char_count <= 500 + 80 + 2


def test_chunks_overlap_so_a_split_passage_stays_retrievable():
    text = "\n\n".join(f"Section {i} discusses thyroid function testing in detail." for i in range(40))
    chunks = chunk_text(text, chunk_size=300, overlap=100)

    assert len(chunks) > 2
    # Some tail of chunk 0 must reappear at the head of chunk 1.
    assert any(word in chunks[1].text for word in chunks[0].text.split()[-6:])


def test_overlap_never_starts_mid_word():
    text = "\n\n".join("Levothyroxine absorption is affected by calcium supplements." for _ in range(30))
    for c in chunk_text(text, chunk_size=200, overlap=60)[1:]:
        assert not c.text.startswith(" ")
        first = c.text.split()[0].strip(".,;:")
        # The overlap trims to a word boundary, so the first token is a real word.
        assert first.isalpha() or first.isdigit()


def test_a_single_oversized_sentence_is_hard_cut():
    text = "word " * 1000  # no sentence terminator anywhere
    chunks = chunk_text(text, chunk_size=200, overlap=0)
    assert len(chunks) > 1
    assert all(c.char_count <= 200 for c in chunks)


def test_abbreviations_do_not_end_a_sentence():
    from chunking import _SENTENCE_END
    assert len(_SENTENCE_END.split("Reviewed by Dr. Nair today.")) == 1
    assert len(_SENTENCE_END.split("Take 10 mg daily. Review in six weeks.")) == 2


@pytest.mark.parametrize("size,overlap", [(0, 0), (100, 100), (100, 150), (100, -1)])
def test_invalid_parameters_are_rejected(size, overlap):
    with pytest.raises(ValueError):
        chunk_text("some text", chunk_size=size, overlap=overlap)


def test_offsets_point_back_into_the_source():
    chunks = chunk_text(GUIDELINE, chunk_size=120, overlap=20)
    for c in chunks:
        assert 0 <= c.char_start < c.char_end <= len(GUIDELINE.strip())
