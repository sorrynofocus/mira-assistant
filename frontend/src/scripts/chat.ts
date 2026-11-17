import {
  ApiError,
  getJson,
  postJson,
  postStream,
  refreshCsrfToken,
  setCsrfToken,
  type ConversationMessage,
  type ConversationSnapshot,
  type MessageStatus,
  type ChatRole,
} from './api';
import {
  ConversationWorkspace,
  deleteConversationWithAbortPolicy,
  focusConversationItem,
  liveConversationClient,
  renderConversationList,
  selectConversationWithAbortPolicy,
  shouldAbortActiveTurn,
  syncLoadMoreButton,
} from './conversations';
import { InspectorController, liveInspectorClient, renderRequestLink } from './inspector';
import { signOut } from './session';
import { readSse, type ChatEnvelope } from './sse';

interface User {
  id: string;
  username: string;
}

const STATUS_LABELS: Partial<Record<MessageStatus, string>> = {
  streaming: 'Responding…',
  interrupted: 'Interrupted',
  failed: 'Failed',
};

function byId<T extends HTMLElement>(id: string): T {
  const element = document.getElementById(id);
  if (!element) throw new Error(`Missing #${id}`);
  return element as T;
}

const loginView = byId<HTMLElement>('login-view');
const loginForm = byId<HTMLFormElement>('login-form');
const usernameInput = byId<HTMLInputElement>('username');
const passwordInput = byId<HTMLInputElement>('password');
const loginButton = byId<HTMLButtonElement>('login-button');
const loginError = byId<HTMLElement>('login-error');

const chatView = byId<HTMLElement>('chat-view');
const whoami = byId<HTMLElement>('whoami');
const logoutButton = byId<HTMLButtonElement>('logout-button');
const workspaceBody = byId<HTMLElement>('workspace-body');
const conversationList = byId<HTMLUListElement>('conversation-list');
const conversationEmpty = byId<HTMLElement>('conversation-empty');
const newChatButton = byId<HTMLButtonElement>('new-chat-button');
const loadMoreButton = byId<HTMLButtonElement>('load-more-button');
const messageList = byId<HTMLOListElement>('messages');
const emptyState = byId<HTMLElement>('empty-state');
const composer = byId<HTMLFormElement>('composer');
const messageInput = byId<HTMLTextAreaElement>('message');
const sendButton = byId<HTMLButtonElement>('send-button');
const stopButton = byId<HTMLButtonElement>('stop-button');
const chatError = byId<HTMLElement>('chat-error');
const announcer = byId<HTMLElement>('announcer');
const inspectorToggle = byId<HTMLButtonElement>('inspector-toggle');
const inspectorClose = byId<HTMLButtonElement>('inspector-close');
const inspectorPanel = byId<HTMLElement>('inspector-panel');

let activeStream: AbortController | null = null;

const workspace = new ConversationWorkspace({
  client: liveConversationClient,
  confirmDelete: (message) => window.confirm(message),
  onChange: paintSidebar,
  onStatus: announce,
  onError: (message) => showError(chatError, message),
});

const inspector = new InspectorController(
  liveInspectorClient,
  {
    panel: inspectorPanel,
    requests: byId('inspector-requests'),
    events: byId('inspector-events'),
    eventsSection: byId('inspector-events-section'),
    empty: byId('inspector-empty'),
    error: byId('inspector-error'),
    firstToken: byId('inspector-first-token'),
  },
  announce,
);

function showError(target: HTMLElement, message: string | null): void {
  target.textContent = message ?? '';
  target.hidden = !message;
}

function announce(message: string): void {
  announcer.textContent = message;
}

function showLogin(message: string | null = null): void {
  stop();
  setInspectorOpen(false);
  inspector.reset();
  chatView.hidden = true;
  loginView.hidden = false;
  visibleConversationId = null;
  messageList.replaceChildren();
  conversationList.replaceChildren();
  showError(loginError, message);
  showError(chatError, null);
  passwordInput.value = '';
  (usernameInput.value ? passwordInput : usernameInput).focus();
}

async function showChat(user: User): Promise<void> {
  loginView.hidden = true;
  chatView.hidden = false;
  whoami.textContent = user.username;
  showError(chatError, null);
  try {
    await workspace.boot();
  } catch (error) {
    handleWorkspaceError(error);
  }
  if (!chatView.hidden) messageInput.focus();
}

let visibleConversationId: string | null = null;

