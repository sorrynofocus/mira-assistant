import {
  ApiError,
  CONVERSATION_LIMITS,
  asConversationPage,
  asConversationSnapshot,
  asConversationSummary,
  deleteRequest,
  getJson,
  patchJson,
  postJson,
  type ConversationPage,
  type ConversationSnapshot,
  type ConversationSummary,
} from './api';

export const SELECTED_CONVERSATION_KEY = 'mira.selectedConversationId';

export const NEW_CHAT_LABEL = 'New chat';

export const DELETE_CONFIRM_SUFFIX = 'permanently? This cannot be undone.';

export function displayTitle(title: string | null | undefined): string {
  const trimmed = title?.trim();
  return trimmed ? trimmed : NEW_CHAT_LABEL;
}

export function normalizeTitle(title: string | null | undefined): string | null {
  if (title == null || title.trim() === '') return null;
  const trimmed = title.trim();
  if (trimmed.length > CONVERSATION_LIMITS.maxTitleLength) {
    throw new Error(`Titles are limited to ${CONVERSATION_LIMITS.maxTitleLength} characters.`);
  }
  return trimmed;
}

export function deleteConfirmMessage(title: string | null | undefined): string {
  return `Delete "${displayTitle(title)}" ${DELETE_CONFIRM_SUFFIX}`;
}

export function readStoredConversationId(storage: Pick<Storage, 'getItem'> | null = defaultStorage()): string | null {
  if (!storage) return null;
  try {
    const value = storage.getItem(SELECTED_CONVERSATION_KEY);
    return value && value.trim() ? value : null;
  } catch {
    return null;
  }
}

export function storeConversationId(
  id: string | null,
  storage: Pick<Storage, 'setItem' | 'removeItem'> | null = defaultStorage(),
): void {
  if (!storage) return;
  try {
    if (id) storage.setItem(SELECTED_CONVERSATION_KEY, id);
    else storage.removeItem(SELECTED_CONVERSATION_KEY);
  } catch {
    // Storage may be unavailable (private mode); selection still works for this page load.
  }
}

function defaultStorage(): Storage | null {
  try {
    return globalThis.localStorage;
  } catch {
    return null;
  }
}

export async function listConversations(
  skip = 0,
  take: number = CONVERSATION_LIMITS.defaultPageSize,
): Promise<ConversationPage> {
  const path = `/api/conversations?skip=${encodeURIComponent(String(skip))}&take=${encodeURIComponent(String(take))}`;
  return asConversationPage(await getJson<unknown>(path));
}

export async function createConversation(): Promise<ConversationSummary> {
  return asConversationSummary(await postJson<unknown>('/api/conversations'));
}

export async function getConversation(id: string): Promise<ConversationSnapshot> {
  return asConversationSnapshot(await getJson<unknown>(`/api/conversations/${encodeURIComponent(id)}`));
}

export async function getCurrentConversation(): Promise<ConversationSnapshot> {
  return asConversationSnapshot(await getJson<unknown>('/api/conversations/current'));
}

export async function renameConversation(id: string, title: string | null): Promise<ConversationSummary | undefined> {
  const body = await patchJson<unknown>(`/api/conversations/${encodeURIComponent(id)}`, { title });
  return body === undefined ? undefined : asConversationSummary(body);
}

export async function deleteConversation(id: string): Promise<void> {
  await deleteRequest(`/api/conversations/${encodeURIComponent(id)}`);
}

export interface ConversationClient {
  list(skip?: number, take?: number): Promise<ConversationPage | ConversationSummary[]>;
  create(): Promise<ConversationSummary>;
  get(id: string): Promise<ConversationSnapshot>;
  getCurrent(): Promise<ConversationSnapshot>;
  rename(id: string, title: string | null): Promise<ConversationSummary | undefined>;
  delete(id: string): Promise<void>;
}

