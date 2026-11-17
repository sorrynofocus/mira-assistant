import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, asConversationSnapshot, CONVERSATION_LIMITS } from './api';
import {
  ConversationWorkspace,
  NEW_CHAT_LABEL,
  SELECTED_CONVERSATION_KEY,
  deleteConfirmMessage,
  deleteConversationWithAbortPolicy,
  displayTitle,
  focusConversationItem,
  nextConversationFocusId,
  normalizeTitle,
  renderConversationList,
  selectConversationWithAbortPolicy,
  shouldAbortActiveTurn,
  syncLoadMoreButton,
  type ConversationClient,
  type ConversationListHandlers,
} from './conversations';
import type { ConversationSnapshot, ConversationSummary } from './api';

const STAMP = '2026-11-16T04:00:00.000Z';

function summary(id: string, title: string | null): ConversationSummary {
  return { id, title, createdAt: STAMP, updatedAt: STAMP };
}

function snapshot(id: string, title: string | null): ConversationSnapshot {
  return { id, title, createdAt: STAMP, updatedAt: STAMP, messages: [] };
}

type FakeEvent = { type: string; key?: string; preventDefault(): void };

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
  value = '';
  maxLength = 0;
  type = '';
  name = '';
  autocomplete = '';
  htmlFor = '';
  dateTime = '';
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
    if (name === 'for') this.htmlFor = value;
    if (name === 'datetime') this.dateTime = value;
    if (name.startsWith('data-')) {
      const key = name.slice(5).replace(/-([a-z])/g, (_, letter: string) => letter.toUpperCase());
      this.dataset[key] = value;
    }
  }

  getAttribute(name: string): string | null {
    if (name === 'id') return this.id || this.attrs.id || null;
    if (name.startsWith('data-')) {
      const key = name.slice(5).replace(/-([a-z])/g, (_, letter: string) => letter.toUpperCase());
      return this.dataset[key] ?? this.attrs[name] ?? null;
    }
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
  if (selector.startsWith('#')) return node.id === selector.slice(1);
  if (selector.startsWith('[') && selector.endsWith(']')) {
    const body = selector.slice(1, -1);
    const eq = body.indexOf('=');
    if (eq < 0) return node.getAttribute(body) != null || (body.startsWith('data-') && node.dataset[attributeToDataset(body)] != null);
    const name = body.slice(0, eq);
    const value = body.slice(eq + 1).replace(/^"|"$/g, '');
    return node.getAttribute(name) === value;
  }
  return node.tagName === selector.toUpperCase();
}

function attributeToDataset(name: string): string {
  return name.replace(/^data-/, '').replace(/-([a-z])/g, (_, letter: string) => letter.toUpperCase());
}

function asElement(node: FakeNode): HTMLElement {
  return node as unknown as HTMLElement;
}

function memoryStorage(initial: Record<string, string> = {}): Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> {
  const data = new Map(Object.entries(initial));
  return {
    getItem: (key) => data.get(key) ?? null,
    setItem: (key, value) => {
      data.set(key, String(value));
    },
    removeItem: (key) => {
      data.delete(key);
    },
  };
}

function stubClient(overrides: Partial<ConversationClient> = {}): ConversationClient {
  return {
    list: vi.fn().mockResolvedValue([]),
    create: vi.fn(),
    get: vi.fn(),
    getCurrent: vi.fn(),
    rename: vi.fn(),
    delete: vi.fn(),
    ...overrides,
  };
}

function handlers(overrides: Partial<ConversationListHandlers> = {}): ConversationListHandlers {
  return {
    onSelect: vi.fn(),
    onBeginRename: vi.fn(),
    onSubmitRename: vi.fn(),
    onCancelRename: vi.fn(),
    onDelete: vi.fn(),
    ...overrides,
  };
}

function paint(conversations: ConversationSummary[], selectedId: string | null, listHandlers = handlers(), renamingId: string | null = null) {
  const list = new FakeDocument().createElement('ul');
  renderConversationList(asElement(list), conversations, selectedId, listHandlers, renamingId);
  return { list, handlers: listHandlers };
}

