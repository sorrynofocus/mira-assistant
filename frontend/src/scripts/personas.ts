import {
  ApiError,
  BUILTIN_MIRA_PERSONA_ID,
  PERSONA_LIMITS,
  createPersona,
  deletePersona,
  draftPersona,
  getDefaultPersonaId,
  getPersona,
  getPersonaVersion,
  listPersonaVersions,
  listPersonas,
  revertPersona,
  setConversationPersona,
  setDefaultPersona,
  updatePersona,
  type ConversationSnapshot,
  type Persona,
  type PersonaDetail,
  type PersonaDraft,
  type PersonaStatus,
  type PersonaVersion,
  type PersonaVersionInfo,
  type PersonaWrite,
  type PipelineEvent,
} from './api';

export const DELETED_PIN_STATUS = 'Persona no longer exists';
export const DELETED_PIN_NEW_TURNS = 'New turns use the built-in MIRA.';
export const DELETED_PERSONA_NAME = '(deleted)';
export const DRAFT_CHOICE_LABEL = 'This is a suggested choice, not a fact about you.';
export const VERSION_RETENTION_NOTE = 'Only the last 5 versions are kept. Older versions are pruned on save.';

export function isBuiltInPersonaId(id: string | null | undefined): boolean {
  return id === BUILTIN_MIRA_PERSONA_ID;
}

export function conversationPinStatus(
  conversation: Pick<ConversationSnapshot, 'personaId' | 'personaStatus'> | null | undefined,
  catalog: readonly Persona[],
  catalogLoaded = true,
): PersonaStatus | 'unknown' {
  if (!conversation?.personaId) return 'unknown';
  if (conversation.personaStatus === 'deleted') return 'deleted';
  if (conversation.personaStatus === 'active') return 'active';
  if (!catalogLoaded) return 'unknown';
  if (isBuiltInPersonaId(conversation.personaId)) return 'active';
  return catalog.some((persona) => persona.id === conversation.personaId) ? 'active' : 'deleted';
}

export function personaLabel(persona: Pick<Persona, 'name' | 'isBuiltIn'>): string {
  return persona.isBuiltIn ? `${persona.name} (built-in)` : persona.name;
}

export function findPersona(catalog: readonly Persona[], id: string | null | undefined): Persona | undefined {
  return id ? catalog.find((persona) => persona.id === id) : undefined;
}

// Names come from the live catalog or a detail fetch, never from a pipeline event payload.
export function personaFieldsFromEvents(events: readonly PipelineEvent[]): {
  personaId: string | null;
  personaVersion: number | null;
} {
  for (const event of events) {
    const data = event.data ?? {};
    const id = typeof data.personaId === 'string' && data.personaId ? data.personaId : null;
    const version = typeof data.personaVersion === 'number' ? data.personaVersion : null;
    if (id || version != null) return { personaId: id, personaVersion: version };
  }
  return { personaId: null, personaVersion: null };
}

export function livePersonaName(id: string | null | undefined, catalog: readonly Persona[]): string | null {
  if (!id) return null;
  return findPersona(catalog, id)?.name ?? null;
}

export async function resolvePersonaName(
  id: string | null | undefined,
  catalog: readonly Persona[],
): Promise<string> {
  if (!id) return DELETED_PERSONA_NAME;
  const known = livePersonaName(id, catalog);
  if (known) return known;
  try {
    const detail = await getPersona(id);
    return detail.name;
  } catch (error) {
    if (error instanceof ApiError && (error.status === 404 || error.status === 400)) return DELETED_PERSONA_NAME;
    throw error;
  }
}

// Untrusted persona fields are assigned with textContent or form .value only.
export function renderPersonaList(
  list: HTMLElement,
  catalog: readonly Persona[],
  handlers: { onOpen: (id: string) => void },
): void {
  list.replaceChildren();
  for (const persona of catalog) {
    const item = list.ownerDocument.createElement('li');
    item.className = 'persona-item';
    item.dataset.personaId = persona.id;
    if (persona.isBuiltIn) item.dataset.builtIn = 'true';

    const open = list.ownerDocument.createElement('button');
    open.type = 'button';
    open.className = 'persona-item__open';
    open.textContent = personaLabel(persona);
    open.addEventListener('click', () => handlers.onOpen(persona.id));

    item.append(open);
    list.append(item);
  }
}