export const liveConversationClient: ConversationClient = {
  list: (skip, take) => listConversations(skip ?? 0, take ?? CONVERSATION_LIMITS.defaultPageSize),
  create: createConversation,
  get: getConversation,
  getCurrent: getCurrentConversation,
  rename: renameConversation,
  delete: deleteConversation,
};

export interface ConversationListHandlers {
  onSelect(id: string): void;
  onBeginRename(id: string): void;
  onSubmitRename(id: string, title: string): void;
  onCancelRename(): void;
  onDelete(id: string): void;
}

export function syncLoadMoreButton(button: HTMLElement, hasMore: boolean): void {
  button.hidden = !hasMore;
}

export type ActiveTurnAction = {
  kind: 'select' | 'delete' | 'loadMore' | 'createNew';
  selectedId: string | null;
  targetId?: string;
  confirmed?: boolean;
};

// Only a real switch, a confirmed delete of the streaming conversation, or New chat should abort.
export function shouldAbortActiveTurn(event: ActiveTurnAction): boolean {
  switch (event.kind) {
    case 'loadMore':
      return false;
    case 'select':
      return event.targetId != null && event.targetId !== event.selectedId;
    case 'delete':
      return event.confirmed === true && event.targetId === event.selectedId;
    case 'createNew':
      return true;
  }
}

export async function selectConversationWithAbortPolicy(
  id: string,
  deps: { selectedId: string | null; abortTurn: () => void; select: (id: string) => Promise<void> },
): Promise<void> {
  if (!shouldAbortActiveTurn({ kind: 'select', selectedId: deps.selectedId, targetId: id })) return;
  deps.abortTurn();
  await deps.select(id);
}

export async function deleteConversationWithAbortPolicy(
  id: string,
  deps: {
    selectedId: string | null;
    abortTurn: () => void;
    remove: (id: string, hooks?: { onConfirmed?: () => void }) => Promise<boolean>;
  },
): Promise<boolean> {
  return deps.remove(id, {
    onConfirmed: () => {
      if (shouldAbortActiveTurn({ kind: 'delete', selectedId: deps.selectedId, targetId: id, confirmed: true })) {
        deps.abortTurn();
      }
    },
  });
}

export type FocusIntent = { type: 'conversation'; id: string } | { type: 'new-chat' } | { type: 'none' };

export function nextConversationFocusId(deletedId: string, ids: readonly string[]): string | null {
  const index = ids.indexOf(deletedId);
  if (index < 0) return ids[0] ?? null;
  return ids[index + 1] ?? ids[index - 1] ?? null;
}

export function focusConversationItem(list: HTMLElement, id: string): boolean {
  const items = list.querySelectorAll('li');
  for (let i = 0; i < items.length; i++) {
    const item = items[i] as HTMLElement;
    if (item.dataset.id !== id) continue;
    const target = (item.querySelector('input') ?? item.querySelector('.conversation-item__title')) as HTMLElement | null;
    target?.focus();
    return Boolean(target);
  }
  return false;
}

// Server-provided titles are untrusted: they are assigned with textContent only, never innerHTML.
export function renderConversationList(
  list: HTMLElement,
  conversations: ConversationSummary[],
  selectedId: string | null,
  handlers: ConversationListHandlers,
  renamingId: string | null = null,
): void {
  const doc = list.ownerDocument;
  list.replaceChildren();

  for (const conversation of conversations) {
    const item = doc.createElement('li');
    item.className = 'conversation-item' + (conversation.id === selectedId ? ' is-selected' : '');
    item.dataset.id = conversation.id;

    if (renamingId === conversation.id) {
      item.append(renderRenameForm(doc, conversation, handlers));
    } else {
      const open = doc.createElement('button');
      open.type = 'button';
      open.className = 'conversation-item__title';
      open.textContent = displayTitle(conversation.title);
      if (conversation.id === selectedId) open.setAttribute('aria-current', 'true');
      open.addEventListener('click', () => handlers.onSelect(conversation.id));
      item.append(open);

      const actions = doc.createElement('div');
      actions.className = 'conversation-item__actions';

      const rename = doc.createElement('button');
      rename.type = 'button';
      rename.className = 'link-button';
      rename.textContent = 'Rename';
      rename.setAttribute('aria-label', `Rename ${displayTitle(conversation.title)}`);
      rename.addEventListener('click', () => handlers.onBeginRename(conversation.id));

      const remove = doc.createElement('button');
      remove.type = 'button';
      remove.className = 'link-button';
      remove.textContent = 'Delete';
      remove.setAttribute('aria-label', `Delete ${displayTitle(conversation.title)}`);
      remove.addEventListener('click', () => handlers.onDelete(conversation.id));

      actions.append(rename, remove);
      item.append(actions);
    }

    list.append(item);
  }
}

