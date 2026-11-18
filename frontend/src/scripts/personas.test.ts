import { afterEach, describe, expect, it, vi } from 'vitest';
import { BUILTIN_MIRA_PERSONA_ID, setCsrfToken, type Persona, type PipelineEvent } from './api';
import {
  DELETED_PERSONA_NAME,
  DELETED_PIN_NEW_TURNS,
  DELETED_PIN_STATUS,
  DRAFT_CHOICE_LABEL,
  PersonaCatalog,
  PersonaEditorSession,
  conversationPinStatus,
  fillPersonaSelect,
  personaFieldsFromEvents,
  personaLabel,
  renderConversationPersonaStatus,
  renderInspectorPersona,
  renderPersonaList,
  resolvePersonaName,
  showPersonaAlert,
  syncEditorControls,
} from './personas';

const mira: Persona = {
  id: BUILTIN_MIRA_PERSONA_ID,
  activeVersion: 1,
  isBuiltIn: true,
  createdAt: '2026-11-18T00:00:00Z',
  updatedAt: '2026-11-18T00:00:00Z',
  name: 'MIRA',
  description: 'Built-in',
};

const jack: Persona = {
  id: 'jack-id',
  activeVersion: 2,
  isBuiltIn: false,
  createdAt: '2026-11-18T00:00:00Z',
  updatedAt: '2026-11-18T00:00:00Z',
  name: 'Jack, sales representative',
  description: 'Sales',
};

type FakeEl = {
  tag: string;
  id?: string;
  name?: string;
  type?: string;
  value: string;
  hidden: boolean;
  readOnly?: boolean;
  selected?: boolean;
  disabled?: boolean;
  textContent: string;
  innerHTML: string;
  dataset: Record<string, string>;
  children: FakeEl[];
  ownerDocument: { createElement(tag: string): FakeEl };
  setAttribute(name: string, value: string): void;
  getAttribute(name: string): string | null;
  querySelector(selector: string): FakeEl | null;
  querySelectorAll(selector: string): FakeEl[];
  replaceChildren(): void;
  append(...nodes: FakeEl[]): void;
  addEventListener(type: string, listener: EventListener): void;
  click(): void;
};

function el(tag: string, attrs: Record<string, string> = {}): FakeEl {
  const listeners = new Map<string, EventListener[]>();
  const node: FakeEl = {
    tag,
    id: attrs.id,
    name: attrs.name,
    type: attrs.type,
    value: attrs.value ?? '',
    hidden: attrs.hidden !== undefined,
    readOnly: false,
    selected: false,
    disabled: false,
    textContent: '',
    innerHTML: '',
    dataset: {},
    children: [],
    ownerDocument: { createElement: (childTag: string) => el(childTag) },
    setAttribute(name, value) {
      if (name === 'id') this.id = value;
    },
    getAttribute(name) {
      return name === 'id' ? (this.id ?? null) : null;
    },
    querySelector(selector) {
      return this.querySelectorAll(selector)[0] ?? null;
    },
    querySelectorAll(selector) {
      const results: FakeEl[] = [];
      const walk = (current: FakeEl) => {
        if (current !== this && matches(current, selector)) results.push(current);
        for (const child of current.children) walk(child);
      };
      walk(this);
      return results;
    },
    replaceChildren() {
      this.children = [];
    },
    append(...nodes) {
      this.children.push(...nodes);
    },
    addEventListener(type, listener) {
      listeners.set(type, [...(listeners.get(type) ?? []), listener]);
    },
    click() {
      for (const listener of listeners.get('click') ?? []) listener(new Event('click'));
    },
  };
  return node;
}

function matches(node: FakeEl, selector: string): boolean {
  if (selector.startsWith('#')) return node.id === selector.slice(1);
  return node.tag === selector;
}

function jsonResponse(body: unknown, status = 200) {
  const text = body === undefined ? '' : JSON.stringify(body);
  return {
    ok: status >= 200 && status < 300,
    status,
    headers: new Headers({ 'content-type': 'application/json' }),
    json: async () => body,
    text: async () => text,
  };
}