export function fillPersonaSelect(
  select: HTMLSelectElement,
  catalog: readonly Persona[],
  selectedId: string | null,
  missing?: { id: string; label: string },
): void {
  select.replaceChildren();
  const doc = select.ownerDocument;
  let sawSelected = false;
  for (const persona of catalog) {
    const option = doc.createElement('option');
    option.value = persona.id;
    option.textContent = personaLabel(persona);
    if (persona.id === selectedId) {
      option.selected = true;
      sawSelected = true;
    }
    select.append(option);
  }
  if (selectedId && !sawSelected) {
    const option = doc.createElement('option');
    option.value = selectedId;
    option.textContent = missing?.label ?? DELETED_PIN_STATUS;
    option.selected = true;
    select.append(option);
  }
}

export function renderConversationPersonaStatus(
  status: HTMLElement,
  note: HTMLElement,
  pin: PersonaStatus | 'unknown',
  name: string | null,
): void {
  if (pin === 'deleted') {
    status.textContent = DELETED_PIN_STATUS;
    note.textContent = DELETED_PIN_NEW_TURNS;
    note.hidden = false;
  } else {
    status.textContent = name ?? '';
    note.textContent = '';
    note.hidden = true;
  }
}

export function renderInspectorPersona(
  target: {
    section: HTMLElement;
    id: HTMLElement;
    version: HTMLElement;
    name: HTMLElement;
  },
  fields: { personaId: string | null; personaVersion: number | null },
  name: string | null,
): void {
  const hasFields = Boolean(fields.personaId) || fields.personaVersion != null;
  target.section.hidden = !hasFields;
  if (!hasFields) {
    target.id.textContent = '';
    target.version.textContent = '';
    target.name.textContent = '';
    return;
  }
  target.id.textContent = fields.personaId ?? '';
  target.version.textContent = fields.personaVersion != null ? String(fields.personaVersion) : '';
  target.name.textContent = name ?? DELETED_PERSONA_NAME;
}

export function showPersonaAlert(target: HTMLElement, message: string | null): void {
  target.textContent = message ?? '';
  target.hidden = !message;
}

export type EditorMode = 'create' | 'edit' | 'builtin';

export interface EditorState {
  mode: EditorMode;
  id: string | null;
  isBuiltIn: boolean;
  isChoice: boolean;
  name: string;
  description: string;
  markdown: string;
  activeVersion: number | null;
  versions: PersonaVersionInfo[];
  diff: PersonaVersion | null;
}

export function emptyEditorState(): EditorState {
  return {
    mode: 'create',
    id: null,
    isBuiltIn: false,
    isChoice: false,
    name: '',
    description: '',
    markdown: '',
    activeVersion: null,
    versions: [],
    diff: null,
  };
}

export function syncEditorControls(root: ParentNode, state: EditorState): void {
  const name = root.querySelector<HTMLInputElement>('#persona-name');
  const description = root.querySelector<HTMLInputElement>('#persona-description');
  const body = root.querySelector<HTMLTextAreaElement>('#persona-body');
  const preview = root.querySelector<HTMLElement>('#persona-preview');
  const choice = root.querySelector<HTMLElement>('#persona-editor-choice');
  const heading = root.querySelector<HTMLElement>('#persona-editor-heading');
  const save = root.querySelector<HTMLButtonElement>('#persona-save');
  const revertBlock = root.querySelector<HTMLElement>('#persona-versions');
  const remove = root.querySelector<HTMLButtonElement>('#persona-delete');
  const copy = root.querySelector<HTMLButtonElement>('#persona-copy');
  const suggest = root.querySelector<HTMLButtonElement>('#persona-suggest');
  const readonly = state.isBuiltIn;
  if (name) {
    name.value = state.name;
    name.readOnly = readonly;
  }
  if (description) {
    description.value = state.description;
    description.readOnly = readonly;
  }
  if (body) {
    body.value = state.markdown;
    body.readOnly = readonly;
  }
  if (preview) preview.textContent = state.markdown;
  if (choice) {
    choice.textContent = state.isChoice ? DRAFT_CHOICE_LABEL : '';
    choice.hidden = !state.isChoice;
  }
  if (heading) heading.textContent = state.isBuiltIn ? 'Built-in MIRA' : state.id ? 'Edit persona' : 'New persona';
  if (save) save.hidden = readonly;
  if (remove) remove.hidden = readonly || !state.id;
  if (copy) copy.hidden = !state.isBuiltIn;
  if (suggest) suggest.hidden = readonly;
  if (revertBlock) revertBlock.hidden = readonly || !state.id;
}