function renderRenameForm(
  doc: Document,
  conversation: ConversationSummary,
  handlers: ConversationListHandlers,
): HTMLElement {
  const form = doc.createElement('form');
  form.className = 'conversation-item__rename';

  const label = doc.createElement('label');
  label.className = 'visually-hidden';
  const inputId = `conversation-rename-${conversation.id}`;
  label.htmlFor = inputId;
  label.textContent = 'Conversation title';

  const input = doc.createElement('input');
  input.id = inputId;
  input.name = 'title';
  input.type = 'text';
  input.value = conversation.title ?? '';
  input.maxLength = CONVERSATION_LIMITS.maxTitleLength;
  input.autocomplete = 'off';
  input.setAttribute('aria-label', 'Conversation title');

  const save = doc.createElement('button');
  save.type = 'submit';
  save.textContent = 'Save';

  const cancel = doc.createElement('button');
  cancel.type = 'button';
  cancel.className = 'secondary';
  cancel.textContent = 'Cancel';
  cancel.addEventListener('click', () => handlers.onCancelRename());

  form.addEventListener('submit', (event) => {
    event.preventDefault();
    handlers.onSubmitRename(conversation.id, input.value);
  });
  input.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
      event.preventDefault();
      handlers.onCancelRename();
    }
  });

  form.append(label, input, save, cancel);
  queueMicrotask(() => input.focus());
  return form;
}

export interface ConversationWorkspaceDeps {
  client: ConversationClient;
  confirmDelete: (message: string) => boolean;
  storage?: Pick<Storage, 'getItem' | 'setItem' | 'removeItem'> | null;
  onChange: (workspace: ConversationWorkspace) => void;
  onStatus: (message: string) => void;
  onError: (message: string | null) => void;
}

export class ConversationWorkspace {
  list: ConversationSummary[] = [];
  selected: ConversationSnapshot | null = null;
  renamingId: string | null = null;
  hasMore = false;
  focusIntent: FocusIntent = { type: 'none' };
  #fetched = 0;

  constructor(private readonly deps: ConversationWorkspaceDeps) {}

  get selectedId(): string | null {
    return this.selected?.id ?? null;
  }

  consumeFocusIntent(): FocusIntent {
    const intent = this.focusIntent;
    this.focusIntent = { type: 'none' };
    return intent;
  }

  async boot(): Promise<void> {
    this.deps.onError(null);
    const page = await this.#loadPage(0);
    this.#rememberPage(page, 'replace');
    const stored = readStoredConversationId(this.deps.storage ?? defaultStorage());
    if (stored) {
      try {
        await this.#open(await this.deps.client.get(stored), page.items);
        return;
      } catch (error) {
        if (!(error instanceof ApiError && error.status === 404)) throw error;
        this.deps.onStatus('The previous conversation is no longer available.');
      }
    }
    await this.#open(await this.deps.client.getCurrent(), page.items, { refreshList: true });
  }

  async select(id: string): Promise<void> {
    if (id === this.selectedId && this.renamingId === null) return;
    this.deps.onError(null);
    this.renamingId = null;
    try {
      this.focusIntent = { type: 'conversation', id };
      await this.#open(await this.deps.client.get(id), this.list);
      this.deps.onStatus(`Opened ${displayTitle(this.selected?.title)}.`);
    } catch (error) {
      await this.#handleMissing(error);
    }
  }

