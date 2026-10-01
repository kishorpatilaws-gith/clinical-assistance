'use client';
import { useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { apiFetch, getToken } from '@/lib/auth';

interface Doc {
  id: string;
  fileName: string;
  documentType: number;
  status: number;
  uploadedAt: string;
  processedAt: string | null;
  charCount: number;
}

const DOC_TYPES = ['Report', 'Clinical Guideline', 'Research Paper'];
const STATUSES = ['Pending', 'Processing', 'Completed', 'Failed'];
const isIndexing = (d: Doc) => d.status === 0 || d.status === 1;
const POLL_MS = 5000;

export default function DocumentsPage() {
  const [docs, setDocs] = useState<Doc[]>([]);
  const [file, setFile] = useState<File | null>(null);
  const [docType, setDocType] = useState('0');
  const [uploading, setUploading] = useState(false);
  const [loading, setLoading] = useState(true);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');
  const router = useRouter();

  const loadDocs = async () => {
    try {
      const res = await apiFetch('/api/document');
      if (res.status === 401) return;            // apiFetch is redirecting to /login
      if (res.status === 403) { setError('You do not have doctor access.'); return; }
      if (!res.ok) { setError(`Could not load documents (${res.status}).`); return; }
      setDocs(await res.json());
      setError('');
    } catch {
      setError('Could not reach the API. Is it running on port 5258?');
    } finally {
      setLoading(false);
    }
  };

  useEffect(() => {
    if (!getToken()) { router.push('/login'); return; }
    loadDocs();
  }, []);

  // Indexing runs in the background and a whole book takes minutes, so keep the
  // list fresh until every row has settled as Completed or Failed.
  useEffect(() => {
    if (!docs.some(isIndexing)) return;
    const timer = setTimeout(loadDocs, POLL_MS);
    return () => clearTimeout(timer);
  }, [docs]);

  const handleUpload = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!file) return;
    setUploading(true);
    setMessage('');
    setError('');

    const formData = new FormData();
    formData.append('file', file);
    formData.append('documentType', docType);

    try {
      const res = await apiFetch('/api/document/upload', { method: 'POST', body: formData });
      if (!res.ok) {
        // The API returns { error } for a rejected file; fall back to the status code.
        const body = await res.json().catch(() => null);
        throw new Error(body?.error ?? `Upload failed (${res.status})`);
      }
      const data: Doc = await res.json();
      setMessage(`Uploaded "${data.fileName}". Indexing in the background — its status below updates when it finishes.`);
      setFile(null);
      const input = document.getElementById('file-input') as HTMLInputElement | null;
      if (input) input.value = '';
      loadDocs();
    } catch (err: any) {
      setError(err.message);
    } finally {
      setUploading(false);
    }
  };

  return (
    <main className="page">
      <Link className="back-link" href="/patients">← Back to patients</Link>

      <div className="topbar">
        <h1>Documents</h1>
        <span className="topbar-meta"><Link href="/ask">Ask the documents</Link></span>
      </div>

      <form className="upload-row" onSubmit={handleUpload}>
        <input
          id="file-input"
          type="file"
          onChange={e => setFile(e.target.files?.[0] ?? null)}
          accept=".pdf,.docx,.txt,.md,.csv"
        />
        <select id="doc-type" value={docType} onChange={e => setDocType(e.target.value)}>
          <option value="0">Report</option>
          <option value="1">Clinical Guideline</option>
          <option value="2">Research Paper</option>
        </select>
        <button type="submit" className="btn btn-primary" disabled={!file || uploading}>
          {uploading ? 'Uploading...' : 'Upload'}
        </button>
      </form>

      {message && <p className="empty-note">{message}</p>}
      {error && <p className="error-text">{error}</p>}

      {loading && <p className="empty-note">Loading...</p>}
      {!loading && docs.length === 0 && !error && (
        <p className="empty-note">No documents uploaded yet.</p>
      )}

      <ul className="record-list">
        {docs.map(d => (
          <li className="record-row" key={d.id}>
            <span className="record-name">{d.fileName}</span>
            <span className="topbar-meta">
              <span className="record-meta">{DOC_TYPES[d.documentType] ?? 'Unknown'}</span>
              <span className="record-meta">
                {d.charCount > 0 ? `${d.charCount.toLocaleString()} chars` : '—'}
              </span>
              <span className="status-pill" data-status={STATUSES[d.status]}>
                {STATUSES[d.status] ?? 'Unknown'}
              </span>
            </span>
          </li>
        ))}
      </ul>
    </main>
  );
}