function buttonByLabel(root: FakeNode, label: string): FakeNode {
  const match = root.querySelectorAll('button').find((button) => button.textContent === label || button.getAttribute('aria-label') === label);
  if (!match) throw new Error(`No button labelled ${label}`);
  return match;
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe('displayTitle and normalizeTitle', () => {
  it('treats a blank title as untitled and caps titles at 200 characters', () => {
    expect(displayTitle(null)).toBe(NEW_CHAT_LABEL);
    expect(NEW_CHAT_LABEL).toBe('New chat');
    expect(displayTitle('   ')).toBe('New chat');
    expect(normalizeTitle('  notes-alpha  ')).toBe('notes-alpha');
    expect(normalizeTitle('')).toBeNull();
    expect(() => normalizeTitle('n'.repeat(201))).toThrow(/200/);
  });
});

describe('renderConversationList', () => {
  it('renders a title that looks like HTML as text and does not create an img element', () => {
    const unsafe = '<img src=x onerror=alert(1)>';
    const { list } = paint([summary('convo-alpha', unsafe)], 'convo-alpha');

    expect(list.querySelectorAll('img')).toHaveLength(0);
    expect(list.textContent).toContain(unsafe);
    expect(list.querySelectorAll('button')[0]?.textContent).toBe(unsafe);
  });

  it('marks the selected conversation and switches when another title is activated', () => {
    const listHandlers = handlers();
    const { list } = paint([summary('convo-alpha', 'notes-alpha'), summary('convo-beta', 'notes-beta')], 'convo-alpha', listHandlers);

    expect(buttonByLabel(list, 'notes-alpha').getAttribute('aria-current')).toBe('true');
    expect(buttonByLabel(list, 'notes-beta').getAttribute('aria-current')).toBeNull();

    buttonByLabel(list, 'notes-beta').click();
    expect(listHandlers.onSelect).toHaveBeenCalledWith('convo-beta');
  });
});

describe('ConversationWorkspace', () => {
  it('creates a new untitled conversation and selects it', async () => {
    const created = summary('convo-gamma', null);
    const client = stubClient({
      list: vi.fn().mockResolvedValue([summary('convo-alpha', 'notes-alpha')]),
      create: vi.fn().mockResolvedValue(created),
      get: vi.fn().mockResolvedValue(snapshot('convo-gamma', null)),
    });
    const storage = memoryStorage();
    const workspace = new ConversationWorkspace({
      client,
      confirmDelete: vi.fn(),
      storage,
      onChange: vi.fn(),
      onStatus: vi.fn(),
      onError: vi.fn(),
    });
    workspace.list = [summary('convo-alpha', 'notes-alpha')];

    await workspace.createNew();

    expect(client.create).toHaveBeenCalledWith();
    expect(workspace.selectedId).toBe('convo-gamma');
    expect(workspace.list[0]?.id).toBe('convo-gamma');
    expect(storage.getItem(SELECTED_CONVERSATION_KEY)).toBe('convo-gamma');
    expect(displayTitle(workspace.selected?.title)).toBe('New chat');
  });

  it('renames a conversation with a trimmed title', async () => {
    const client = stubClient({
      rename: vi.fn().mockResolvedValue(summary('convo-alpha', 'notes-renamed')),
    });
    const workspace = new ConversationWorkspace({
      client,
      confirmDelete: vi.fn(),
      storage: memoryStorage(),
      onChange: vi.fn(),
      onStatus: vi.fn(),
      onError: vi.fn(),
    });
    workspace.list = [summary('convo-alpha', 'notes-alpha')];
    workspace.selected = snapshot('convo-alpha', 'notes-alpha');

    await workspace.submitRename('convo-alpha', '  notes-renamed  ');

    expect(client.rename).toHaveBeenCalledWith('convo-alpha', 'notes-renamed');
    expect(workspace.list[0]?.title).toBe('notes-renamed');
    expect(workspace.selected?.title).toBe('notes-renamed');
    expect(workspace.renamingId).toBeNull();
  });

  it('deletes only after confirmation and leaves everything in place when the prompt is cancelled', async () => {
    const client = stubClient({
      delete: vi.fn().mockResolvedValue(undefined),
    });
    const confirmDelete = vi.fn().mockReturnValue(false);
    const workspace = new ConversationWorkspace({
      client,
      confirmDelete,
      storage: memoryStorage({ [SELECTED_CONVERSATION_KEY]: 'convo-alpha' }),
      onChange: vi.fn(),
      onStatus: vi.fn(),
      onError: vi.fn(),
    });
    workspace.list = [summary('convo-alpha', 'notes-alpha'), summary('convo-beta', 'notes-beta')];
    workspace.selected = snapshot('convo-alpha', 'notes-alpha');

    await expect(workspace.remove('convo-alpha')).resolves.toBe(false);
    expect(confirmDelete).toHaveBeenCalledWith(deleteConfirmMessage('notes-alpha'));
    expect(client.delete).not.toHaveBeenCalled();
    expect(workspace.list).toHaveLength(2);
    expect(workspace.selectedId).toBe('convo-alpha');

    confirmDelete.mockReturnValue(true);
    client.get = vi.fn().mockResolvedValue(snapshot('convo-beta', 'notes-beta'));
    await expect(workspace.remove('convo-alpha')).resolves.toBe(true);
    expect(client.delete).toHaveBeenCalledTimes(1);
    expect(client.delete).toHaveBeenCalledWith('convo-alpha');
    expect(workspace.list.map((item) => item.id)).toEqual(['convo-beta']);
    expect(workspace.selectedId).toBe('convo-beta');
  });

  it('restores the stored conversation on boot and refreshes the list when it is gone', async () => {
    const client = stubClient({
      list: vi.fn().mockResolvedValue([summary('convo-beta', 'notes-beta')]),
      get: vi
        .fn()
        .mockRejectedValueOnce(new ApiError(404, 'Not found'))
        .mockResolvedValueOnce(snapshot('convo-beta', 'notes-beta')),
      getCurrent: vi.fn().mockResolvedValue(snapshot('convo-beta', 'notes-beta')),
    });
    const storage = memoryStorage({ [SELECTED_CONVERSATION_KEY]: 'convo-missing' });
    const workspace = new ConversationWorkspace({
      client,
      confirmDelete: vi.fn(),
      storage,
      onChange: vi.fn(),
      onStatus: vi.fn(),
      onError: vi.fn(),
    });

    await workspace.boot();

    expect(client.get).toHaveBeenCalledWith('convo-missing');
    expect(client.getCurrent).toHaveBeenCalled();
    expect(workspace.selectedId).toBe('convo-beta');
    expect(storage.getItem(SELECTED_CONVERSATION_KEY)).toBe('convo-beta');
  });

  it('uses the title from the full current-conversation shape on a fallback reload', async () => {
    const client = stubClient({
      list: vi.fn().mockResolvedValue({ items: [], hasMore: false }),
      getCurrent: vi.fn().mockResolvedValue(
        asConversationSnapshot({
          id: 'convo-alpha',
          title: 'notes-current',
          createdAt: STAMP,
          updatedAt: STAMP,
          messages: [],
        }),
      ),
    });
    const workspace = new ConversationWorkspace({
      client,
      confirmDelete: vi.fn(),
      storage: memoryStorage(),
      onChange: vi.fn(),
      onStatus: vi.fn(),
      onError: vi.fn(),
    });

    await workspace.boot();

    expect(workspace.selected?.title).toBe('notes-current');
    expect(workspace.list[0]?.title).toBe('notes-current');
    expect(asConversationSnapshot({ id: 'convo-beta', messages: [] }).title).toBeNull();
  });

  it('loads the next conversation page when hasMore is true and hides Load more otherwise', async () => {
    const first = [summary('convo-alpha', 'notes-alpha'), summary('convo-beta', 'notes-beta')];
    const next = [summary('convo-gamma', 'notes-gamma')];
    const client = stubClient({
      list: vi
        .fn()
        .mockResolvedValueOnce({ items: first, skip: 0, take: 50, hasMore: true })
        .mockResolvedValueOnce({ items: next, skip: 2, take: 50, hasMore: false }),
      get: vi.fn().mockResolvedValue(snapshot('convo-alpha', 'notes-alpha')),
    });
    const onStatus = vi.fn();
    const workspace = new ConversationWorkspace({
      client,
      confirmDelete: vi.fn(),
      storage: memoryStorage({ [SELECTED_CONVERSATION_KEY]: 'convo-alpha' }),
      onChange: vi.fn(),
      onStatus,
      onError: vi.fn(),
    });
    const button = new FakeDocument().createElement('button');
    button.textContent = 'Load more';
    button.setAttribute('aria-label', 'Load more conversations');

    await workspace.boot();
    syncLoadMoreButton(asElement(button), workspace.hasMore);
    expect(workspace.list).toHaveLength(2);
    expect(workspace.hasMore).toBe(true);
    expect(button.hidden).toBe(false);
    expect(client.list).toHaveBeenCalledWith(0, CONVERSATION_LIMITS.defaultPageSize);

    await workspace.loadMore();
    expect(client.list).toHaveBeenCalledWith(2, CONVERSATION_LIMITS.defaultPageSize);
    expect(workspace.list.map((item) => item.id)).toEqual(['convo-alpha', 'convo-beta', 'convo-gamma']);
    expect(workspace.hasMore).toBe(false);
    expect(onStatus).toHaveBeenCalledWith('Loaded 1 more conversations. Showing all conversations.');
    syncLoadMoreButton(asElement(button), workspace.hasMore);
    expect(button.hidden).toBe(true);

    const calls = (client.list as ReturnType<typeof vi.fn>).mock.calls.length;
    await workspace.loadMore();
    expect((client.list as ReturnType<typeof vi.fn>).mock.calls).toHaveLength(calls);
  });

  it('submits an in-progress rename from the list form and can cancel it', () => {
    const listHandlers = handlers();
    const item = summary('convo-alpha', 'notes-alpha');
    const { list } = paint([item], 'convo-alpha', listHandlers, 'convo-alpha');
    const input = list.querySelector('input');
    const form = list.querySelector('form');
    if (!input || !form) throw new Error('Rename form was not rendered');

    input.value = 'notes-renamed';
    form.dispatch('submit');
    expect(listHandlers.onSubmitRename).toHaveBeenCalledWith('convo-alpha', 'notes-renamed');

    const cancelled = paint([item], 'convo-alpha', listHandlers, 'convo-alpha');
    buttonByLabel(cancelled.list, 'Cancel').click();
    expect(listHandlers.onCancelRename).toHaveBeenCalled();
  });
});

describe('active-turn abort policy', () => {
  it('does not stop the stream when a delete is cancelled', async () => {
    const abortTurn = vi.fn();
    const client = stubClient({ delete: vi.fn() });
    const workspace = new ConversationWorkspace({
      client,
      confirmDelete: vi.fn().mockReturnValue(false),
      storage: memoryStorage(),
      onChange: vi.fn(),
      onStatus: vi.fn(),
      onError: vi.fn(),
    });
    workspace.list = [summary('convo-alpha', 'notes-alpha')];
    workspace.selected = snapshot('convo-alpha', 'notes-alpha');

    await expect(
      deleteConversationWithAbortPolicy('convo-alpha', {
        selectedId: 'convo-alpha',
        abortTurn,
        remove: (id, hooks) => workspace.remove(id, hooks),
      }),
    ).resolves.toBe(false);
    expect(abortTurn).not.toHaveBeenCalled();
    expect(client.delete).not.toHaveBeenCalled();
  });

  it('does not stop the stream when loading more conversations', () => {
    expect(shouldAbortActiveTurn({ kind: 'loadMore', selectedId: 'convo-alpha' })).toBe(false);
  });

  it('does not stop the stream when re-selecting the open conversation', async () => {
    const abortTurn = vi.fn();
    const select = vi.fn();
    await selectConversationWithAbortPolicy('convo-alpha', {
      selectedId: 'convo-alpha',
      abortTurn,
      select,
    });
    expect(abortTurn).not.toHaveBeenCalled();
    expect(select).not.toHaveBeenCalled();
  });

  it('stops the stream after a confirmed delete of the active conversation', async () => {
    const abortTurn = vi.fn();
    const client = stubClient({
      delete: vi.fn().mockResolvedValue(undefined),
      get: vi.fn().mockResolvedValue(snapshot('convo-beta', 'notes-beta')),
    });
    const workspace = new ConversationWorkspace({
      client,
      confirmDelete: vi.fn().mockReturnValue(true),
      storage: memoryStorage(),
      onChange: vi.fn(),
      onStatus: vi.fn(),
      onError: vi.fn(),
    });
    workspace.list = [summary('convo-alpha', 'notes-alpha'), summary('convo-beta', 'notes-beta')];
    workspace.selected = snapshot('convo-alpha', 'notes-alpha');

    await expect(
      deleteConversationWithAbortPolicy('convo-alpha', {
        selectedId: 'convo-alpha',
        abortTurn,
        remove: (id, hooks) => workspace.remove(id, hooks),
      }),
    ).resolves.toBe(true);
    expect(abortTurn).toHaveBeenCalledTimes(1);
    expect(client.delete).toHaveBeenCalledWith('convo-alpha');
  });

  it('stops the stream when switching to a different conversation', async () => {
    const abortTurn = vi.fn();
    const select = vi.fn().mockResolvedValue(undefined);
    await selectConversationWithAbortPolicy('convo-beta', {
      selectedId: 'convo-alpha',
      abortTurn,
      select,
    });
    expect(abortTurn).toHaveBeenCalledTimes(1);
    expect(select).toHaveBeenCalledWith('convo-beta');
  });
});

describe('list focus restoration', () => {
  it('restores focus to the selected item after the list rebuilds', () => {
    const { list } = paint(
      [summary('convo-alpha', 'notes-alpha'), summary('convo-beta', 'notes-beta')],
      'convo-beta',
    );
    expect(focusConversationItem(asElement(list), 'convo-beta')).toBe(true);
    expect(list.ownerDocument.activeElement?.textContent).toBe('notes-beta');
    expect(list.ownerDocument.activeElement?.className).toContain('conversation-item__title');
  });

  it('restores focus to the renamed item after a rename', async () => {
    const list = new FakeDocument().createElement('ul');
    const workspace = new ConversationWorkspace({
      client: stubClient({ rename: vi.fn().mockResolvedValue(summary('convo-alpha', 'notes-renamed')) }),
      confirmDelete: vi.fn(),
      storage: memoryStorage(),
      onChange: (current) => {
        const intent = current.consumeFocusIntent();
        renderConversationList(asElement(list), current.list, current.selectedId, handlers(), current.renamingId);
        if (intent.type === 'conversation') focusConversationItem(asElement(list), intent.id);
      },
      onStatus: vi.fn(),
      onError: vi.fn(),
    });
    workspace.list = [summary('convo-alpha', 'notes-alpha'), summary('convo-beta', 'notes-beta')];
    workspace.selected = snapshot('convo-alpha', 'notes-alpha');

    await workspace.submitRename('convo-alpha', 'notes-renamed');
    expect(list.ownerDocument.activeElement?.textContent).toBe('notes-renamed');
    expect(list.ownerDocument.activeElement?.className).toContain('conversation-item__title');
  });

  it('moves focus to the next item after delete, or the previous item if it was last', () => {
    expect(nextConversationFocusId('convo-alpha', ['convo-alpha', 'convo-beta', 'convo-gamma'])).toBe('convo-beta');
    expect(nextConversationFocusId('convo-gamma', ['convo-alpha', 'convo-beta', 'convo-gamma'])).toBe('convo-beta');
    expect(nextConversationFocusId('convo-alpha', ['convo-alpha'])).toBeNull();

    const list = new FakeDocument().createElement('ul');
    const remaining = [summary('convo-beta', 'notes-beta'), summary('convo-gamma', 'notes-gamma')];
    renderConversationList(asElement(list), remaining, 'convo-beta', handlers());
    const nextId = nextConversationFocusId('convo-alpha', ['convo-alpha', 'convo-beta', 'convo-gamma']);
    expect(nextId).toBe('convo-beta');
    focusConversationItem(asElement(list), nextId!);
    expect(list.ownerDocument.activeElement?.textContent).toBe('notes-beta');
  });
});
