import { afterEach, describe, expect, it, vi } from 'vitest';
import { setCsrfToken } from './api';
import { SIGN_OUT_FAILED, signOut } from './session';

type Reply = Response | Error;

function json(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

// Answers fetch calls in order and records each request's path and CSRF header.
function stubFetch(...replies: Reply[]) {
  const calls: { path: string; csrf: string | null }[] = [];
  vi.stubGlobal('fetch', async (path: string, init?: RequestInit) => {
    const headers = (init?.headers ?? {}) as Record<string, string>;
    calls.push({ path, csrf: headers['X-CSRF-TOKEN'] ?? null });
    const reply = replies.shift();
    if (!reply) throw new Error(`Unexpected request to ${path}`);
    if (reply instanceof Error) throw reply;
    return reply;
  });
  return calls;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe('signOut', () => {
  it('signs out when the server revokes the session', async () => {
    setCsrfToken('before');
    const calls = stubFetch(json(200, { csrfToken: 'anonymous' }));

    expect(await signOut()).toEqual({ signedOut: true });
    expect(calls).toEqual([{ path: '/api/auth/logout', csrf: 'before' }]);
  });

  it('treats an already-ended session (401) as signed out', async () => {
    const calls = stubFetch(json(401, { title: 'Unauthorized' }), json(200, { csrfToken: 'anonymous' }));

    expect(await signOut()).toEqual({ signedOut: true });
    expect(calls.map((call) => call.path)).toEqual(['/api/auth/logout', '/api/auth/csrf']);
  });

  it('stays signed in when the API is unreachable', async () => {
    stubFetch(new TypeError('Failed to fetch'));

    expect(await signOut()).toEqual({ signedOut: false, message: SIGN_OUT_FAILED });
  });

  it('stays signed in when the server fails', async () => {
    stubFetch(json(500, { title: 'Server error' }));

    expect(await signOut()).toEqual({ signedOut: false, message: SIGN_OUT_FAILED });
  });

  it('retries once with a fresh CSRF token after a 403', async () => {
    setCsrfToken('stale');
    const calls = stubFetch(
      json(403, { title: 'CSRF validation failed.' }),
      json(200, { csrfToken: 'fresh' }),
      json(200, { csrfToken: 'anonymous' }),
    );

    expect(await signOut()).toEqual({ signedOut: true });
    expect(calls).toEqual([
      { path: '/api/auth/logout', csrf: 'stale' },
      { path: '/api/auth/csrf', csrf: null },
      { path: '/api/auth/logout', csrf: 'fresh' },
    ]);
  });

  it('stays signed in when the retry is refused too', async () => {
    const calls = stubFetch(
      json(403, { title: 'Cross-origin request refused.' }),
      json(200, { csrfToken: 'fresh' }),
      json(403, { title: 'Cross-origin request refused.' }),
    );

    expect(await signOut()).toEqual({ signedOut: false, message: SIGN_OUT_FAILED });
    expect(calls).toHaveLength(3);
  });
});
