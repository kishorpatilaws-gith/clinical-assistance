'use client';
import { useEffect, useState } from 'react';
import { useRouter } from 'next/navigation';
import Link from 'next/link';
import { apiFetch, getToken, getUserEmail, logout } from '@/lib/auth';

interface Patient {
  id: string;
  firstName: string;
  lastName: string;
  dateOfBirth: string;
}

export default function PatientsPage() {
  const [patients, setPatients] = useState<Patient[]>([]);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(true);
  const [userEmail, setUserEmail] = useState('');
  const router = useRouter();

  useEffect(() => {
    // Read after mount only: reading localStorage during render makes the server
    // and first client render disagree, which triggers a hydration mismatch.
    setUserEmail(getUserEmail() ?? '');

    if (!getToken()) {
      router.push('/login');
      return;
    }
    (async () => {
      try {
        const res = await apiFetch('/api/patient');
        if (res.status === 401) return;          // apiFetch is redirecting to /login
        if (res.status === 403) {
          setError('You do not have doctor access.');
          return;
        }
        if (!res.ok) {
          setError(`Could not load patients (${res.status}).`);
          return;
        }
        setPatients(await res.json());
      } catch {
        setError('Could not reach the API. Is it running on port 5258?');
      } finally {
        setLoading(false);
      }
    })();
  }, []);

  return (
    <main className="page">
      <div className="topbar">
        <h1>Patients</h1>
        <div className="topbar-meta">
          <span>{userEmail}</span>
          <button className="btn" onClick={() => { logout(); router.push('/login'); }}>
            Sign out
          </button>
        </div>
      </div>

      <p className="topbar-meta" style={{ marginBottom: 16 }}>
        <Link href="/ask">Ask the documents</Link>
        <Link href="/documents">Manage documents</Link>
      </p>

      {error && <p className="error-text">{error}</p>}

      {!error && loading && <p className="empty-note">Loading...</p>}

      {!error && !loading && patients.length === 0 && (
        <p className="empty-note">No patients on record.</p>
      )}

      <ul className="record-list">
        {patients.map(p => (
          <li className="record-row" key={p.id}>
            <Link className="record-name" href={`/patients/${p.id}`}>
              {p.firstName} {p.lastName}
            </Link>
            <span className="record-meta">DOB {p.dateOfBirth}</span>
          </li>
        ))}
      </ul>
    </main>
  );
}
