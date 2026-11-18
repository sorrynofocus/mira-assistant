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
  type UserPreferences,
  type PipelineEvent,
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
import { InspectorController, liveInspectorClient, renderRequestLink, type InspectorClient } from './inspector';
import {
  DELETED_PERSONA_NAME,
  DELETED_PIN_NEW_TURNS,
  DELETED_PIN_STATUS,
  PersonaCatalog,
  PersonaEditorSession,
  conversationPinStatus,
  fillPersonaSelect,
  findPersona,
  personaFieldsFromEvents,
  readEditorWrite,
  renderConversationPersonaStatus,
  renderInspectorPersona,
  renderPersonaList,
  renderVersionDiff,
  renderVersionList,
  resolvePersonaName,
  showPersonaAlert,
  syncEditorControls,
} from './personas';
import {
  SettingsController,
  clearAccountArea,
  closeSettingsDialog,
  handleDialogKeydown,
  openSettingsDialog,
  renderAccountArea,
  showSettingsAlert,
  signOutFromSettings,
  syncSettingsControls,
  type SettingsDialogElement,
} from './settings';
import { applyPreferences, isFontSizeValue, isThemeValue } from './theme';
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
const accountAvatar = byId<HTMLElement>('account-avatar');
const accountUsername = byId<HTMLElement>('account-username');
const accountSettings = byId<HTMLButtonElement>('account-settings');
const settingsDialog = byId<HTMLDialogElement>('settings-dialog');
const settingsError = byId<HTMLElement>('settings-error');
const settingsClose = byId<HTMLButtonElement>('settings-close');
const settingsSignOut = byId<HTMLButtonElement>('settings-sign-out');
const workspaceBody = byId<HTMLElement>('workspace-body');
const conversationList = byId<HTMLUListElement>('conversation-list');
const conversationEmpty = byId<HTMLElement>('conversation-empty');
const newChatButton = byId<HTMLButtonElement>('new-chat-button');
const newPersonaButton = byId<HTMLButtonElement>('new-persona-button');
const personaList = byId<HTMLUListElement>('persona-list');
const conversationPersonaStatus = byId<HTMLElement>('conversation-persona-status');
const conversationPersonaPicker = byId<HTMLSelectElement>('conversation-persona-picker');
const conversationPersonaNote = byId<HTMLElement>('conversation-persona-note');
const settingsDefaultPersona = byId<HTMLSelectElement>('settings-default-persona');
const personaEditorDialog = byId<HTMLDialogElement>('persona-editor');
const personaEditorError = byId<HTMLElement>('persona-editor-error');
const personaVersionList = byId<HTMLUListElement>('persona-version-list');
const personaDiff = byId<HTMLElement>('persona-diff');
const personaBody = byId<HTMLTextAreaElement>('persona-body');
const personaPreview = byId<HTMLElement>('persona-preview');
const personaSuggest = byId<HTMLButtonElement>('persona-suggest');
const personaCopy = byId<HTMLButtonElement>('persona-copy');
const personaDelete = byId<HTMLButtonElement>('persona-delete');
const personaSave = byId<HTMLButtonElement>('persona-save');
const personaCancel = byId<HTMLButtonElement>('persona-cancel');
const inspectorPersona = byId<HTMLElement>('inspector-persona');
const inspectorPersonaId = byId<HTMLElement>('inspector-persona-id');
const inspectorPersonaVersion = byId<HTMLElement>('inspector-persona-version');
const inspectorPersonaName = byId<HTMLElement>('inspector-persona-name');
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

const personaCatalog = new PersonaCatalog();
const personaSession = new PersonaEditorSession();

const inspectorClient: InspectorClient = {
  listRequests: (conversationId) => liveInspectorClient.listRequests(conversationId),
  async getEvents(requestId) {
    const events = await liveInspectorClient.getEvents(requestId);
    await paintInspectorPersona(events);
    return events;
  },
};

