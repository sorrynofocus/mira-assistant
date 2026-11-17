// Same-origin JSON helpers. Every state-changing request carries the antiforgery token in X-CSRF-TOKEN.
//
// Conversation and diagnostics response shapes live here (final API contract in docs/M2_CHUNKS.md,
// verified against ConversationEndpoints.cs, DiagnosticsEndpoints.cs and ChatEndpoints.cs).
// Helpers still accept a bare array or a wrapper so a small rename does not require changes at every call site.

export class ApiError extends Error {
  constructor(
    readonly status: number,
    message: string,
  ) {
    super(message);
  }
}

export const CONVERSATION_LIMITS = {
  maxTitleLength: 200,
  defaultPageSize: 50,
  maxPageSize: 100,
} as const;

export type ChatRole = 'user' | 'assistant';
export type MessageStatus = 'streaming' | 'completed' | 'interrupted' | 'failed';

export interface ConversationMessage {
  id: string;
  role: ChatRole;
  content: string;
  status: MessageStatus;
  createdAt: string;
  modelId: string | null;
  requestId: string | null;
}

export interface ConversationSummary {
  id: string;
  title: string | null;
  createdAt: string;
  updatedAt: string;
}

// GET /api/conversations/{id} and GET /api/conversations/current return this Conversation shape (no ownerUserId).
// The unwrap still tolerates the older { id, messages } body if title or timestamps are omitted.
export interface ConversationSnapshot {
  id: string;
  ownerUserId?: string;
  title: string | null;
  createdAt: string;
  updatedAt: string;
  messages: ConversationMessage[];
}

export interface PipelineEventData {
  modelId?: string | null;
  status?: MessageStatus | null;
  inputChars?: number | null;
  outputChars?: number | null;
  promptTurns?: number | null;
  deltaCount?: number | null;
  firstTokenMs?: number | null;
  totalMs?: number | null;
  errorCode?: string | null;
}

// GET /api/diagnostics/{requestId} returns { requestId, conversationId, events }.
// Each event is PipelineEventResponse: eventType, elapsedMs, createdAt, data (no event/owner ids).
export interface PipelineEvent {
  eventType: string;
  elapsedMs: number;
  createdAt: string;
  data: PipelineEventData;
  id?: string;
  requestId?: string;
  conversationId?: string | null;
  ownerUserId?: string;
}

export interface PipelineRequestSummary {
  requestId: string;
  conversationId: string | null;
  startedAt: string;
  lastEventType: string;
  firstTokenMs: number | null;
  totalMs: number | null;
}

export function unwrapList<T>(body: unknown, keys: string[] = ['items']): T[] {
  if (Array.isArray(body)) return body as T[];
  if (typeof body === 'object' && body !== null) {
    const record = body as Record<string, unknown>;
    for (const key of keys) {
      if (Array.isArray(record[key])) return record[key] as T[];
    }
  }
  return [];
}

export interface ConversationPage {
  items: ConversationSummary[];
  skip: number;
  take: number;
  hasMore: boolean;
}

export function asConversationList(body: unknown): ConversationSummary[] {
  return unwrapList<ConversationSummary>(body, ['items', 'conversations']);
}

export function asConversationPage(body: unknown): ConversationPage {
  const items = asConversationList(body);
  if (typeof body === 'object' && body !== null && !Array.isArray(body)) {
    const record = body as { skip?: unknown; take?: unknown; hasMore?: unknown };
    return {
      items,
      skip: typeof record.skip === 'number' ? record.skip : 0,
      take: typeof record.take === 'number' ? record.take : CONVERSATION_LIMITS.defaultPageSize,
      hasMore: record.hasMore === true,
    };
  }
  return { items, skip: 0, take: CONVERSATION_LIMITS.defaultPageSize, hasMore: false };
}

export function asRequestList(body: unknown): PipelineRequestSummary[] {
  return unwrapList<PipelineRequestSummary>(body, ['items', 'requests']);
}

export function asEventList(body: unknown): PipelineEvent[] {
  return unwrapList<PipelineEvent>(body, ['events', 'items']);
}

export function asConversationSnapshot(body: unknown): ConversationSnapshot {
  if (typeof body !== 'object' || body === null || !('id' in body)) {
    throw new Error('Unexpected conversation response.');
  }
  const record = body as Partial<ConversationSnapshot> & { id: unknown; messages?: unknown };
  return {
    id: String(record.id),
    ownerUserId: record.ownerUserId,
    title: record.title ?? null,
    createdAt: record.createdAt ?? '',
    updatedAt: record.updatedAt ?? '',
    messages: Array.isArray(record.messages) ? record.messages : [],
  };
}

export function asConversationSummary(body: unknown): ConversationSummary {
  if (typeof body !== 'object' || body === null || !('id' in body)) {
    throw new Error('Unexpected conversation response.');
  }
  const record = body as Partial<ConversationSummary> & { id: unknown };
  return {
    id: String(record.id),
    title: record.title ?? null,
    createdAt: record.createdAt ?? '',
    updatedAt: record.updatedAt ?? '',
  };
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
  return (await readJson<T>(response)) as T;
}

export async function postJson<T>(path: string, body?: unknown): Promise<T> {
  const response = await mutate('POST', path, body);
  if (!response.ok) throw await toError(response);
  return (await readJson<T>(response)) as T;
}

export async function patchJson<T>(path: string, body: unknown): Promise<T | undefined> {
  const response = await mutate('PATCH', path, body);
  if (!response.ok) throw await toError(response);
  return readJson<T>(response);
}

export async function deleteRequest(path: string): Promise<void> {
  const response = await mutate('DELETE', path);
  if (!response.ok) throw await toError(response);
}

// Returns the raw response so the caller can read the event stream.
export async function postStream(path: string, body: unknown, signal: AbortSignal): Promise<Response> {
  const response = await mutate('POST', path, body, signal);
  if (!response.ok) throw await toError(response);
  if (!response.body) throw new ApiError(response.status, 'The response had no body.');
  return response;
}

function mutate(method: 'POST' | 'PATCH' | 'DELETE', path: string, body?: unknown, signal?: AbortSignal): Promise<Response> {
  const headers: Record<string, string> = {};
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (csrfToken) headers['X-CSRF-TOKEN'] = csrfToken;
  return fetch(path, {
    method,
    credentials: 'same-origin',
    cache: 'no-store',
    headers,
    body: body !== undefined ? JSON.stringify(body) : undefined,
    signal,
  });
}

async function readJson<T>(response: Response): Promise<T | undefined> {
  if (response.status === 204) return undefined;
  const text = await response.text();
  if (!text) return undefined;
  return JSON.parse(text) as T;
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