  async createNew(): Promise<void> {
    this.deps.onError(null);
    this.renamingId = null;
    const created = await this.deps.client.create();
    const snapshot = await this.#loadCreated(created);
    this.focusIntent = { type: 'conversation', id: snapshot.id };
    await this.#open(snapshot, this.list, { prepend: created });
    this.deps.onStatus('Started a new chat.');
  }

  beginRename(id: string): void {
    this.renamingId = id;
    this.focusIntent = { type: 'none' };
    this.#emit();
  }

  cancelRename(): void {
    const id = this.renamingId;
    this.renamingId = null;
    this.focusIntent = id ? { type: 'conversation', id } : { type: 'none' };
    this.#emit();
  }

  async submitRename(id: string, rawTitle: string): Promise<void> {
    this.deps.onError(null);
    let title: string | null;
    try {
      title = normalizeTitle(rawTitle);
    } catch (error) {
      this.deps.onError(error instanceof Error ? error.message : 'That title is not allowed.');
      return;
    }

    try {
      const updated = await this.deps.client.rename(id, title);
      const nextTitle = updated?.title ?? title;
      this.list = this.list.map((item) =>
        item.id === id
          ? { ...item, title: nextTitle, updatedAt: updated?.updatedAt ?? item.updatedAt }
          : item,
      );
      if (this.selected?.id === id) {
        this.selected = { ...this.selected, title: nextTitle, updatedAt: updated?.updatedAt ?? this.selected.updatedAt };
      }
      this.renamingId = null;
      this.focusIntent = { type: 'conversation', id };
      this.deps.onStatus(`Renamed to ${displayTitle(nextTitle)}.`);
      this.#emit();
    } catch (error) {
      await this.#handleMissing(error, 'Could not rename that conversation.');
    }
  }

  async remove(id: string, hooks?: { onConfirmed?: () => void }): Promise<boolean> {
    const current = this.list.find((item) => item.id === id);
    const title = current?.title ?? (this.selected?.id === id ? this.selected.title : null);
    if (!this.deps.confirmDelete(deleteConfirmMessage(title))) return false;
    hooks?.onConfirmed?.();

    this.deps.onError(null);
    this.renamingId = null;
    const ids = this.list.map((item) => item.id);
    const nextId = nextConversationFocusId(id, ids);
    try {
      await this.deps.client.delete(id);
    } catch (error) {
      await this.#handleMissing(error, 'Could not delete that conversation.');
      return false;
    }

    const remaining = this.list.filter((item) => item.id !== id);
    this.list = remaining;
    this.#fetched = Math.max(0, this.#fetched - 1);
    this.deps.onStatus(`Deleted ${displayTitle(title)}.`);

    if (this.selectedId === id) {
      if (nextId) {
        this.focusIntent = { type: 'conversation', id: nextId };
        try {
          await this.#open(await this.deps.client.get(nextId), remaining);
        } catch (error) {
          await this.#handleMissing(error);
        }
      } else {
        try {
          const snapshot = await this.deps.client.getCurrent();
          this.focusIntent = { type: 'conversation', id: snapshot.id };
          await this.#open(snapshot, remaining, { refreshList: true });
        } catch (error) {
          this.selected = null;
          storeConversationId(null, this.deps.storage ?? defaultStorage());
          this.focusIntent = { type: 'new-chat' };
          this.#emit();
          if (!(error instanceof ApiError && error.status === 404)) throw error;
        }
      }
    } else {
      this.focusIntent = nextId ? { type: 'conversation', id: nextId } : { type: 'new-chat' };
      this.#emit();
    }
    return true;
  }

  async refreshList(): Promise<void> {
    const page = await this.#loadPage(0);
    this.#rememberPage(page, 'replace');
    this.list = this.#ensureInList(page.items, this.selected);
    this.#emit();
  }

