# RAG evaluation harness (Part 5.6)

Turns the informal observations from 5.1–5.5 into repeatable numbers, so Part 6 has
something concrete to compare Bedrock Knowledge Bases against.

```powershell
python eval/run_retrieval.py    # free: hits /search directly, no Bedrock
python eval/run_endtoend.py     # one Bedrock call per question (21 per run)
```

Outputs land in `eval/results/` as JSON and CSV.

## Read the numbers with this caveat

The corpus is **16 chunks across 4 documents**, 13 of them from a single file. Random
guessing would score recall@5 ≈ 0.31. These figures are **indicative, not conclusive** —
the harness is the durable deliverable, and Part 11 re-runs it on a larger corpus.

## Dataset

`dataset.json` — 15 answerable questions with hand-verified ground truth, 6 deliberately
unanswerable.

Questions were **hand-written from the chunk text before anything was run through
retrieval**. Generating questions from a chunk with an LLM reuses that chunk's own
vocabulary, and the retriever then scores well against its own phrasing. Natural
questions are harder and honest.

`expected_chunks` is a list, because overlap places some content across two chunks.

## Results (2026-09-16)

### Retrieval

| Metric | Value |
|---|---|
| recall@1 | **0.467** |
| recall@3 | 0.867 |
| recall@5 | **0.933** |
| recall@10 | 1.000 |
| MRR | 0.660 |
| median latency | 469 ms |

**The recall@1 to recall@5 gap is the headline.** The answering passage is ranked first
less than half the time, but is in the top five for 93% of questions. That is the
measured justification for passing K=5 passages to the model instead of the single best
one — retrieving one would be wrong more often than right.

One miss at k=5: `a09`, *"Can this be handled with remedies from a pharmacy instead of a
specialist?"* — ranked 7. Fair enough: the question has no topic anchor ("this"), so
there is little for an embedding to match on. A weakness of the question as much as the
retriever.

### End to end

| Metric | Value |
|---|---|
| false answer rate | **0.000** |
| false refusal rate | 0.067 |
| answers carrying citations | 0.933 |
| citation validity | 1.000 |
| cited correct source | 0.800 |
| median latency | 1441 ms |
| p90 latency | 4109 ms |

**Zero hallucinations.** All six unanswerable questions were refused, and none was
answered from the model's own training data. That is the number that matters most in a
clinical tool, and it is what the prompt contract exists to produce.

**Every citation resolved.** No invented `[7]` when five passages were supplied.

### The one false refusal is worth understanding

`a14` — *"Does paracetamol bring down swelling as well as pain?"* — was refused, even
though retrieval supplied the answering passage at rank 3:

> "Acetaminophen: Tylenol for targeted pain management (does not reduce inflammation)."

The question says **paracetamol**; the document says **acetaminophen**. Same drug,
different name. The model would not bridge the synonym, because it was instructed to
answer only from the passages.

This is the cost of strict grounding made visible. It is arguably correct behaviour —
the passage genuinely does not contain the word — but a clinician was denied information
sitting in their own library. Options for later: query expansion over drug synonyms, or
softening the instruction and accepting some hallucination risk. Not a bug to fix now; a
trade-off to measure. Note which way the current setting errs: toward refusing rather
than inventing, which is the right direction for clinical use.

## What is deliberately not measured

**Answer faithfulness** — whether a cited passage actually *justifies* the claim. Here we
verify a citation *resolves*, which is mechanical. Faithfulness needs an LLM judge and
belongs to Part 11. A valid citation number is not a correct citation.
