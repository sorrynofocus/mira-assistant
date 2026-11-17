import { describe, expect, it, vi } from 'vitest';
import { ApiError, type PipelineEvent, type PipelineEventData, type PipelineRequestSummary } from './api';
import {
  InspectorController,
  type InspectorClient,
  type InspectorView,
  firstTokenFromEvents,
  formatElapsed,
  formatTimestamp,
  focusRequestItem,
  renderEventList,
  renderRequestLink,
  renderRequestList,
  utcTimestamp,
} from './inspector';

const STARTED = '2026-11-16T04:00:00.000Z';
const FIRST = '2026-11-16T04:00:00.040Z';
const DONE = '2026-11-16T04:00:00.800Z';

type FakeEvent = { type: string; preventDefault(): void };

class FakeNode {
  tagName: string;
  childNodes: FakeNode[] = [];
  attrs: Record<string, string> = {};
  listeners: Record<string, Array<(event: FakeEvent) => void>> = {};
  ownerDocument: FakeDocument;
  dataset: Record<string, string> = {};
  className = '';
  id = '';
  hidden = false;
  type = '';
  dateTime = '';
  title = '';
  #text = '';

  constructor(tag: string, ownerDocument: FakeDocument) {
    this.tagName = tag.toUpperCase();
    this.ownerDocument = ownerDocument;
  }

  get textContent(): string {
    if (this.childNodes.length === 0) return this.#text;
    return this.childNodes.map((child) => child.textContent).join('');
  }

  set textContent(value: string) {
    this.#text = value ?? '';
    this.childNodes = [];
  }

  setAttribute(name: string, value: string): void {
    this.attrs[name] = value;
    if (name === 'id') this.id = value;
    if (name === 'datetime') this.dateTime = value;
    if (name === 'title') this.title = value;
    if (name.startsWith('data-')) {
      const key = name.slice(5).replace(/-([a-z])/g, (_, letter: string) => letter.toUpperCase());
      this.dataset[key] = value;
    }
  }

  getAttribute(name: string): string | null {
    if (name === 'id') return this.id || this.attrs.id || null;
    return this.attrs[name] ?? null;
  }

  append(...nodes: FakeNode[]): void {
    this.childNodes.push(...nodes);
  }

  replaceChildren(...nodes: FakeNode[]): void {
    this.childNodes = [];
    this.#text = '';
    this.append(...nodes);
  }

  addEventListener(type: string, listener: (event: FakeEvent) => void): void {
    (this.listeners[type] ??= []).push(listener);
  }

  dispatch(type: string, init: Partial<FakeEvent> = {}): void {
    const event: FakeEvent = { type, preventDefault() {}, ...init };
    for (const listener of this.listeners[type] ?? []) listener(event);
  }

  click(): void {
    this.dispatch('click');
  }

  focus(): void {
    this.ownerDocument.activeElement = this;
  }

  querySelectorAll(selector: string): FakeNode[] {
    const matches: FakeNode[] = [];
    walk(this, (node) => {
      if (node !== this && matchesSelector(node, selector)) matches.push(node);
    });
    return matches;
  }

  querySelector(selector: string): FakeNode | null {
    return this.querySelectorAll(selector)[0] ?? null;
  }
}

class FakeDocument {
  activeElement: FakeNode | null = null;

  createElement(tag: string): FakeNode {
    return new FakeNode(tag, this);
  }
}

function walk(node: FakeNode, visit: (node: FakeNode) => void): void {
  visit(node);
  for (const child of node.childNodes) walk(child, visit);
}

function matchesSelector(node: FakeNode, selector: string): boolean {
  if (selector.startsWith('.')) return node.className.split(/\s+/).includes(selector.slice(1));
  return node.tagName === selector.toUpperCase();
}

function asElement(node: FakeNode): HTMLElement {
  return node as unknown as HTMLElement;
}

function allowlist(overrides: Partial<PipelineEventData> = {}): PipelineEventData {
  return {
    modelId: null,
    status: null,
    inputChars: null,
    outputChars: null,
    promptTurns: null,
    deltaCount: null,
    firstTokenMs: null,
    totalMs: null,
    errorCode: null,
    ...overrides,
  };
}

function event(
  eventType: string,
  elapsedMs: number,
  createdAt: string,
  data: PipelineEventData = allowlist(),
): PipelineEvent {
  return { eventType, elapsedMs, data, createdAt };
}

