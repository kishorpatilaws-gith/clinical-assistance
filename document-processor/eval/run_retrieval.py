"""
Retrieval evaluation (5.6.b).

Hits the Python /search path directly: no auth, no Bedrock, no cost, so it can be
run freely while tuning chunk size or swapping the embedding model.

Metrics:
  recall@k  - fraction of questions where an expected chunk appears in the top k.
              This is the headline number. 5.3 showed the answering chunk is not
              reliably ranked first, so recall@1 and recall@5 are expected to differ
              substantially - the gap is what justifies passing K passages to the
              model rather than one.
  MRR       - mean reciprocal rank of the first correct chunk. One number capturing
              "how far down the list is the answer, on average".

Run:  python eval/run_retrieval.py
"""

from __future__ import annotations

import json
import logging
import os
import sys
import time
from pathlib import Path

from dotenv import load_dotenv

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(ROOT))
load_dotenv(ROOT / ".env")

logging.getLogger("httpx").setLevel(logging.WARNING)
logging.getLogger("sentence_transformers").setLevel(logging.WARNING)

import vectorstore as vs  # noqa: E402

K_VALUES = (1, 3, 5, 10)
MAX_K = max(K_VALUES)


def evaluate(dataset: dict) -> dict:
    rows = []
    for case in dataset["answerable"]:
        started = time.perf_counter()
        hits = vs.search(case["question"], limit=MAX_K)
        elapsed_ms = (time.perf_counter() - started) * 1000

        # Rank (1-based) of the first hit that is an expected chunk of the expected
        # document. Both must match: the right chunk index in the wrong file is not
        # a hit.
        rank = None
        for i, h in enumerate(hits, start=1):
            if h["file_name"] == case["document"] and h["chunk_index"] in case["expected_chunks"]:
                rank = i
                break

        rows.append({
            "id": case["id"],
            "question": case["question"],
            "expected": f'{case["document"]}#{case["expected_chunks"]}',
            "rank": rank,
            "reciprocal_rank": 1.0 / rank if rank else 0.0,
            "top_hit": f'{hits[0]["file_name"]}#{hits[0]["chunk_index"]}' if hits else None,
            "top_score": round(hits[0]["score"], 4) if hits else None,
            "latency_ms": round(elapsed_ms, 1),
            **{f"hit@{k}": bool(rank and rank <= k) for k in K_VALUES},
        })

    n = len(rows)
    summary = {
        "questions": n,
        **{f"recall@{k}": round(sum(r[f"hit@{k}"] for r in rows) / n, 3) for k in K_VALUES},
        "mrr": round(sum(r["reciprocal_rank"] for r in rows) / n, 3),
        "median_latency_ms": round(sorted(r["latency_ms"] for r in rows)[n // 2], 1),
        "misses_at_5": [r["id"] for r in rows if not r["hit@5"]],
    }
    return {"summary": summary, "rows": rows}


def main() -> int:
    dataset = json.loads((HERE / "dataset.json").read_text(encoding="utf-8"))
    print(f"corpus: {dataset['corpus']}")
    print(f"evaluating {len(dataset['answerable'])} answerable questions ...\n")

    result = evaluate(dataset)
    s = result["summary"]

    print(f"{'id':<5} {'rank':>5}  {'top hit':<34} {'score':>7}  question")
    print("-" * 100)
    for r in result["rows"]:
        rank = str(r["rank"]) if r["rank"] else "MISS"
        print(f'{r["id"]:<5} {rank:>5}  {str(r["top_hit"]):<34} {r["top_score"]:>7}  {r["question"][:40]}')

    print("\n" + "=" * 50)
    for k in K_VALUES:
        print(f"  recall@{k:<3}        {s[f'recall@{k}']:.3f}")
    print(f"  MRR               {s['mrr']:.3f}")
    print(f"  median latency    {s['median_latency_ms']} ms")
    if s["misses_at_5"]:
        print(f"  missed at k=5     {', '.join(s['misses_at_5'])}")

    out_dir = HERE / "results"
    out_dir.mkdir(exist_ok=True)
    (out_dir / "retrieval.json").write_text(json.dumps(result, indent=2), encoding="utf-8")

    import csv
    with (out_dir / "retrieval.csv").open("w", newline="", encoding="utf-8") as f:
        writer = csv.DictWriter(f, fieldnames=list(result["rows"][0].keys()))
        writer.writeheader()
        writer.writerows(result["rows"])

    print(f"\nwrote {out_dir / 'retrieval.json'} and retrieval.csv")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
