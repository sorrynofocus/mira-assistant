// Same-origin JSON helpers. Every state-changing request carries the antiforgery token in X-CSRF-TOKEN.

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

let csrfToken: string | null = null;

export function setCsrfToken(token: string): void {
  csrfToken = token;
}

export async function refreshCsrfToken(): Promise<void> {
  const body = await getJson<{ csrfToken: string }>('/api/auth/csrf');
  csrfToken = body.csrfToken;
}

export async function getJson<T>(path: string): Promise<T> {
  const response = await fetch(path, { credentials: 'same-origin', cache: 'no-store' });
  if (!response.ok) throw await toError(response);
  return (await response.json()) as T;
}

export async function postJson<T>(path: string, body: unknown): Promise<T> {
  const response = await post(path, body);
  if (!response.ok) throw await toError(response);
  return (await response.json()) as T;
}

// Returns the raw response so the caller can read the event stream.
export async function postStream(path: string, body: unknown, signal: AbortSignal): Promise<Response> {
  const response = await post(path, body, signal);
  if (!response.ok) throw await toError(response);
  if (!response.body) throw new ApiError(response.status, 'The response had no body.');
  return response;
}

function post(path: string, body: unknown, signal?: AbortSignal): Promise<Response> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (csrfToken) headers['X-CSRF-TOKEN'] = csrfToken;
  return fetch(path, {
    method: 'POST',
    credentials: 'same-origin',
    cache: 'no-store',
    headers,
    body: JSON.stringify(body),
    signal,
  });
}

async function toError(response: Response): Promise<ApiError> {
  let message = `Request failed (HTTP ${response.status}).`;
  try {
    const body: unknown = await response.json();
    if (typeof body === 'object' && body !== null && 'title' in body && typeof body.title === 'string') {
      message = body.title;
    }
  } catch {
    // Non-JSON error body; keep the generic message.
  }
  return new ApiError(response.status, message);
}
