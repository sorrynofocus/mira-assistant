import { ApiError, asEventList, asRequestList, getJson, type PipelineEvent, type PipelineRequestSummary } from './api';

export async function listDiagnosticRequests(conversationId: string | null): Promise<PipelineRequestSummary[]> {
  const path = conversationId
    ? `/api/diagnostics?conversationId=${encodeURIComponent(conversationId)}`
    : '/api/diagnostics';
  return asRequestList(await getJson<unknown>(path));
}

export async function getDiagnosticEvents(requestId: string): Promise<PipelineEvent[]> {
  return asEventList(await getJson<unknown>(`/api/diagnostics/${encodeURIComponent(requestId)}`));
}

export interface InspectorClient {
  listRequests(conversationId: string | null): Promise<PipelineRequestSummary[]>;
  getEvents(requestId: string): Promise<PipelineEvent[]>;
}

export const liveInspectorClient: InspectorClient = {
  listRequests: listDiagnosticRequests,
  getEvents: getDiagnosticEvents,
};

export function formatElapsed(ms: number | null | undefined): string {
  if (ms == null || Number.isNaN(ms)) return '—';
  return `${ms} ms`;
}

export function formatTimestamp(value: string | null | undefined): string {
  if (!value) return '—';
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  return parsed.toLocaleString();
}

export function utcTimestamp(value: string | null | undefined): string {
  if (!value) return '';
  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) return value;
  return parsed.toISOString();
}

function stampTime(doc: Document, value: string, className: string): HTMLTimeElement {
  const stamp = doc.createElement('time');
  stamp.className = className;
  const utc = utcTimestamp(value) || value;
  stamp.dateTime = utc;
  stamp.title = utc;
  stamp.setAttribute('datetime', utc);
  stamp.setAttribute('title', utc);
  stamp.textContent = formatTimestamp(value);
  return stamp;
}

// Labelled control that opens the inspector on a message's requestId.
export function renderRequestLink(
  doc: Document,
  requestId: string,
  onInspect: (requestId: string) => void,
): HTMLButtonElement {
  const button = doc.createElement('button');
  button.type = 'button';
  button.className = 'link-button message__inspect';
  button.textContent = 'Inspect request';
  button.setAttribute('aria-label', `Open inspector for request ${requestId}`);
  button.addEventListener('click', () => onInspect(requestId));
  return button;
}

export function firstTokenFromEvents(events: PipelineEvent[]): number | null {
  for (const event of events) {
    const value = event.data?.firstTokenMs;
    if (typeof value === 'number') return value;
  }
  return null;
}

export interface InspectorRequestHandlers {
  onSelect(requestId: string): void;
}

// Inspector values are untrusted server text: assigned with textContent only, never innerHTML.
export function renderRequestList(
  list: HTMLElement,
  requests: PipelineRequestSummary[],
  selectedRequestId: string | null,
  handlers: InspectorRequestHandlers,
): void {
  const doc = list.ownerDocument;
  list.replaceChildren();

  for (const request of requests) {
    const item = doc.createElement('li');
    item.className = 'inspector-request' + (request.requestId === selectedRequestId ? ' is-selected' : '');
    item.dataset.requestId = request.requestId;

    const open = doc.createElement('button');
    open.type = 'button';
    open.className = 'inspector-request__button';
    if (request.requestId === selectedRequestId) open.setAttribute('aria-current', 'true');

    const idLine = doc.createElement('span');
    idLine.className = 'inspector-request__id';
    idLine.textContent = request.requestId;

    const meta = doc.createElement('span');
    meta.className = 'inspector-request__meta';
    const started = stampTime(doc, request.startedAt, 'inspector-request__time');
    const rest = doc.createElement('span');
    const parts = [
      request.lastEventType || 'unknown',
      request.firstTokenMs != null ? `First token ${formatElapsed(request.firstTokenMs)}` : 'First token not recorded',
    ];
    if (request.totalMs != null) parts.push(`Total ${formatElapsed(request.totalMs)}`);
    rest.textContent = ` · ${parts.join(' · ')}`;

    meta.append(started, rest);
    open.append(idLine, meta);
    open.addEventListener('click', () => handlers.onSelect(request.requestId));
    item.append(open);
    list.append(item);
  }
}

export function focusRequestItem(list: HTMLElement, requestId: string): boolean {
  const items = list.querySelectorAll('li');
  for (let i = 0; i < items.length; i++) {
    const item = items[i] as HTMLElement;
    if (item.dataset.requestId !== requestId) continue;
    const target = item.querySelector('button') as HTMLElement | null;
    target?.focus();
    return Boolean(target);
  }
  return false;
}

export function renderEventList(list: HTMLElement, events: PipelineEvent[]): void {
  const doc = list.ownerDocument;
  list.replaceChildren();

  for (const event of events) {
    const item = doc.createElement('li');
    item.className = 'inspector-event';
    item.dataset.eventType = event.eventType;
    item.dataset.elapsedMs = String(event.elapsedMs);

    const type = doc.createElement('span');
    type.className = 'inspector-event__type';
    type.textContent = event.eventType;

    const elapsed = doc.createElement('span');
    elapsed.className = 'inspector-event__elapsed';
    elapsed.textContent = formatElapsed(event.elapsedMs);

    const stamp = stampTime(doc, event.createdAt, 'inspector-event__time');

    const extra = doc.createElement('span');
    extra.className = 'inspector-event__extra';
    extra.textContent = eventExtra(event);

    item.append(type, elapsed, stamp, extra);
    list.append(item);
  }
}