const redactedEvents: PipelineEvent[] = [
  event('request.started', 0, STARTED, allowlist({ inputChars: 12, promptTurns: 2, modelId: 'mock-chat' })),
  event('response.first_token', 40, FIRST, allowlist({ firstTokenMs: 40 })),
  event('response.completed', 800, DONE, allowlist({ status: 'completed', outputChars: 24, deltaCount: 3, totalMs: 800, firstTokenMs: 40 })),
  event('response.failed', 900, DONE, allowlist({ status: 'failed', modelId: 'unlisted', errorCode: 'provider_error', outputChars: 0, deltaCount: 0, totalMs: 900 })),
];

const terminalTypes = ['response.completed', 'response.cancelled', 'response.aborted', 'response.timeout', 'response.failed'];

const request: PipelineRequestSummary = {
  requestId: 'req-fixture-alpha',
  conversationId: 'convo-alpha',
  startedAt: STARTED,
  lastEventType: 'response.completed',
  firstTokenMs: 40,
  totalMs: 800,
};

describe('renderEventList and renderRequestList', () => {
  it('renders a redacted event list with type, elapsed ms, timestamps and first-token latency', () => {
    const doc = new FakeDocument();
    const events = doc.createElement('ol');
    const requests = doc.createElement('ul');
    const onSelect = vi.fn();

    renderEventList(asElement(events), redactedEvents);
    renderRequestList(asElement(requests), [request], 'req-fixture-alpha', { onSelect });

    expect(events.querySelectorAll('img')).toHaveLength(0);
    expect(events.querySelectorAll('li')).toHaveLength(4);
    expect(events.textContent).toContain('request.started');
    expect(events.textContent).toContain('response.first_token');
    expect(events.textContent).toContain('response.completed');
    expect(events.textContent).toContain('response.failed');
    expect(events.textContent).toContain('0 ms');
    expect(events.textContent).toContain('40 ms');
    expect(events.textContent).toContain('800 ms');
    expect(events.textContent).toContain('First token 40 ms');
    expect(events.textContent).toContain('Model unlisted');
    expect(events.textContent).toContain('Error provider_error');
    expect(events.textContent).not.toContain('null');
    expect(events.textContent).not.toMatch(/password|Bearer |sk-|secret/i);
    for (const type of terminalTypes) {
      expect(['response.completed', 'response.cancelled', 'response.aborted', 'response.timeout', 'response.failed']).toContain(type);
    }

    const times = events.querySelectorAll('time');
    expect(times).toHaveLength(4);
    expect(times[0]?.getAttribute('title') ?? times[0]?.dateTime).toBe(STARTED);
    expect(times[0]?.dateTime).toBe(STARTED);
    expect(times[0]?.textContent).toBe(formatTimestamp(STARTED));
    expect(formatTimestamp(STARTED)).toBe(new Date(STARTED).toLocaleString());
    expect(utcTimestamp(STARTED)).toBe(STARTED);
    expect(formatTimestamp(STARTED)).not.toBe(STARTED);

    expect(firstTokenFromEvents(redactedEvents)).toBe(40);
    expect(requests.textContent).toContain('req-fixture-alpha');
    expect(requests.textContent).toContain('First token 40 ms');
    expect(requests.textContent).toContain('response.completed');
    const requestTime = requests.querySelector('time');
    if (!requestTime) throw new Error('Request timestamp was not rendered');
    expect(requestTime.getAttribute('title') ?? requestTime.dateTime).toBe(STARTED);
    expect(requestTime.textContent).toBe(formatTimestamp(STARTED));

    const unsafeType = event('<img src=x onerror=alert(1)>', 1, STARTED);
    const probe = doc.createElement('ol');
    renderEventList(asElement(probe), [unsafeType]);
    expect(probe.querySelectorAll('img')).toHaveLength(0);
    expect(probe.textContent).toContain('<img src=x onerror=alert(1)>');
  });

  it('shows an empty state when there are no requests', async () => {
    const doc = new FakeDocument();
    const view = {
      panel: doc.createElement('aside'),
      requests: doc.createElement('ul'),
      events: doc.createElement('ol'),
      eventsSection: doc.createElement('section'),
      empty: doc.createElement('p'),
      error: doc.createElement('p'),
      firstToken: doc.createElement('p'),
    };
    const client: InspectorClient = {
      listRequests: vi.fn().mockResolvedValue([]),
      getEvents: vi.fn(),
    };
    const controller = new InspectorController(client, view as unknown as InspectorView, vi.fn());

    await controller.load('convo-alpha');

    expect(client.listRequests).toHaveBeenCalledWith('convo-alpha');
    expect(view.empty.hidden).toBe(false);
    expect(view.requests.hidden).toBe(true);
    expect(view.empty.textContent).toBe('No recorded requests for this conversation.');
    expect(view.eventsSection.hidden).toBe(true);
    expect(view.events.childNodes).toHaveLength(0);
  });

  it('loads events for a selected request and reports first-token latency', async () => {
    const doc = new FakeDocument();
    const view = {
      panel: doc.createElement('aside'),
      requests: doc.createElement('ul'),
      events: doc.createElement('ol'),
      eventsSection: doc.createElement('section'),
      empty: doc.createElement('p'),
      error: doc.createElement('p'),
      firstToken: doc.createElement('p'),
    };
    const client: InspectorClient = {
      listRequests: vi.fn().mockResolvedValue([request]),
      getEvents: vi.fn().mockResolvedValue(redactedEvents),
    };
    const controller = new InspectorController(client, view as unknown as InspectorView, vi.fn());

    await controller.load('convo-alpha');
    expect(view.empty.hidden).toBe(true);
    await controller.select('req-fixture-alpha');

    expect(client.getEvents).toHaveBeenCalledWith('req-fixture-alpha');
    expect(view.eventsSection.hidden).toBe(false);
    expect(view.firstToken.textContent).toBe('First-token latency: 40 ms');
    expect(view.events.textContent).toContain('request.started');
    expect(formatElapsed(40)).toBe('40 ms');
  });

  it('handles a missing or malformed requestId as unavailable', async () => {
    const doc = new FakeDocument();
    const view = {
      panel: doc.createElement('aside'),
      requests: doc.createElement('ul'),
      events: doc.createElement('ol'),
      eventsSection: doc.createElement('section'),
      empty: doc.createElement('p'),
      error: doc.createElement('p'),
      firstToken: doc.createElement('p'),
    };
    const client: InspectorClient = {
      listRequests: vi.fn().mockResolvedValue([request]),
      getEvents: vi.fn().mockRejectedValue(new ApiError(404, 'Request not found.')),
    };
    const controller = new InspectorController(client, view as unknown as InspectorView, vi.fn());

    await controller.reveal('not-a-valid-request-id', 'convo-alpha');

    expect(client.getEvents).toHaveBeenCalledWith('not-a-valid-request-id');
    expect(view.error.hidden).toBe(false);
    expect(view.error.textContent).toBe('That request is no longer available.');
  });

  it('renders a labelled inspect control that reports the request id', () => {
    const doc = new FakeDocument();
    const onInspect = vi.fn();
    const button = renderRequestLink(doc as unknown as Document, 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa', onInspect);
    expect(button.textContent).toBe('Inspect request');
    expect(button.getAttribute('aria-label')).toBe('Open inspector for request aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
    button.click();
    expect(onInspect).toHaveBeenCalledWith('aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
  });

  it('restores focus to the selected request after the list rebuilds', async () => {
    const doc = new FakeDocument();
    const view = {
      panel: doc.createElement('aside'),
      requests: doc.createElement('ul'),
      events: doc.createElement('ol'),
      eventsSection: doc.createElement('section'),
      empty: doc.createElement('p'),
      error: doc.createElement('p'),
      firstToken: doc.createElement('p'),
    };
    const client: InspectorClient = {
      listRequests: vi.fn().mockResolvedValue([request]),
      getEvents: vi.fn().mockResolvedValue(redactedEvents),
    };
    const controller = new InspectorController(client, view as unknown as InspectorView, vi.fn());

    await controller.load('convo-alpha');
    expect(doc.activeElement).toBeNull();
    await controller.select('req-fixture-alpha');

    expect(doc.activeElement?.className).toContain('inspector-request__button');
    expect(doc.activeElement?.textContent).toContain('req-fixture-alpha');
    expect(focusRequestItem(asElement(view.requests), 'req-fixture-alpha')).toBe(true);
  });
});