const inspector = new InspectorController(
  inspectorClient,
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

const settings = new SettingsController();
applyPreferences(settings.current);

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
  if (settingsDialog.open) closeSettingsDialog(settingsDialog);
  if (personaEditorDialog.open) closeSettingsDialog(personaEditorDialog);
  personaCatalog.clear();
  personaList.replaceChildren();
  conversationPersonaPicker.replaceChildren();
  settingsDefaultPersona.replaceChildren();
  clearInspectorPersona();
  clearAccountArea(accountAvatar, accountUsername);
  settings.clearMemory();
  workspace.list = [];
  workspace.selected = null;
  workspace.renamingId = null;
  workspace.hasMore = false;
  chatView.hidden = true;
  loginView.hidden = false;
  visibleConversationId = null;
  messageList.replaceChildren();
  conversationList.replaceChildren();
  showError(loginError, message);
  showError(chatError, null);
  showSettingsAlert(settingsError, null);
  passwordInput.value = '';
  (usernameInput.value ? passwordInput : usernameInput).focus();
}

async function showChat(user: User): Promise<void> {
  loginView.hidden = true;
  chatView.hidden = false;
  renderAccountArea(user.username, accountAvatar, accountUsername);
  showError(chatError, null);
  try {
    await settings.loadFromServer();
    setInspectorOpen(settings.current.inspectorOpenByDefault);
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      showLogin('Your session ended. Sign in again.');
      return;
    }
  }
  try {
    await personaCatalog.refresh();
    paintPersonaUi();
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      showLogin('Your session ended. Sign in again.');
      return;
    }
  }
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
  paintConversationPersona();
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
  if (inspector.selectedRequestId) await paintInspectorPersona(inspector.events);
  else clearInspectorPersona();
}

function clearInspectorPersona(): void {
  renderInspectorPersona(
    {
      section: inspectorPersona,
      id: inspectorPersonaId,
      version: inspectorPersonaVersion,
      name: inspectorPersonaName,
    },
    { personaId: null, personaVersion: null },
    null,
  );
}

async function paintInspectorPersona(events: PipelineEvent[]): Promise<void> {
  const fields = personaFieldsFromEvents(events);
  let name: string | null = null;
  if (fields.personaId) {
    try {
      name = await resolvePersonaName(fields.personaId, personaCatalog.list);
    } catch {
      name = DELETED_PERSONA_NAME;
    }
  }
  renderInspectorPersona(
    {
      section: inspectorPersona,
      id: inspectorPersonaId,
      version: inspectorPersonaVersion,
      name: inspectorPersonaName,
    },
    fields,
    name,
  );
}

function paintPersonaUi(): void {
  renderPersonaList(personaList, personaCatalog.list, {
    onOpen: (id) => void openPersona(id, newPersonaButton),
  });
  fillPersonaSelect(settingsDefaultPersona, personaCatalog.list, personaCatalog.defaultPersonaId);
  paintConversationPersona();
}

function paintConversationPersona(): void {
  const selected = workspace.selected;
  const pin = conversationPinStatus(selected, personaCatalog.list, personaCatalog.loaded);
  const personaId = selected?.personaId ?? null;
  const known = findPersona(personaCatalog.list, personaId);
  renderConversationPersonaStatus(conversationPersonaStatus, conversationPersonaNote, pin, known?.name ?? null);
  fillPersonaSelect(
    conversationPersonaPicker,
    personaCatalog.list,
    personaId,
    pin === 'deleted' && personaId ? { id: personaId, label: DELETED_PIN_STATUS } : undefined,
  );
  conversationPersonaPicker.disabled = !selected;
}

function confirmAction(message: string): boolean {
  return window.confirm(message);
}

function paintEditor(): void {
  syncEditorControls(personaEditorDialog, personaSession.state);
  showPersonaAlert(personaEditorError, null);
  renderVersionList(personaVersionList, personaSession.state.versions, personaSession.state.activeVersion, {
    onPreview: (version) => void previewPersonaVersion(version),
    onRevert: (version) => void revertPersonaVersion(version),
  });
  renderVersionDiff(personaDiff, readEditorWrite(personaEditorDialog), personaSession.state.diff);
}