export function readEditorWrite(root: ParentNode): PersonaWrite {
  const name = root.querySelector<HTMLInputElement>('#persona-name')?.value ?? '';
  const description = root.querySelector<HTMLInputElement>('#persona-description')?.value ?? '';
  const markdown = root.querySelector<HTMLTextAreaElement>('#persona-body')?.value ?? '';
  const trimmedDescription = description.trim();
  return {
    name: name.trim(),
    description: trimmedDescription.length > 0 ? trimmedDescription : null,
    markdown,
  };
}

export function validateWrite(write: PersonaWrite): string | null {
  if (write.name.length < 1 || write.name.length > PERSONA_LIMITS.maxNameLength) {
    return `Name must be 1–${PERSONA_LIMITS.maxNameLength} characters.`;
  }
  if ((write.description?.length ?? 0) > PERSONA_LIMITS.maxDescriptionLength) {
    return `Description must be at most ${PERSONA_LIMITS.maxDescriptionLength} characters.`;
  }
  if (write.markdown.length < 1 || write.markdown.length > PERSONA_LIMITS.maxBodyLength) {
    return `Body must be 1–${PERSONA_LIMITS.maxBodyLength} characters.`;
  }
  return null;
}

export function renderVersionList(
  list: HTMLElement,
  versions: readonly PersonaVersionInfo[],
  activeVersion: number | null,
  handlers: { onPreview: (version: number) => void; onRevert: (version: number) => void },
): void {
  list.replaceChildren();
  const doc = list.ownerDocument;
  for (const version of versions) {
    const item = doc.createElement('li');
    item.className = 'persona-version';
    item.dataset.version = String(version.version);

    const label = doc.createElement('span');
    label.className = 'persona-version__label';
    label.textContent =
      version.version === activeVersion
        ? `Version ${version.version} (current): ${version.name}`
        : `Version ${version.version}: ${version.name}`;

    const preview = doc.createElement('button');
    preview.type = 'button';
    preview.className = 'link-button';
    preview.textContent = 'Show';
    preview.addEventListener('click', () => handlers.onPreview(version.version));

    item.append(label, preview);
    if (version.version !== activeVersion) {
      const revert = doc.createElement('button');
      revert.type = 'button';
      revert.className = 'link-button';
      revert.textContent = `Revert to version ${version.version}`;
      revert.addEventListener('click', () => handlers.onRevert(version.version));
      item.append(revert);
    }
    list.append(item);
  }
}

export function renderVersionDiff(target: HTMLElement, current: PersonaWrite, version: PersonaVersion | null): void {
  if (!version) {
    target.textContent = '';
    target.hidden = true;
    return;
  }
  const lines = [
    `Version ${version.version}`,
    `Name: ${version.name}`,
    `Description: ${version.description ?? ''}`,
    'Body:',
    version.markdown,
    '',
    'Current (unsaved if you have edited):',
    `Name: ${current.name}`,
    `Description: ${current.description ?? ''}`,
    'Body:',
    current.markdown,
  ];
  target.textContent = lines.join('\n');
  target.hidden = false;
}

export const SAVE_CONFIRM = 'Save this persona? Only the last 5 versions are kept.';
export const REVERT_CONFIRM = (version: number): string =>
  `Revert to version ${version}? This restores that name, description and body.`;
export const DELETE_CONFIRM =
  'Delete this persona? Conversations that use it will keep their messages and show that the persona no longer exists.';

export class PersonaCatalog {
  list: Persona[] = [];
  defaultPersonaId = BUILTIN_MIRA_PERSONA_ID;
  loaded = false;

  async refresh(): Promise<void> {
    const [list, defaultPersonaId] = await Promise.all([listPersonas(), getDefaultPersonaId().catch(() => BUILTIN_MIRA_PERSONA_ID)]);
    this.list = list;
    this.defaultPersonaId = defaultPersonaId;
    this.loaded = true;
  }

  clear(): void {
    this.list = [];
    this.defaultPersonaId = BUILTIN_MIRA_PERSONA_ID;
    this.loaded = false;
  }