  async loadMore(): Promise<void> {
    if (!this.hasMore) return;
    this.deps.onError(null);
    const page = await this.#loadPage(this.#fetched);
    const seen = new Set(this.list.map((item) => item.id));
    const extra = page.items.filter((item) => !seen.has(item.id));
    this.#rememberPage(page, 'append');
    this.list = this.#ensureInList([...this.list, ...extra], this.selected);
    const count = extra.length;
    this.deps.onStatus(
      this.hasMore
        ? `Loaded ${count} more conversations.`
        : count === 0
          ? 'Showing all conversations.'
          : `Loaded ${count} more conversations. Showing all conversations.`,
    );
    this.#emit();
  }

  #emit(): void {
    this.deps.onChange(this);
  }

  async #open(
    snapshot: ConversationSnapshot,
    list: ConversationSummary[],
    options: { refreshList?: boolean; prepend?: ConversationSummary } = {},
  ): Promise<void> {
    this.selected = snapshot;
    storeConversationId(snapshot.id, this.deps.storage ?? defaultStorage());
    let next = options.prepend ? this.#merge(list, options.prepend) : list;
    if (options.refreshList) {
      try {
        const page = await this.#loadPage(0);
        this.#rememberPage(page, 'replace');
        next = page.items;
      } catch {
        // Keep the list we already have; the opened conversation is still shown.
      }
    }
    this.list = this.#ensureInList(next, snapshot);
    this.renamingId = null;
    this.#emit();
  }

  async #loadCreated(created: ConversationSummary): Promise<ConversationSnapshot> {
    try {
      return await this.deps.client.get(created.id);
    } catch (error) {
      if (error instanceof ApiError && error.status === 404) throw error;
      return {
        id: created.id,
        title: created.title,
        createdAt: created.createdAt,
        updatedAt: created.updatedAt,
        messages: [],
      };
    }
  }

  async #handleMissing(error: unknown, fallback = 'That conversation is no longer available.'): Promise<void> {
    if (error instanceof ApiError && error.status === 404) {
      this.deps.onError(fallback);
      this.deps.onStatus(fallback);
      this.renamingId = null;
      await this.refreshList();
      if (this.selectedId && !this.list.some((item) => item.id === this.selectedId)) {
        this.selected = null;
        storeConversationId(null, this.deps.storage ?? defaultStorage());
        if (this.list[0]) {
          try {
            await this.#open(await this.deps.client.get(this.list[0].id), this.list);
            return;
          } catch {
            this.selected = null;
          }
        }
        this.#emit();
      }
      return;
    }
    throw error;
  }

  async #loadPage(skip: number): Promise<ConversationPage> {
    return asConversationPage(await this.deps.client.list(skip, CONVERSATION_LIMITS.defaultPageSize));
  }

  #rememberPage(page: ConversationPage, mode: 'replace' | 'append'): void {
    if (mode === 'replace') this.#fetched = page.items.length;
    else this.#fetched += page.items.length;
    this.hasMore = page.hasMore;
  }

  #ensureInList(list: ConversationSummary[], snapshot: ConversationSnapshot | null): ConversationSummary[] {
    if (!snapshot) return list;
    if (list.some((item) => item.id === snapshot.id)) {
      return list.map((item) =>
        item.id === snapshot.id
          ? {
              ...item,
              title: snapshot.title ?? item.title,
              createdAt: snapshot.createdAt || item.createdAt,
              updatedAt: snapshot.updatedAt || item.updatedAt,
            }
          : item,
      );
    }
    return [
      {
        id: snapshot.id,
        title: snapshot.title,
        createdAt: snapshot.createdAt,
        updatedAt: snapshot.updatedAt,
      },
      ...list,
    ];
  }

  #merge(list: ConversationSummary[], created: ConversationSummary): ConversationSummary[] {
    return [created, ...list.filter((item) => item.id !== created.id)];
  }
}