function editorTree() {
  const name = el('input', { id: 'persona-name' });
  const description = el('input', { id: 'persona-description' });
  const body = el('textarea', { id: 'persona-body' });
  const preview = el('pre', { id: 'persona-preview' });
  const choice = el('p', { id: 'persona-editor-choice', hidden: '' });
  const heading = el('h2', { id: 'persona-editor-heading' });
  const save = el('button', { id: 'persona-save' });
  const remove = el('button', { id: 'persona-delete' });
  const copy = el('button', { id: 'persona-copy' });
  const suggest = el('button', { id: 'persona-suggest' });
  const versions = el('section', { id: 'persona-versions', hidden: '' });
  const root = el('dialog', { id: 'persona-editor' });
  root.append(name, description, body, preview, choice, heading, save, remove, copy, suggest, versions);
  return { root, name, description, body, preview, choice, heading, save, remove, copy, suggest, versions };
}

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('persona list and inspector rendering', () => {
  it('writes names with textContent only and does not render raw Markdown as HTML', () => {
    const hostile: Persona = {
      ...jack,
      id: 'hostile',
      name: '<img src=x onerror=alert(1)> **Jack**',
    };
    const list = el('ul');
    const opened: string[] = [];
    renderPersonaList(list as unknown as HTMLElement, [mira, hostile], { onOpen: (id) => opened.push(id) });
    const buttons = list.children.map((item) => item.children[0]);
    expect(buttons[0].textContent).toBe('MIRA (built-in)');
    expect(buttons[1].textContent).toBe(hostile.name);
    expect(buttons[1].innerHTML).toBe('');
    expect(list.innerHTML).toBe('');
    buttons[1].click();
    expect(opened).toEqual(['hostile']);
  });

  it('takes id and version from the event and resolves the name live, ignoring a name on the payload', async () => {
    const events: PipelineEvent[] = [
      {
        eventType: 'request.started',
        elapsedMs: 0,
        createdAt: '2026-11-18T00:00:00Z',
        data: {
          personaId: 'jack-id',
          personaVersion: 2,
          // Hostile or stale payload fields must never be used as the name.
          name: '<b>from-event</b>',
          markdown: '# ignored',
          description: 'ignored',
        } as PipelineEvent['data'],
      },
    ];
    expect(personaFieldsFromEvents(events)).toEqual({ personaId: 'jack-id', personaVersion: 2 });
    expect(personaFieldsFromEvents(events)).not.toHaveProperty('name');

    const section = el('section');
    const id = el('span');
    const version = el('span');
    const name = el('span');
    const live = await resolvePersonaName('jack-id', [mira, jack]);
    expect(live).toBe(jack.name);
    renderInspectorPersona(
      { section, id, version, name } as unknown as Parameters<typeof renderInspectorPersona>[0],
      personaFieldsFromEvents(events),
      live,
    );
    expect(section.hidden).toBe(false);
    expect(id.textContent).toBe('jack-id');
    expect(version.textContent).toBe('2');
    expect(name.textContent).toBe('Jack, sales representative');
    expect(name.textContent).not.toContain('from-event');
    expect(name.innerHTML).toBe('');
  });

  it('shows (deleted) when the live lookup cannot find the persona', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ title: 'Not found.' }, 404)));
    const name = await resolvePersonaName('missing-id', [mira]);
    expect(name).toBe(DELETED_PERSONA_NAME);
    const section = el('section');
    const id = el('span');
    const version = el('span');
    const label = el('span');
    renderInspectorPersona(
      { section, id, version, name: label } as unknown as Parameters<typeof renderInspectorPersona>[0],
      { personaId: 'missing-id', personaVersion: 1 },
      name,
    );
    expect(label.textContent).toBe('(deleted)');
  });
});