function openPersonaEditor(restoreTo: HTMLElement): void {
  paintEditor();
  openSettingsDialog(personaEditorDialog, restoreTo);
}

async function openPersona(id: string, restoreTo: HTMLElement): Promise<void> {
  showPersonaAlert(personaEditorError, null);
  try {
    await personaSession.open(id);
    openPersonaEditor(restoreTo);
  } catch (error) {
    showError(chatError, error instanceof ApiError ? error.message : 'Could not open that persona.');
  }
}

async function refreshPersonas(): Promise<void> {
  try {
    await personaCatalog.refresh();
  } catch (error) {
    if (error instanceof ApiError && error.status === 401) {
      showLogin('Your session ended. Sign in again.');
      return;
    }
  }
  paintPersonaUi();
}

async function previewPersonaVersion(version: number): Promise<void> {
  try {
    await personaSession.previewVersion(version);
    renderVersionDiff(personaDiff, readEditorWrite(personaEditorDialog), personaSession.state.diff);
  } catch (error) {
    showPersonaAlert(personaEditorError, error instanceof ApiError ? error.message : 'Could not load that version.');
  }
}

async function revertPersonaVersion(version: number): Promise<void> {
  try {
    const saved = await personaSession.revert(version, confirmAction);
    if (!saved) return;
    paintEditor();
    await refreshPersonas();
    announce(`Reverted to version ${version}.`);
  } catch (error) {
    showPersonaAlert(personaEditorError, error instanceof ApiError ? error.message : 'Could not revert that persona.');
  }
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
  if (conversationPinStatus(workspace.selected, personaCatalog.list, personaCatalog.loaded) === 'deleted') {
    announce(DELETED_PIN_NEW_TURNS);
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

accountSettings.addEventListener('click', () => {
  syncSettingsControls(settingsDialog, settings.current);
  fillPersonaSelect(settingsDefaultPersona, personaCatalog.list, personaCatalog.defaultPersonaId);
  showSettingsAlert(settingsError, null);
  openSettingsDialog(settingsDialog as SettingsDialogElement, accountSettings);
});

settingsDefaultPersona.addEventListener('change', () => {
  void personaCatalog.changeDefault(settingsDefaultPersona.value, {
    showError: (message) => showSettingsAlert(settingsError, message),
    sync: (id) => fillPersonaSelect(settingsDefaultPersona, personaCatalog.list, id),
  });
});

conversationPersonaPicker.addEventListener('change', () => {
  void changeChatPersona();
});

async function changeChatPersona(): Promise<void> {
  const conversationId = workspace.selectedId;
  const previous = workspace.selected?.personaId ?? null;
  const next = conversationPersonaPicker.value;
  if (!conversationId || !next || next === previous) return;
  const snapshot = await personaCatalog.changeConversationPersona(
    conversationId,
    next,
    {
      showError: (message) => showError(chatError, message),
      sync: (id) =>
        fillPersonaSelect(
          conversationPersonaPicker,
          personaCatalog.list,
          id,
          conversationPinStatus(workspace.selected, personaCatalog.list, personaCatalog.loaded) === 'deleted' &&
            workspace.selected?.personaId === id &&
            id
            ? { id, label: DELETED_PIN_STATUS }
            : undefined,
        ),
    },
    previous,
  );
  if (!snapshot) return;
  if (workspace.selected?.id === conversationId) {
    workspace.selected = {
      ...workspace.selected,
      personaId: snapshot.personaId ?? next,
      personaStatus: snapshot.personaStatus ?? 'active',
    };
  }
  const listItem = workspace.list.find((item) => item.id === conversationId);
  if (listItem) {
    listItem.personaId = snapshot.personaId ?? next;
    listItem.personaStatus = snapshot.personaStatus ?? 'active';
  }
  paintConversationPersona();
  announce('Persona updated for this chat.');
}

newPersonaButton.addEventListener('click', () => {
  personaSession.startNew();
  openPersonaEditor(newPersonaButton);
});

personaEditorDialog.addEventListener('keydown', (event) => {
  handleDialogKeydown(event, personaEditorDialog, () => closeSettingsDialog(personaEditorDialog), document.activeElement);
});

personaCancel.addEventListener('click', () => {
  closeSettingsDialog(personaEditorDialog);
});

personaBody.addEventListener('input', () => {
  personaPreview.textContent = personaBody.value;
});

personaSuggest.addEventListener('click', () => {
  void (async () => {
    try {
      await personaSession.suggest(readEditorWrite(personaEditorDialog));
      paintEditor();
    } catch (error) {
      showPersonaAlert(personaEditorError, error instanceof ApiError ? error.message : 'Could not suggest a personality.');
    }
  })();
});

personaCopy.addEventListener('click', () => {
  void (async () => {
    try {
      await personaSession.copyBuiltIn();
      paintEditor();
    } catch (error) {
      showPersonaAlert(personaEditorError, error instanceof ApiError ? error.message : 'Could not copy the built-in MIRA.');
    }
  })();
});

personaSave.addEventListener('click', () => {
  void (async () => {
    try {
      const saved = await personaSession.save(readEditorWrite(personaEditorDialog), confirmAction);
      if (!saved) return;
      closeSettingsDialog(personaEditorDialog);
      await refreshPersonas();
      announce(`Saved ${saved.name}.`);
    } catch (error) {
      showPersonaAlert(personaEditorError, error instanceof Error ? error.message : 'Could not save that persona.');
    }
  })();
});

personaDelete.addEventListener('click', () => {
  void (async () => {
    try {
      const removed = await personaSession.remove(confirmAction);
      if (!removed) return;
      closeSettingsDialog(personaEditorDialog);
      await refreshPersonas();
      announce('Persona deleted.');
    } catch (error) {
      showPersonaAlert(personaEditorError, error instanceof ApiError ? error.message : 'Could not delete that persona.');
    }
  })();
});

settingsDialog.addEventListener('keydown', (event) => {
  handleDialogKeydown(event, settingsDialog, () => closeSettingsDialog(settingsDialog), document.activeElement);
});

settingsClose.addEventListener('click', () => {
  closeSettingsDialog(settingsDialog);
});

settingsDialog.addEventListener('change', (event) => {
  const target = event.target;
  if (!(target instanceof HTMLInputElement)) return;
  if (target.name === 'theme' && isThemeValue(target.value)) {
    void savePreference({ theme: target.value });
  } else if (target.name === 'fontSize' && isFontSizeValue(target.value)) {
    void savePreference({ fontSize: target.value });
  } else if (target.id === 'settings-inspector-default') {
    void savePreference({ inspectorOpenByDefault: target.checked });
  } else if (target.id === 'settings-inspect-links') {
    void savePreference({ showInspectLinks: target.checked });
  }
});

settingsSignOut.addEventListener('click', () => {
  void handleSignOut();
});

async function savePreference(update: Partial<UserPreferences>): Promise<void> {
  const previousInspectorOpen = inspector.open;
  if (update.inspectorOpenByDefault !== undefined) {
    setInspectorOpen(update.inspectorOpenByDefault);
  }
  const ok = await settings.change(update, {
    showError: (message) => showSettingsAlert(settingsError, message),
    announce,
    sync: (preferences) => syncSettingsControls(settingsDialog, preferences),
  });
  if (!ok && update.inspectorOpenByDefault !== undefined) {
    setInspectorOpen(previousInspectorOpen);
  }
}

async function handleSignOut(): Promise<void> {
  stop();
  settingsSignOut.disabled = true;
  showError(chatError, null);
  showSettingsAlert(settingsError, null);
  try {
    const result = await signOutFromSettings();
    if (result.signedOut) {
      closeSettingsDialog(settingsDialog);
      showLogin();
    } else {
      showSettingsAlert(settingsError, result.message);
      announcer.textContent = result.message;
    }
  } finally {
    settingsSignOut.disabled = false;
  }
}

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