  async changeDefault(
    personaId: string,
    ui: { showError: (message: string | null) => void; sync: (id: string) => void },
  ): Promise<boolean> {
    const previous = this.defaultPersonaId;
    this.defaultPersonaId = personaId;
    ui.sync(personaId);
    ui.showError(null);
    try {
      this.defaultPersonaId = await setDefaultPersona(personaId);
      ui.sync(this.defaultPersonaId);
      return true;
    } catch (error) {
      this.defaultPersonaId = previous;
      ui.sync(previous);
      ui.showError(error instanceof ApiError ? error.message : 'Could not save the default persona.');
      return false;
    }
  }

  async changeConversationPersona(
    conversationId: string,
    personaId: string,
    ui: { showError: (message: string | null) => void; sync: (id: string) => void },
    previousId: string | null,
  ): Promise<ConversationSnapshot | undefined> {
    ui.sync(personaId);
    ui.showError(null);
    try {
      return await setConversationPersona(conversationId, personaId);
    } catch (error) {
      if (previousId) ui.sync(previousId);
      ui.showError(error instanceof ApiError ? error.message : 'Could not change this chat’s persona.');
      return undefined;
    }
  }
}

export class PersonaEditorSession {
  state: EditorState = emptyEditorState();

  startNew(): EditorState {
    this.state = emptyEditorState();
    return this.state;
  }

  async open(id: string): Promise<EditorState> {
    const detail = await getPersona(id);
    const versions = detail.isBuiltIn ? [] : await listPersonaVersions(id).catch(() => []);
    this.state = {
      mode: detail.isBuiltIn ? 'builtin' : 'edit',
      id: detail.id,
      isBuiltIn: detail.isBuiltIn,
      isChoice: false,
      name: detail.name,
      description: detail.description ?? '',
      markdown: detail.markdown,
      activeVersion: detail.activeVersion,
      versions,
      diff: null,
    };
    return this.state;
  }

  async copyBuiltIn(): Promise<EditorState> {
    const detail = await getPersona(BUILTIN_MIRA_PERSONA_ID);
    this.state = {
      ...emptyEditorState(),
      name: detail.name,
      description: detail.description ?? '',
      markdown: detail.markdown,
    };
    return this.state;
  }

  applyDraft(draft: PersonaDraft): EditorState {
    this.state = {
      ...this.state,
      mode: this.state.id && !this.state.isBuiltIn ? 'edit' : 'create',
      isBuiltIn: false,
      isChoice: true,
      name: draft.name,
      description: draft.description ?? '',
      markdown: draft.markdown,
      diff: null,
    };
    return this.state;
  }

  async suggest(write?: PersonaWrite): Promise<EditorState> {
    return this.applyDraft(await draftPersona(write));
  }

  async previewVersion(version: number): Promise<EditorState> {
    if (!this.state.id) return this.state;
    this.state = { ...this.state, diff: await getPersonaVersion(this.state.id, version) };
    return this.state;
  }

  async save(write: PersonaWrite, confirm: (message: string) => boolean): Promise<PersonaDetail | null> {
    if (this.state.isBuiltIn) return null;
    const invalid = validateWrite(write);
    if (invalid) throw new Error(invalid);
    if (!confirm(SAVE_CONFIRM)) return null;
    const saved = this.state.id
      ? await updatePersona(this.state.id, write)
      : await createPersona(write);
    this.state = {
      ...this.state,
      mode: 'edit',
      id: saved.id,
      isBuiltIn: saved.isBuiltIn,
      isChoice: false,
      name: saved.name,
      description: saved.description ?? '',
      markdown: saved.markdown,
      activeVersion: saved.activeVersion,
      versions: await listPersonaVersions(saved.id).catch(() => []),
      diff: null,
    };
    return saved;
  }

  async revert(version: number, confirm: (message: string) => boolean): Promise<PersonaDetail | null> {
    if (!this.state.id || this.state.isBuiltIn) return null;
    if (!confirm(REVERT_CONFIRM(version))) return null;
    const saved = await revertPersona(this.state.id, version);
    this.state = {
      ...this.state,
      isChoice: false,
      name: saved.name,
      description: saved.description ?? '',
      markdown: saved.markdown,
      activeVersion: saved.activeVersion,
      versions: await listPersonaVersions(saved.id).catch(() => []),
      diff: null,
    };
    return saved;
  }

  async remove(confirm: (message: string) => boolean): Promise<boolean> {
    if (!this.state.id || this.state.isBuiltIn) return false;
    if (!confirm(DELETE_CONFIRM)) return false;
    await deletePersona(this.state.id);
    this.state = emptyEditorState();
    return true;
  }
}