describe('deleted pin and picker', () => {
  it('reports Persona no longer exists and that new turns use built-in MIRA without retargeting the pin', () => {
    const conversation = { personaId: 'gone-id', personaStatus: 'deleted' as const, id: 'c1', title: null, createdAt: '', updatedAt: '', messages: [] };
    expect(conversationPinStatus(conversation, [mira, jack])).toBe('deleted');
    const status = el('p');
    const note = el('p');
    renderConversationPersonaStatus(status as unknown as HTMLElement, note as unknown as HTMLElement, 'deleted', null);
    expect(status.textContent).toBe(DELETED_PIN_STATUS);
    expect(note.textContent).toBe(DELETED_PIN_NEW_TURNS);
    expect(note.hidden).toBe(false);
    expect(conversation.personaId).toBe('gone-id');
  });

  it('treats a missing catalog row as a deleted pin when the API omitted personaStatus', () => {
    expect(conversationPinStatus({ personaId: 'gone-id' }, [mira], true)).toBe('deleted');
    expect(conversationPinStatus({ personaId: jack.id }, [mira, jack], true)).toBe('active');
  });

  it('calls PUT /api/conversations/{id}/persona and reverts the picker with role=alert on failure', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(jsonResponse({ title: 'That persona is not available.' }, 404));
    vi.stubGlobal('fetch', fetchMock);
    setCsrfToken('csrf-token');
    const catalog = new PersonaCatalog();
    catalog.list = [mira, jack];
    const select = el('select') as unknown as HTMLSelectElement;
    fillPersonaSelect(select, catalog.list, mira.id);
    const error = el('p', { role: 'alert' });
    const result = await catalog.changeConversationPersona(
      'conv-1',
      jack.id,
      {
        showError: (message) => showPersonaAlert(error as unknown as HTMLElement, message),
        sync: (id) => fillPersonaSelect(select, catalog.list, id),
      },
      mira.id,
    );
    expect(result).toBe(false);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/conversations/conv-1/persona');
    expect(fetchMock.mock.calls[0][1]).toMatchObject({
      method: 'PUT',
      headers: expect.objectContaining({ 'X-CSRF-TOKEN': 'csrf-token' }),
    });
    expect(JSON.parse(fetchMock.mock.calls[0][1].body as string)).toEqual({ personaId: 'jack-id' });
    expect((select as unknown as FakeEl).children.find((option) => option.selected)?.value).toBe(mira.id);
    expect(error.textContent).toBe('That persona is not available.');
    expect(error.hidden).toBe(false);
  });

  it('keeps the new picker value after a successful PUT', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse(undefined, 204)));
    setCsrfToken('csrf-token');
    const catalog = new PersonaCatalog();
    const saved = await catalog.changeConversationPersona(
      'conv-1',
      jack.id,
      { showError() {}, sync() {} },
      mira.id,
    );
    expect(saved).toBe(true);
  });
});

