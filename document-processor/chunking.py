"""
Chunking for the RAG pipeline (Part 5).

Why chunk at all: an embedding model has a fixed input window, and a vector that
averages a whole 40-page guideline is too diffuse to match any specific question.
Smaller passages give sharper vectors, at the cost of losing surrounding context -
which is what the overlap is for.

Strategy is boundary-aware rather than a blind character slice: prefer to break at
paragraph breaks, fall back to sentence ends, and only cut mid-sentence when a
single sentence is itself longer than the target. A chunk that starts mid-sentence
retrieves badly, because the embedding picks up a fragment the author never wrote.
"""

from __future__ import annotations

import re
from dataclasses import dataclass

DEFAULT_CHUNK_SIZE = 900
DEFAULT_OVERLAP = 150

# A sentence ends at . ! or ? followed by whitespace. Guard common clinical
# abbreviations so "Dr. Nair" and "e.g. levothyroxine" do not become boundaries.
#
# The lookbehinds must include the trailing dot. They are evaluated at the split
# point - i.e. *after* the period - so a bare (?<!\bDr) inspects "r." and never
# fires. Caught by test_abbreviations_do_not_end_a_sentence.
_ABBREVIATIONS = (
    r"(?<!\bDr\.)(?<!\bMr\.)(?<!\bMrs\.)(?<!\bMs\.)(?<!\bProf\.)"
    r"(?<!\bapprox\.)(?<!\bvs\.)(?<!\bmg\.)(?<!\bmcg\.)(?<!\bNo\.)"
    r"(?<!\be\.g\.)(?<!\bi\.e\.)"
)
_SENTENCE_END = re.compile(_ABBREVIATIONS + r"(?<=[.!?])\s+")
_PARAGRAPH_BREAK = re.compile(r"\n\s*\n")


@dataclass(frozen=True)
class Chunk:
    index: int
    text: str
    char_start: int
    char_end: int

    @property
    def char_count(self) -> int:
        return len(self.text)


def _split_long_unit(unit: str, chunk_size: int) -> list[str]:
    """A paragraph longer than chunk_size: split on sentences, then hard-cut."""
    pieces, current = [], ""
    for sentence in _SENTENCE_END.split(unit):
        if not sentence:
            continue
        if len(current) + len(sentence) + 1 <= chunk_size:
            current = f"{current} {sentence}".strip()
        else:
            if current:
                pieces.append(current)
            # A single sentence can still exceed the target (tables, long lists).
            while len(sentence) > chunk_size:
                pieces.append(sentence[:chunk_size])
                sentence = sentence[chunk_size:]
            current = sentence
    if current:
        pieces.append(current)
    return pieces


def chunk_text(
    text: str,
    chunk_size: int = DEFAULT_CHUNK_SIZE,
    overlap: int = DEFAULT_OVERLAP,
) -> list[Chunk]:
    if chunk_size <= 0:
        raise ValueError("chunk_size must be positive")
    if overlap < 0:
        raise ValueError("overlap must not be negative")
    if overlap >= chunk_size:
        # Otherwise each chunk re-reads everything the last one did and the walk
        # never advances.
        raise ValueError("overlap must be smaller than chunk_size")

    text = text.strip()
    if not text:
        return []

    # 1. Break into paragraphs, splitting any that are individually too long.
    units: list[str] = []
    for para in _PARAGRAPH_BREAK.split(text):
        para = para.strip()
        if not para:
            continue
        units.extend([para] if len(para) <= chunk_size else _split_long_unit(para, chunk_size))

    # 2. Greedily pack units into chunks.
    chunks: list[Chunk] = []
    current = ""
    for unit in units:
        candidate = f"{current}\n\n{unit}" if current else unit
        if len(candidate) <= chunk_size:
            current = candidate
            continue
        if current:
            chunks.append(current)
        current = unit
    if current:
        chunks.append(current)

    # 3. Prepend a tail of the previous chunk so a passage split across a boundary
    #    is still retrievable from either side.
    out: list[Chunk] = []
    cursor = 0
    for i, body in enumerate(chunks):
        if i > 0 and overlap:
            tail = chunks[i - 1][-overlap:]
            # Start the overlap at a word boundary; a half-word helps nobody.
            space = tail.find(" ")
            if space != -1:
                tail = tail[space + 1:]
            body = f"{tail}\n\n{body}" if tail else body

        start = text.find(chunks[i][:40], cursor)
        if start == -1:
            start = cursor
        end = start + len(chunks[i])
        cursor = end
        out.append(Chunk(index=i, text=body, char_start=start, char_end=end))

    return out
