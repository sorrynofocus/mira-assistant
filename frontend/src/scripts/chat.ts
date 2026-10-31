import { ApiError, getJson, postJson, postStream, refreshCsrfToken, setCsrfToken } from './api';
import { signOut } from './session';
import { readSse, type ChatEnvelope } from './sse';

type MessageStatus = 'streaming' | 'completed' | 'interrupted' | 'failed';

interface User {
  id: string;
  username: string;
}

interface Message {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  status: MessageStatus;
}

interface Conversation {
  id: string;
  messages: Message[];
}

const STATUS_LABELS: Partial<Record<MessageStatus, string>> = {
  streaming: 'Responding…',
  interrupted: 'Stopped',
  failed: 'Error',
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
const messageList = byId<HTMLOListElement>('messages');
const emptyState = byId<HTMLElement>('empty-state');
const composer = byId<HTMLFormElement>('composer');
const messageInput = byId<HTMLTextAreaElement>('message');
const sendButton = byId<HTMLButtonElement>('send-button');
const stopButton = byId<HTMLButtonElement>('stop-button');
const chatError = byId<HTMLElement>('chat-error');
const announcer = byId<HTMLElement>('announcer');

let activeStream: AbortController | null = null;

function showError(target: HTMLElement, message: string | null): void {
  target.textContent = message ?? '';
  target.hidden = !message;
}

function showLogin(message: string | null = null): void {
  chatView.hidden = true;
  loginView.hidden = false;
  messageList.replaceChildren();
  showError(loginError, message);
  passwordInput.value = '';
  (usernameInput.value ? passwordInput : usernameInput).focus();
}

async function showChat(user: User): Promise<void> {
  loginView.hidden = true;
  chatView.hidden = false;
  whoami.textContent = user.username;
  showError(chatError, null);
  const conversation = await getJson<Conversation>('/api/conversations/current');
  messageList.replaceChildren(...conversation.messages.map(renderMessage));
  updateEmptyState();
  scrollToEnd(true);
  messageInput.focus();
}

// Model text is untrusted: it is only ever assigned via textContent, never parsed as HTML.
function renderMessage(message: Pick<Message, 'role' | 'content' | 'status'>): HTMLLIElement {
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
  return item;
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
  composer.setAttribute('aria-busy', String(streaming));
}

async function send(): Promise<void> {
  const text = messageInput.value;
  if (activeStream || text.trim().length === 0) return;

  showError(chatError, null);
  const userItem = renderMessage({ role: 'user', content: text, status: 'completed' });
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
    const response = await postStream('/api/chat/stream', { message: text }, controller.signal);
    started = true;
    messageInput.value = '';

    for await (const event of readSse(response.body!)) {
      const envelope = JSON.parse(event.data) as ChatEnvelope<{ text?: string; status?: MessageStatus; message?: string }>;
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
      // Nothing was saved for a refused request: remove the optimistic bubbles and keep the draft.
      userItem.remove();
      assistantItem.remove();
      updateEmptyState();
      if (error.status === 401) {
        showLogin('Your session ended. Sign in again.');
        return;
      }
      showError(chatError, error.message);
    } else {
      finalStatus = 'failed';
      showError(chatError, 'The connection to MIRA was lost.');
    }
  } finally {
    setStatus(assistantItem, finalStatus);
    activeStream = null;
    setStreaming(false);
    announcer.textContent = finalStatus === 'completed' ? 'Response complete.' : `Response ${STATUS_LABELS[finalStatus]?.toLowerCase() ?? finalStatus}.`;
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