function paintSidebar(current: ConversationWorkspace): void {
  const intent = current.consumeFocusIntent();
  renderConversationList(conversationList, current.list, current.selectedId, {
    onSelect: (id) => void runWorkspaceAction(() =>
      selectConversationWithAbortPolicy(id, {
        selectedId: workspace.selectedId,
        abortTurn: stop,
        select: (target) => workspace.select(target),
      }),
    ),
    onBeginRename: (id) => current.beginRename(id),
    onSubmitRename: (id, title) => void current.submitRename(id, title),
    onCancelRename: () => current.cancelRename(),
    onDelete: (id) => void runWorkspaceAction(() =>
      deleteConversationWithAbortPolicy(id, {
        selectedId: workspace.selectedId,
        abortTurn: stop,
        remove: (target, hooks) => workspace.remove(target, hooks),
      }),
    ),
  }, current.renamingId);
  conversationEmpty.hidden = current.list.length > 0;
  syncLoadMoreButton(loadMoreButton, current.hasMore);
  if (intent.type === 'conversation') focusConversationItem(conversationList, intent.id);
  else if (intent.type === 'new-chat') newChatButton.focus();
  if (current.selectedId !== visibleConversationId) {
    visibleConversationId = current.selectedId;
    loadMessages(current.selected);
  }
  void refreshInspector();
}

function loadMessages(selected: ConversationSnapshot | null): void {
  if (!selected) {
    messageList.replaceChildren();
    updateEmptyState();
    return;
  }
  messageList.replaceChildren(...selected.messages.map(renderMessage));
  updateEmptyState();
  scrollToEnd(true);
}

// Model text is untrusted: it is only ever assigned via textContent, never parsed as HTML.
function renderMessage(message: Pick<ConversationMessage, 'role' | 'content' | 'status'> & { requestId?: string | null }): HTMLLIElement {
  const item = document.createElement('li');
  item.className = `message message--${message.role}`;

  const author = document.createElement('span');
  author.className = 'message__author';
  author.textContent = message.role === 'user' ? 'You' : 'MIRA';

  const body = document.createElement('div');
  body.className = 'message__body';
  body.textContent = message.content;

  const status = document.createElement('span');
  status.className = 'message__status';

  item.append(author, body, status);
  setStatus(item, message.status);
  if (message.requestId) attachInspect(item, message.requestId);
  return item;
}

function attachInspect(item: HTMLLIElement, requestId: string): void {
  if (item.dataset.requestId) return;
  item.dataset.requestId = requestId;
  item.append(renderRequestLink(document, requestId, inspectRequest));
}

function inspectRequest(requestId: string): void {
  setInspectorOpen(true);
  void inspector.reveal(requestId, workspace.selectedId).catch((error) => {
    handleWorkspaceError(error);
  });
}

function setStatus(item: HTMLLIElement, status: MessageStatus): void {
  item.dataset.status = status;
  item.setAttribute('aria-busy', String(status === 'streaming'));
  const label = item.querySelector<HTMLElement>('.message__status');
  if (label) {
    label.textContent = STATUS_LABELS[status] ?? '';
    label.hidden = !STATUS_LABELS[status];
  }
}

function appendText(item: HTMLLIElement, text: string): void {
  const body = item.querySelector<HTMLElement>('.message__body');
  if (body) body.textContent += text;
}

function updateEmptyState(): void {
  emptyState.hidden = messageList.children.length > 0;
}

function scrollToEnd(force = false): void {
  const nearBottom = messageList.scrollHeight - messageList.scrollTop - messageList.clientHeight < 80;
  if (force || nearBottom) messageList.scrollTop = messageList.scrollHeight;
}

function setStreaming(streaming: boolean): void {
  sendButton.disabled = streaming;
  stopButton.hidden = !streaming;
  stopButton.disabled = !streaming;
  newChatButton.disabled = streaming;
  composer.setAttribute('aria-busy', String(streaming));
}

function setInspectorOpen(open: boolean): void {
  inspector.setOpen(open);
  workspaceBody.classList.toggle('is-inspector-open', open);
  inspectorToggle.setAttribute('aria-expanded', String(open));
  inspectorToggle.textContent = open ? 'Hide inspector' : 'Show inspector';
  if (open) void refreshInspector();
}

async function refreshInspector(): Promise<void> {
  if (!inspector.open) return;
  await inspector.load(workspace.selectedId);
}

async function runWorkspaceAction(action: () => Promise<unknown>): Promise<void> {
  try {
    await action();
  } catch (error) {
    handleWorkspaceError(error);
  }
}

function handleWorkspaceError(error: unknown): void {
  if (error instanceof ApiError && error.status === 401) {
    showLogin('Your session ended. Sign in again.');
    return;
  }
  showError(chatError, error instanceof ApiError ? error.message : 'Could not update conversations.');
}

