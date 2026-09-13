// 'use client';
// import { useState } from 'react';

// export default function Home() {
//   const [prompt, setPrompt] = useState('');
//   const [response, setResponse] = useState('');
//   const [loading, setLoading] = useState(false);

//   const callApi = async () => {
//     setLoading(true);
//     const res = await fetch('http://localhost:5258/api/bedrock/invoke', {
//       method: 'POST',
//       headers: { 'Content-Type': 'application/json' },
//       body: JSON.stringify({ prompt }),
//     });
//     const data = await res.json();
//     setResponse(data.response);
//     setLoading(false);
//   };

//   return (
//     <main style={{ padding: 40 }}>
//       <h1>Bedrock Connectivity Test</h1>
//       <textarea value={prompt} onChange={e => setPrompt(e.target.value)} rows={4} cols={50} />
//       <br />
//       <button onClick={callApi} disabled={loading}>
//         {loading ? 'Calling Bedrock...' : 'Send'}
//       </button>
//       <p>{response}</p>
//     </main>
//   );
// }

'use client';
import { useEffect } from 'react';
import { useRouter } from 'next/navigation';
import { getToken } from '@/lib/auth';

export default function Home() {
  const router = useRouter();
  useEffect(() => {
    router.push(getToken() ? '/patients' : '/login');
  }, []);
  return null;
}