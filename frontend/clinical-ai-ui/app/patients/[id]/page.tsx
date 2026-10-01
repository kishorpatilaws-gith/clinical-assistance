'use client';
import { useState } from 'react';
import { useParams, useRouter } from 'next/navigation';
import Link from 'next/link';
import { apiFetch, getToken } from '@/lib/auth';

export default function PatientDetailPage() {
  const { id } = useParams<{ id: string }>();
  const router = useRouter();
  const [summary, setSummary] = useState('');
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');

  const generateSummary = async () => {
    if (!getToken()) { router.push('/login'); return; }
    setLoading(true);
    setError('');
    try {
      const res = await apiFetch(`/api/patient/${id}/summary`);
      // apiFetch renews an expired session itself; a 401 here means it could not,
      // and it is already redirecting to /login. Say nothing rather than flash an
      // error at a page that is on its way out.
      if (res.status === 401) return;
      if (res.status === 403) throw new Error('You do not have doctor access.');
      if (!res.ok) throw new Error(`Request failed (${res.status})`);
      const data = await res.json();
      setSummary(data.summary);
    } catch (err: any) {
      setError(err.message);
    } finally {
      setLoading(false);
    }
  };

  return (
    <main className="page">
      <Link className="back-link" href="/patients">← Back to patients</Link>

      <div className="topbar">
        <h1>Clinical summary</h1>
      </div>

      <button className="btn btn-primary" onClick={generateSummary} disabled={loading}>
        {loading ? 'Generating...' : 'Generate clinical summary'}
      </button>

      {error && <p className="error-text">{error}</p>}

      {summary && <div className="summary-box">{summary}</div>}
    </main>
  );
}
