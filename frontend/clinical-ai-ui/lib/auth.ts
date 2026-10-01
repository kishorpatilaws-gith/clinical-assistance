const SUPABASE_URL = process.env.NEXT_PUBLIC_SUPABASE_URL!;
const SUPABASE_ANON_KEY = process.env.NEXT_PUBLIC_SUPABASE_ANON_KEY!;
const API_URL = process.env.NEXT_PUBLIC_API_URL!;

const ACCESS = 'access_token';
const REFRESH = 'refresh_token';
const EXPIRES_AT = 'expires_at';
const EMAIL = 'user_email';

// Renew this far ahead of expiry, so a request never leaves with a token that
// dies in flight.
const RENEW_MARGIN_SECONDS = 60;

interface SupabaseSession {
  access_token: string;
  refresh_token?: string;
  expires_in?: number;
  expires_at?: number;
  user?: { email?: string };
}

function saveSession(data: SupabaseSession, fallbackEmail?: string) {
  localStorage.setItem(ACCESS, data.access_token);
  // A refresh response may omit the email; never overwrite a good value with ''.
  const email = data.user?.email ?? fallbackEmail;
  if (email) localStorage.setItem(EMAIL, email);
  if (data.refresh_token) localStorage.setItem(REFRESH, data.refresh_token);

  const expiresAt =
    data.expires_at ?? Math.floor(Date.now() / 1000) + (data.expires_in ?? 3600);
  localStorage.setItem(EXPIRES_AT, String(expiresAt));
}

export async function login(email: string, password: string): Promise<string> {
  const res = await fetch(`${SUPABASE_URL}/auth/v1/token?grant_type=password`, {
    method: 'POST',
    headers: { apikey: SUPABASE_ANON_KEY, 'Content-Type': 'application/json' },
    body: JSON.stringify({ email, password }),
  });

  if (!res.ok) {
    const err = await res.json().catch(() => ({}));
    throw new Error(err.error_description || err.msg || 'Login failed');
  }

  const data: SupabaseSession = await res.json();
  saveSession(data, email);
  return data.access_token;
}

export function getToken(): string | null {
  if (typeof window === 'undefined') return null;
  return localStorage.getItem(ACCESS);
}

export function getUserEmail(): string | null {
  if (typeof window === 'undefined') return null;
  return localStorage.getItem(EMAIL);
}

export function logout() {
  [ACCESS, REFRESH, EXPIRES_AT, EMAIL].forEach(k => localStorage.removeItem(k));
}

function expiresWithin(seconds: number): boolean {
  const at = Number(localStorage.getItem(EXPIRES_AT));
  if (!at) return false;             // unknown expiry: let the 401 path handle it
  return at - Date.now() / 1000 <= seconds;
}

// Supabase access tokens last an hour. Without this the session hard-expires and
// every call 401s until the user works out they need to sign in again.
//
// A single shared promise, so ten concurrent 401s trigger one refresh rather than
// ten - and a refresh token is single-use, so racing requests would otherwise
// invalidate each other's.
let inFlightRefresh: Promise<string | null> | null = null;

function refreshSession(): Promise<string | null> {
  if (inFlightRefresh) return inFlightRefresh;

  inFlightRefresh = (async () => {
    const refreshToken = localStorage.getItem(REFRESH);
    if (!refreshToken) return null;

    try {
      const res = await fetch(`${SUPABASE_URL}/auth/v1/token?grant_type=refresh_token`, {
        method: 'POST',
        headers: { apikey: SUPABASE_ANON_KEY, 'Content-Type': 'application/json' },
        body: JSON.stringify({ refresh_token: refreshToken }),
      });
      if (!res.ok) return null;

      const data: SupabaseSession = await res.json();
      if (!data.access_token) return null;

      saveSession(data, getUserEmail() ?? undefined);
      return data.access_token;
    } catch {
      return null;                    // network failure: treat as unrecoverable
    } finally {
      inFlightRefresh = null;
    }
  })();

  return inFlightRefresh;
}

function redirectToLogin() {
  if (typeof window === 'undefined') return;
  if (window.location.pathname === '/login') return;   // no redirect loop
  logout();
  window.location.assign('/login');
}

function send(path: string, options: RequestInit, token: string | null) {
  return fetch(`${API_URL}${path}`, {
    ...options,
    headers: {
      ...(options.headers || {}),
      // Omit the header entirely when there is no token: sending the literal
      // "Bearer null" reads as a malformed token rather than a missing one.
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
  });
}

/**
 * Authenticated fetch. Renews the session ahead of expiry, and on a 401 refreshes
 * once and retries before giving up and sending the user to sign in. Callers only
 * need to handle real outcomes - 403, 404, validation errors - not expiry.
 *
 * Note: the retry re-sends `options.body`. FormData and string bodies replay
 * cleanly; a single-use ReadableStream body would not, and none are used here.
 */
export async function apiFetch(path: string, options: RequestInit = {}) {
  let token = getToken();

  if (token && expiresWithin(RENEW_MARGIN_SECONDS)) {
    token = (await refreshSession()) ?? token;
  }

  let res = await send(path, options, token);
  if (res.status !== 401) return res;

  const renewed = await refreshSession();
  if (renewed) {
    res = await send(path, options, renewed);
    if (res.status !== 401) return res;
  }

  redirectToLogin();
  return res;
}