async function send(): Promise<void> {
  const text = messageInput.value;
  const conversationId = workspace.selectedId;
  if (activeStream || text.trim().length === 0) return;
  if (!conversationId) {
    showError(chatError, 'Start a new chat before sending a message.');
    return;
  }

  showError(chatError, null);
  const userItem = renderMessage({ role: 'user' satisfies ChatRole, content: text, status: 'completed' });
  const assistantItem = renderMessage({ role: 'assistant', content: '', status: 'streaming' });
  messageList.append(userItem, assistantItem);
  updateEmptyState();
  scrollToEnd(true);

  const controller = new AbortController();
  activeStream = controller;
  setStreaming(true);
  announcer.textContent = 'MIRA is responding.';

  let finalStatus: MessageStatus = 'interrupted';
  let started = false;
  try {
    const response = await postStream('/api/chat/stream', { message: text, conversationId }, controller.signal);
    started = true;
    messageInput.value = '';

    for await (const event of readSse(response.body!)) {
      const envelope = JSON.parse(event.data) as ChatEnvelope<{ text?: string; status?: MessageStatus; message?: string }>;
      if (envelope.requestId) {
        attachInspect(userItem, envelope.requestId);
        attachInspect(assistantItem, envelope.requestId);
      }
      switch (event.event) {
        case 'response.delta':
          appendText(assistantItem, envelope.data.text ?? '');
          scrollToEnd();
          break;
        case 'response.completed':
          finalStatus = 'completed';
          break;
        case 'response.cancelled':
          finalStatus = 'interrupted';
          break;
        case 'response.error':
          finalStatus = envelope.data.status ?? 'failed';
          showError(chatError, envelope.data.message ?? 'The response failed.');
          break;
      }
    }
  } catch (error) {
    if (controller.signal.aborted) {
      finalStatus = 'interrupted';
    } else if (error instanceof ApiError && !started) {
      userItem.remove();
      assistantItem.remove();
      updateEmptyState();
      if (error.status === 401) {
        showLogin('Your session ended. Sign in again.');
        return;
      }
      if (error.status === 404) {
        showError(chatError, 'That conversation is no longer available.');
        announce('That conversation is no longer available.');
        await workspace.refreshList().catch(() => undefined);
        if (workspace.selectedId === conversationId) {
          try {
            await workspace.boot();
          } catch (refreshError) {
            handleWorkspaceError(refreshError);
          }
        }
        return;
      }
      showError(chatError, error.message);
    } else {
      finalStatus = 'failed';
      showError(chatError, 'The connection to MIRA was lost.');
    }
  } finally {
    if (assistantItem.isConnected) setStatus(assistantItem, finalStatus);
    if (activeStream === controller) {
      activeStream = null;
      setStreaming(false);
    }
    if (assistantItem.isConnected) {
      announcer.textContent =
        finalStatus === 'completed' ? 'Response complete.' : `Response ${STATUS_LABELS[finalStatus]?.toLowerCase() ?? finalStatus}.`;
      void refreshInspector();
    }
    if (!chatView.hidden) messageInput.focus();
  }
}

function stop(): void {
  activeStream?.abort();
}

loginForm.addEventListener('submit', async (event) => {
  event.preventDefault();
  loginButton.disabled = true;
  showError(loginError, null);
  try {
    const result = await postJson<{ user: User; csrfToken: string }>('/api/auth/login', {
      username: usernameInput.value,
      password: passwordInput.value,
    });
    passwordInput.value = '';
    setCsrfToken(result.csrfToken);
    await showChat(result.user);
  } catch (error) {
    passwordInput.value = '';
    showError(loginError, error instanceof ApiError ? error.message : 'MIRA is unreachable. Is the API running?');
    passwordInput.focus();
    if (error instanceof ApiError && error.status === 403) await refreshCsrfToken().catch(() => undefined);
  } finally {
    loginButton.disabled = false;
  }
});

logoutButton.addEventListener('click', async () => {
  stop();
  logoutButton.disabled = true;
  showError(chatError, null);
  try {
    const result = await signOut();
    if (result.signedOut) {
      showLogin();
    } else {
      showError(chatError, result.message);
      announcer.textContent = result.message;
    }
  } finally {
    logoutButton.disabled = false;
  }
});

newChatButton.addEventListener('click', () => {
  void runWorkspaceAction(async () => {
    if (shouldAbortActiveTurn({ kind: 'createNew', selectedId: workspace.selectedId })) stop();
    await workspace.createNew();
  });
});

loadMoreButton.addEventListener('click', () => {
  loadMoreButton.disabled = true;
  void runWorkspaceAction(() => workspace.loadMore()).finally(() => {
    loadMoreButton.disabled = false;
    syncLoadMoreButton(loadMoreButton, workspace.hasMore);
  });
});

inspectorToggle.addEventListener('click', () => {
  setInspectorOpen(!inspector.open);
});

inspectorClose.addEventListener('click', () => {
  setInspectorOpen(false);
  inspectorToggle.focus();
});

composer.addEventListener('submit', (event) => {
  event.preventDefault();
  void send();
});

messageInput.addEventListener('keydown', (event) => {
  if (event.key === 'Enter' && !event.shiftKey && !event.isComposing) {
    event.preventDefault();
    void send();
  } else if (event.key === 'Escape' && activeStream) {
    event.preventDefault();
    stop();
  }
});

stopButton.addEventListener('click', stop);

async function start(): Promise<void> {
  try {
    await refreshCsrfToken();
  } catch {
    showLogin('MIRA is unreachable. Start the API, then reload this page.');
    return;
  }

  try {
    await showChat(await getJson<User>('/api/auth/me'));
  } catch (error) {
    showLogin(error instanceof ApiError && error.status === 401 ? null : 'Could not load your session.');
  }
}

void start();
