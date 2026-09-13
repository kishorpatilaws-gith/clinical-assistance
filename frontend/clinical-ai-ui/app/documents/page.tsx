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
}

const DOC_TYPES = ['Report', 'Clinical Guideline', 'Research Paper'];
const STATUSES = ['Pending', 'Processing', 'Completed', 'Failed'];

export default function DocumentsPage() {
  const [docs, setDocs] = useState<Doc[]>([]);
  const [file, setFile] = useState<File | null>(null);
  const [docType, setDocType] = useState('0');
  const [uploading, setUploading] = useState(false);
  const [message, setMessage] = useState('');
  const [notBuilt, setNotBuilt] = useState(false);
  const router = useRouter();

  const loadDocs = async () => {
    try {
      const res = await apiFetch('/api/document');
      // DocumentController is Part 4 and not built yet; a 404 here means the
      // endpoint is absent, not that the request failed.
      if (res.status === 404) { setNotBuilt(true); return; }
      if (res.ok) { setDocs(await res.json()); setNotBuilt(false); }
    } catch {
      setMessage('Could not reach the API. Is it running on port 5258?');
    }
  };

  useEffect(() => {
    if (!getToken()) { router.push('/login'); return; }
    loadDocs();
  }, []);

  const handleUpload = async (e: React.FormEvent) => {
    e.preventDefault();
    if (!file) return;
    setUploading(true);
    setMessage('');
    const formData = new FormData();
    formData.append('file', file);
    formData.append('documentType', docType);

    try {
      const res = await apiFetch('/api/document/upload', { method: 'POST', body: formData });
      if (res.status === 404) {
        setNotBuilt(true);
        throw new Error('Upload endpoint does not exist yet (Part 4 is unfinished).');
      }
      if (!res.ok) throw new Error(`Upload failed (${res.status})`);
      const data = await res.json();
      setMessage(`Uploaded "${data.fileName}" — status: ${STATUSES[data.status]}`);
      setFile(null);
      loadDocs();
    } catch (err: any) {
      setMessage(err.message);
    } finally {
      setUploading(false);
    }
  };

  return (
    <main className="page">
      <Link className="back-link" href="/patients">← Back to patients</Link>

      <div className="topbar">
        <h1>Documents</h1>
      </div>

      <form className="upload-row" onSubmit={handleUpload}>
        <input
          type="file"
          onChange={e => setFile(e.target.files?.[0] ?? null)}
          accept=".pdf,.docx,.txt"
        />
        <select value={docType} onChange={e => setDocType(e.target.value)}>
          <option value="0">Report</option>
          <option value="1">Clinical Guideline</option>
          <option value="2">Research Paper</option>
        </select>
        <button type="submit" className="btn btn-primary" disabled={!file || uploading}>
          {uploading ? 'Uploading...' : 'Upload'}
        </button>
      </form>

      {message && <p className="error-text">{message}</p>}

      {notBuilt && (
        <p className="empty-note">
          Document storage is not wired up yet — the API has no /api/document endpoint.
          This page will populate once Part 4 is finished.
        </p>
      )}

      {!notBuilt && docs.length === 0 && (
        <p className="empty-note">No documents uploaded.</p>
      )}

      <ul className="record-list">
        {docs.map(d => (
          <li className="record-row" key={d.id}>
            <span className="record-name">{d.fileName}</span>
            <span className="topbar-meta">
              <span className="record-meta">{DOC_TYPES[d.documentType]}</span>
              <span className="status-pill" data-status={STATUSES[d.status]}>
                {STATUSES[d.status]}
              </span>
            </span>
          </li>
        ))}
      </ul>
    </main>
  );
}