describe('editor save, confirm, revert and delete', () => {
  it('does not write until Save is confirmed, then PUTs a new version', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        jsonResponse({
          ...jack,
          markdown: 'Be affable and a little sarcastic.',
          activeVersion: 3,
        }),
      )
      .mockResolvedValueOnce(jsonResponse({ items: [{ personaId: jack.id, version: 3, name: jack.name, description: jack.description, createdAt: jack.updatedAt }] }));
    vi.stubGlobal('fetch', fetchMock);
    setCsrfToken('csrf-token');
    const session = new PersonaEditorSession();
    session.state = {
      mode: 'edit',
      id: jack.id,
      isBuiltIn: false,
      isChoice: true,
      name: jack.name,
      description: jack.description ?? '',
      markdown: 'old',
      activeVersion: 2,
      versions: [],
      diff: null,
    };
    const cancelled = await session.save(
      { name: jack.name, description: jack.description, markdown: 'Be affable and a little sarcastic.' },
      () => false,
    );
    expect(cancelled).toBeNull();
    expect(fetchMock).not.toHaveBeenCalled();

    const saved = await session.save(
      { name: jack.name, description: jack.description, markdown: 'Be affable and a little sarcastic.' },
      () => true,
    );
    expect(saved?.activeVersion).toBe(3);
    expect(fetchMock.mock.calls[0][0]).toBe(`/api/personas/${jack.id}`);
    expect(fetchMock.mock.calls[0][1]).toMatchObject({ method: 'PUT' });
    expect(session.state.isChoice).toBe(false);
  });

  it('reverts and deletes only after confirm, and never offers those actions for the built-in', async () => {
    const fetchMock = vi
      .fn()
      .mockResolvedValueOnce(
        jsonResponse({
          ...jack,
          markdown: 'earlier',
          activeVersion: 1,
        }),
      )
      .mockResolvedValueOnce(jsonResponse({ items: [] }))
      .mockResolvedValueOnce(jsonResponse(undefined, 204));
    vi.stubGlobal('fetch', fetchMock);
    setCsrfToken('csrf-token');
    const session = new PersonaEditorSession();
    session.state = {
      mode: 'edit',
      id: jack.id,
      isBuiltIn: false,
      isChoice: false,
      name: jack.name,
      description: '',
      markdown: 'now',
      activeVersion: 2,
      versions: [],
      diff: null,
    };
    expect(await session.revert(1, () => false)).toBeNull();
    expect(await session.remove(() => false)).toBe(false);
    expect(fetchMock).not.toHaveBeenCalled();

    const reverted = await session.revert(1, () => true);
    expect(reverted?.markdown).toBe('earlier');
    expect(fetchMock.mock.calls[0][0]).toBe(`/api/personas/${jack.id}/revert`);
    expect(JSON.parse(fetchMock.mock.calls[0][1].body as string)).toEqual({ version: 1 });

    expect(await session.remove(() => true)).toBe(true);
    expect(fetchMock.mock.calls[2][0]).toBe(`/api/personas/${jack.id}`);
    expect(fetchMock.mock.calls[2][1]).toMatchObject({ method: 'DELETE' });

    const tree = editorTree();
    const builtin = new PersonaEditorSession();
    builtin.state = {
      mode: 'builtin',
      id: mira.id,
      isBuiltIn: true,
      isChoice: false,
      name: mira.name,
      description: '',
      markdown: 'whoami',
      activeVersion: 1,
      versions: [],
      diff: null,
    };
    syncEditorControls(tree.root as unknown as ParentNode, builtin.state);
    expect(tree.save.hidden).toBe(true);
    expect(tree.remove.hidden).toBe(true);
    expect(tree.copy.hidden).toBe(false);
    expect(tree.body.readOnly).toBe(true);
    expect(await builtin.save({ name: 'x', description: null, markdown: 'y' }, () => true)).toBeNull();
    expect(await builtin.remove(() => true)).toBe(false);
  });

  it('lands a draft in the editor as a choice and still requires Save', async () => {
    const fetchMock = vi.fn().mockResolvedValue(jsonResponse({ name: 'Suggested', description: null, markdown: 'A dry wit.' }));
    vi.stubGlobal('fetch', fetchMock);
    const session = new PersonaEditorSession();
    session.startNew();
    await session.suggest({ name: '', description: null, markdown: '' });
    expect(JSON.parse(fetchMock.mock.calls[0][1].body as string)).toEqual({
      name: 'Suggested',
      description: null,
      markdown: 'A suggested personality.',
    });
    expect(session.state.isChoice).toBe(true);
    expect(session.state.id).toBeNull();
    expect(session.state.markdown).toBe('A dry wit.');
    const tree = editorTree();
    syncEditorControls(tree.root as unknown as ParentNode, session.state);
    expect(tree.choice.hidden).toBe(false);
    expect(tree.choice.textContent).toBe(DRAFT_CHOICE_LABEL);
    expect(tree.body.value).toBe('A dry wit.');
    expect(tree.preview.textContent).toBe('A dry wit.');
    expect(tree.preview.innerHTML).toBe('');

    fetchMock.mockClear();
    await session.suggest();
    expect(JSON.parse(fetchMock.mock.calls[0][1].body as string)).toEqual({
      name: 'Suggested',
      description: null,
      markdown: 'A suggested personality.',
    });

    fetchMock.mockClear();
    await session.suggest({ name: '', description: 'Dry wit', markdown: '' });
    expect(JSON.parse(fetchMock.mock.calls[0][1].body as string)).toEqual({
      name: 'Dry wit',
      description: 'Dry wit',
      markdown: 'Dry wit',
    });
  });

  it('reverts a failed default-persona PUT and shows role=alert', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(jsonResponse({ title: 'Unknown preference.' }, 400)));
    setCsrfToken('csrf-token');
    const catalog = new PersonaCatalog();
    catalog.defaultPersonaId = mira.id;
    const error = el('p');
    let shown = mira.id;
    const ok = await catalog.changeDefault(jack.id, {
      showError: (message) => showPersonaAlert(error as unknown as HTMLElement, message),
      sync: (id) => {
        shown = id;
      },
    });
    expect(ok).toBe(false);
    expect(catalog.defaultPersonaId).toBe(mira.id);
    expect(shown).toBe(mira.id);
    expect(error.textContent).toBe('Unknown preference.');
    expect(error.hidden).toBe(false);
  });
});

describe('labels', () => {
  it('marks the built-in in the picker without taking a name from event data', () => {
    expect(personaLabel(mira)).toBe('MIRA (built-in)');
    expect(personaLabel(jack)).toBe(jack.name);
    const select = el('select') as unknown as HTMLSelectElement;
    fillPersonaSelect(select, [mira, jack], 'gone-id', { id: 'gone-id', label: DELETED_PIN_STATUS });
    const options = (select as unknown as FakeEl).children;
    expect(options[2].value).toBe('gone-id');
    expect(options[2].textContent).toBe(DELETED_PIN_STATUS);
    expect(options[2].innerHTML).toBe('');
  });
});
