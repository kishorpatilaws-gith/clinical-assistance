'use client';
import { useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { apiFetch, getToken } from '@/lib/auth';

interface Source {
  marker: number;
  fileName: string;
  documentType: string;
  documentId: string;
  chunkIndex: number;
  charStart: number;
  charEnd: number;
  score: number;
}

interface Passage {
  score: number;
  fileName: string;
  chunkIndex: number;
  text: string;
  charStart: number;
  charEnd: number;
}

interface Answer {
  question: string;
  answer: string;
  model: string;
  sources: Source[];
  retrieved: Passage[];
}

interface DocOption {
  id: string;
  fileName: string;
}

// The API says exactly this when the passages do not answer the question.
const NO_ANSWER = 'The provided documents do not address this question.';

/**
 * Splits an answer into text and citation markers. The model usually writes "[1]",
 * but "[1][2]" and "[1, 2]" both occur, so a marker group expands to one button per
 * number. Anything that does not parse is left as plain text rather than mangled.
 */
function parseAnswer(answer: string): Array<string | number> {
  const parts: Array<string | number> = [];
  const pattern = /\[(\d+(?:\s*,\s*\d+)*)\]/g;
  let last = 0;
  let match: RegExpExecArray | null;

  while ((match = pattern.exec(answer)) !== null) {
    if (match.index > last) parts.push(answer.slice(last, match.index));
    for (const n of match[1].split(',')) parts.push(Number(n.trim()));
    last = match.index + match[0].length;
  }
  if (last < answer.length) parts.push(answer.slice(last));
  return parts;
}

export default function AskPage() {
  const [question, setQuestion] = useState('');
  const [scope, setScope] = useState('');
  const [docs, setDocs] = useState<DocOption[]>([]);
  const [result, setResult] = useState<Answer | null>(null);
  const [asking, setAsking] = useState(false);
  const [error, setError] = useState('');
  const [openMarker, setOpenMarker] = useState<number | null>(null);
  // Distinguish "none indexed" from "not known yet" - rendering "0 indexed" while
  // the list is still in flight tells the clinician something untrue.
  const [docsLoaded, setDocsLoaded] = useState(false);
  const router = useRouter();

  useEffect(() => {
    if (!getToken()) { router.push('/login'); return; }
    (async () => {
      try {
        const res = await apiFetch('/api/document');
        if (res.ok) {
          const list = await res.json();
          setDocs(list.map((d: any) => ({ id: d.id, fileName: d.fileName })));
        }
      } catch {
        // The document list only powers the optional scope filter; asking still works.
      } finally {
        setDocsLoaded(true);
      }
    })();
  }, []);

  const ask = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!question.trim()) return;

    setAsking(true);
    setError('');
    setResult(null);
    setOpenMarker(null);

    try {
      const res = await apiFetch('/api/rag/ask', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          question: question.trim(),
          topK: 5,
          documentId: scope || null,
        }),
      });
      if (res.status === 401) return;          // apiFetch is redirecting to /login
      if (res.status === 403) throw new Error('You do not have doctor access.');
      if (!res.ok) {
        const body = await res.json().catch(() => null);
        throw new Error(body?.error ?? `Request failed (${res.status})`);
      }
      setResult(await res.json());
    } catch (err: any) {
      setError(err.message);
    } finally {
      setAsking(false);
    }
  };

  const refused = result?.answer.trim() === NO_ANSWER;

  // `sources` is every retrieved passage, numbered for the model to choose from -
  // not the subset it actually cited. Counting it as "cited" overstates how well
  // grounded the answer is, so count the distinct markers present in the text.
  const citedMarkers = result
    ? new Set(parseAnswer(result.answer).filter((x): x is number => typeof x === 'number'))
    : new Set<number>();
  const openSource = result?.sources.find(s => s.marker === openMarker) ?? null;
  const openPassage =
    openSource && result
      ? result.retrieved.find(
          p => p.fileName === openSource.fileName && p.chunkIndex === openSource.chunkIndex
        )
      : null;

  return (
    <main className="page">
      <Link className="back-link" href="/patients">← Back to patients</Link>

      <div className="topbar">
        <h1>Ask the documents</h1>
        <span className="topbar-meta">{docsLoaded ? `${docs.length} indexed` : ''}</span>
      </div>

      <form className="ask-form" onSubmit={ask}>
        <textarea
          id="question"
          value={question}
          onChange={e => setQuestion(e.target.value)}
          placeholder="e.g. What is the difference between osteoarthritis and rheumatoid arthritis?"
          maxLength={2000}
          aria-label="Question"
        />
        <div className="ask-controls">
          <select
            id="scope"
            value={scope}
            onChange={e => setScope(e.target.value)}
            aria-label="Limit to one document"
            disabled={!docsLoaded}
          >
            <option value="">All documents</option>
            {docs.map(d => (
              <option key={d.id} value={d.id}>{d.fileName}</option>
            ))}
          </select>
          <button type="submit" className="btn btn-primary" disabled={!question.trim() || asking}>
            {asking ? 'Searching...' : 'Ask'}
          </button>
        </div>
      </form>

      {error && <p className="error-text">{error}</p>}

      {asking && (
        <p className="empty-note">Retrieving passages and generating an answer...</p>
      )}

      {!asking && !result && !error && (
        <p className="empty-note">
          Answers are generated only from your uploaded documents, with citations back
          to the passage each claim came from.
        </p>
      )}

      {result && refused && (
        <div className="refusal">
          {result.answer}
          <p className="refusal-hint">
            Passages were retrieved but none answered the question. Your library may not
            cover this topic — the model is deliberately not answering from general
            knowledge.
          </p>
        </div>
      )}

      {result && !refused && (
        <>
          <div className="summary-box">
            {parseAnswer(result.answer).map((part, i) =>
              typeof part === 'string' ? (
                <span key={i}>{part}</span>
              ) : (
                <button
                  key={i}
                  type="button"
                  className="citation"
                  aria-expanded={openMarker === part}
                  aria-label={`Show source ${part}`}
                  onClick={() => setOpenMarker(openMarker === part ? null : part)}
                >
                  {part}
                </button>
              )
            )}
          </div>

          {openSource && (
            <div className="source-panel">
              <div className="source-head">
                <span>
                  [{openSource.marker}] {openSource.fileName} · chunk {openSource.chunkIndex}
                </span>
                <span>
                  chars {openSource.charStart}–{openSource.charEnd} · score{' '}
                  {openSource.score.toFixed(3)}
                </span>
              </div>
              <div className="source-text">
                {openPassage?.text ?? 'Passage text unavailable.'}
              </div>
            </div>
          )}

          <div className="answer-meta">
            <span>
              {citedMarkers.size} of {result.sources.length} passages cited
            </span>
            <span>{result.model}</span>
          </div>

          <details className="disclosure">
            <summary>
              {result.retrieved.length} passages retrieved — show what the model was given
            </summary>
            {result.retrieved.map((p, i) => (
              <div className="passage" key={i}>
                <div className="passage-head">
                  <span>{p.fileName} · chunk {p.chunkIndex}</span>
                  <span>{p.score.toFixed(3)}</span>
                </div>
                <div className="passage-text">{p.text}</div>
              </div>
            ))}
          </details>
        </>
      )}
    </main>
  );
}