// data always includes all nine allowlist keys; unused values are null and are omitted here (not shown as "null").
function eventExtra(event: PipelineEvent): string {
  const parts: string[] = [];
  const data = event.data ?? {};
  if (data.modelId) parts.push(`Model ${data.modelId}`);
  if (data.status) parts.push(`Status ${data.status}`);
  if (data.inputChars != null) parts.push(`Input ${data.inputChars} chars`);
  if (data.outputChars != null) parts.push(`Output ${data.outputChars} chars`);
  if (data.promptTurns != null) parts.push(`Turns ${data.promptTurns}`);
  if (data.deltaCount != null) parts.push(`Deltas ${data.deltaCount}`);
  if (data.firstTokenMs != null) parts.push(`First token ${formatElapsed(data.firstTokenMs)}`);
  if (data.totalMs != null) parts.push(`Total ${formatElapsed(data.totalMs)}`);
  if (data.errorCode) parts.push(`Error ${data.errorCode}`);
  return parts.join(' · ');
}

function describeInspectorError(error: unknown, missing: 'request' | 'conversation'): string {
  if (error instanceof ApiError && error.status === 404) {
    return missing === 'request'
      ? 'That request is no longer available.'
      : 'That conversation is no longer available.';
  }
  return error instanceof Error ? error.message : 'Could not load inspector data.';
}

export interface InspectorView {
  panel: HTMLElement;
  requests: HTMLElement;
  events: HTMLElement;
  eventsSection: HTMLElement;
  empty: HTMLElement;
  error: HTMLElement;
  firstToken: HTMLElement;
}

export class InspectorController {
  requests: PipelineRequestSummary[] = [];
  events: PipelineEvent[] = [];
  selectedRequestId: string | null = null;
  conversationId: string | null = null;

  constructor(
    private readonly client: InspectorClient,
    private readonly view: InspectorView,
    private readonly onStatus: (message: string) => void,
  ) {}

  get open(): boolean {
    return !this.view.panel.hidden;
  }

  setOpen(open: boolean): void {
    this.view.panel.hidden = !open;
  }

  async load(conversationId: string | null): Promise<void> {
    this.conversationId = conversationId;
    this.selectedRequestId = null;
    this.events = [];
    this.#showError(null);
    try {
      this.requests = await this.client.listRequests(conversationId);
    } catch (error) {
      this.requests = [];
      this.#showError(describeInspectorError(error, 'conversation'));
    }
    this.#render();
  }

  async select(requestId: string): Promise<void> {
    this.selectedRequestId = requestId;
    this.#showError(null);
    try {
      this.events = await this.client.getEvents(requestId);
      this.onStatus(`Loaded events for request ${requestId}.`);
    } catch (error) {
      this.events = [];
      this.#showError(describeInspectorError(error, 'request'));
    }
    this.#render(requestId);
  }

  async reveal(requestId: string, conversationId: string | null): Promise<void> {
    this.setOpen(true);
    await this.load(conversationId);
    await this.select(requestId);
  }

  reset(): void {
    this.requests = [];
    this.events = [];
    this.selectedRequestId = null;
    this.conversationId = null;
    this.#showError(null);
    this.#render();
  }

  #showError(message: string | null): void {
    this.view.error.textContent = message ?? '';
    this.view.error.hidden = !message;
  }

  #render(focusRequestId?: string | null): void {
    const empty = this.requests.length === 0;
    this.view.empty.hidden = !empty;
    this.view.requests.hidden = empty;
    if (empty) {
      this.view.empty.textContent = 'No recorded requests for this conversation.';
    }
    renderRequestList(this.view.requests, this.requests, this.selectedRequestId, {
      onSelect: (id) => {
        void this.select(id);
      },
    });
    if (focusRequestId) focusRequestItem(this.view.requests, focusRequestId);

    const showEvents = this.selectedRequestId != null;
    this.view.eventsSection.hidden = !showEvents;
    if (showEvents) {
      const latency = this.#firstToken();
      this.view.firstToken.textContent =
        latency != null ? `First-token latency: ${formatElapsed(latency)}` : 'First-token latency: not recorded';
      if (this.events.length === 0 && !this.view.error.textContent) {
        this.view.firstToken.textContent = 'No events for this request.';
      }
      renderEventList(this.view.events, this.events);
    } else {
      this.view.firstToken.textContent = '';
      this.view.events.replaceChildren();
    }
  }

  #firstToken(): number | null {
    const fromEvents = firstTokenFromEvents(this.events);
    if (fromEvents != null) return fromEvents;
    const selected = this.requests.find((request) => request.requestId === this.selectedRequestId);
    return selected?.firstTokenMs ?? null;
  }
}
