"""
End-to-end evaluation (5.6.c).

Hits the real .NET /api/rag/ask, so it measures the system a clinician actually
uses - auth, retrieval, prompt and model together - rather than a reimplementation
of it. That means one Bedrock call per question, so this is a separate command from
the retrieval eval, which is free to run.

Metrics:
  false refusal rate  - answerable question refused. Costs the clinician real
                        information that was sitting in their own library.
  false answer rate   - unanswerable question answered anyway. The hallucination
                        measure, and the dangerous one in a clinical tool.
  citation validity   - every [n] marker resolves to a passage that was actually
                        supplied. Catches a model inventing [7] when given five.
                        NB: this checks that a citation *resolves*, not that it
                        *justifies* the claim - faithfulness needs an LLM judge and
                        belongs to Part 11.
  cited source correct- for answerable questions, did a cited passage come from the
                        chunk the answer should have been drawn from.
  latency             - wall clock per question, end to end.

Run:  python eval/run_endtoend.py
"""

from __future__ import annotations

import csv
import json
import os
import re
import statistics
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path

from dotenv import load_dotenv

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
load_dotenv(ROOT / ".env")

NO_ANSWER = "The provided documents do not address this question."
MARKER = re.compile(r"\[(\d+(?:\s*,\s*\d+)*)\]")
TOP_K = 5


def post(url: str, payload: dict, headers: dict | None = None, timeout: int = 180) -> dict:
    body = json.dumps(payload).encode()
    req = urllib.request.Request(url, data=body, method="POST",
                                 headers={"Content-Type": "application/json", **(headers or {})})
    with urllib.request.urlopen(req, timeout=timeout) as resp:
        return json.loads(resp.read())


def get_token() -> str:
    supabase = os.environ["SUPABASE_URL"].rstrip("/")
    data = post(
        f"{supabase}/auth/v1/token?grant_type=password",
        {"email": os.environ["EVAL_EMAIL"], "password": os.environ["EVAL_PASSWORD"]},
        {"apikey": os.environ["SUPABASE_ANON_KEY"]},
    )
    return data["access_token"]


def markers_in(answer: str) -> set[int]:
    found: set[int] = set()
    for group in MARKER.findall(answer):
        for n in group.split(","):
            found.add(int(n.strip()))
    return found


def ask(api: str, token: str, question: str) -> tuple[dict, float]:
    started = time.perf_counter()
    result = post(f"{api}/api/rag/ask", {"question": question, "topK": TOP_K},
                  {"Authorization": f"Bearer {token}"})
    return result, (time.perf_counter() - started) * 1000


def main() -> int:
    dataset = json.loads((HERE / "dataset.json").read_text(encoding="utf-8"))
    api = os.getenv("EVAL_API_URL", "http://localhost:5258").rstrip("/")

    try:
        token = get_token()
    except (urllib.error.URLError, KeyError) as exc:
        print(f"could not authenticate: {exc}")
        return 1

    total = len(dataset["answerable"]) + len(dataset["unanswerable"])
    print(f"{total} questions against {api} - one Bedrock call each\n")

    rows = []

    for case in dataset["answerable"]:
        result, ms = ask(api, token, case["question"])
        answer = result["answer"].strip()
        refused = answer == NO_ANSWER
        used = markers_in(answer)
        sources = result.get("sources", [])

        # Every marker must point at a passage that was actually supplied.
        citations_valid = all(1 <= m <= len(sources) for m in used) if used else None

        # Did any cited passage come from a chunk the answer should rest on?
        cited_correct = any(
            sources[m - 1]["fileName"] == case["document"]
            and sources[m - 1]["chunkIndex"] in case["expected_chunks"]
            for m in used if 1 <= m <= len(sources)
        ) if used else False

        rows.append({
            "id": case["id"], "kind": "answerable", "question": case["question"],
            "refused": refused, "correct": not refused,
            "markers": len(used), "citations_valid": citations_valid,
            "cited_source_correct": cited_correct,
            "latency_ms": round(ms, 1),
            "answer": answer[:400],
        })
        print(f'  {case["id"]}  {"REFUSED" if refused else "answered"}'
              f'{"" if refused else ("  cited-right" if cited_correct else "  cited-other")}'
              f'  {ms:.0f}ms')

    for case in dataset["unanswerable"]:
        result, ms = ask(api, token, case["question"])
        answer = result["answer"].strip()
        refused = answer == NO_ANSWER

        rows.append({
            "id": case["id"], "kind": "unanswerable", "question": case["question"],
            "refused": refused, "correct": refused,
            "markers": len(markers_in(answer)), "citations_valid": None,
            "cited_source_correct": None,
            "latency_ms": round(ms, 1),
            "answer": answer[:400],
        })
        print(f'  {case["id"]}  {"refused (correct)" if refused else "ANSWERED (hallucination)"}  {ms:.0f}ms')

    answerable = [r for r in rows if r["kind"] == "answerable"]
    unanswerable = [r for r in rows if r["kind"] == "unanswerable"]
    with_markers = [r for r in answerable if r["markers"] > 0]

    summary = {
        "questions": len(rows),
        "false_refusal_rate": round(sum(r["refused"] for r in answerable) / len(answerable), 3),
        "false_answer_rate": round(sum(not r["refused"] for r in unanswerable) / len(unanswerable), 3),
        "answers_carrying_citations": round(len(with_markers) / len(answerable), 3),
        "citation_validity": round(
            sum(bool(r["citations_valid"]) for r in with_markers) / len(with_markers), 3
        ) if with_markers else None,
        "cited_source_correct": round(
            sum(r["cited_source_correct"] for r in answerable) / len(answerable), 3),
        "median_latency_ms": round(statistics.median(r["latency_ms"] for r in rows), 1),
        "p90_latency_ms": round(sorted(r["latency_ms"] for r in rows)[int(len(rows) * 0.9)], 1),
        "false_refusals": [r["id"] for r in answerable if r["refused"]],
        "hallucinations": [r["id"] for r in unanswerable if not r["refused"]],
    }

    print("\n" + "=" * 52)
    print(f'  false refusal rate       {summary["false_refusal_rate"]:.3f}  (answerable but refused)')
    print(f'  false answer rate        {summary["false_answer_rate"]:.3f}  (unanswerable but answered)')
    print(f'  answers with citations   {summary["answers_carrying_citations"]:.3f}')
    print(f'  citation validity        {summary["citation_validity"]}')
    print(f'  cited correct source     {summary["cited_source_correct"]:.3f}')
    print(f'  median latency           {summary["median_latency_ms"]:.0f} ms')
    print(f'  p90 latency              {summary["p90_latency_ms"]:.0f} ms')
    if summary["false_refusals"]:
        print(f'  false refusals           {", ".join(summary["false_refusals"])}')
    if summary["hallucinations"]:
        print(f'  HALLUCINATIONS           {", ".join(summary["hallucinations"])}')

    out = HERE / "results"
    out.mkdir(exist_ok=True)
    (out / "endtoend.json").write_text(
        json.dumps({"summary": summary, "rows": rows}, indent=2), encoding="utf-8")
    with (out / "endtoend.csv").open("w", newline="", encoding="utf-8") as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)

    print(f"\nwrote {out / 'endtoend.json'} and endtoend.csv")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
