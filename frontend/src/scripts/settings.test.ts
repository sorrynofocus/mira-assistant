import { afterEach, describe, expect, it, vi } from 'vitest';
import { setCsrfToken } from './api';
import {
  SettingsController,
  clearAccountArea,
  closeSettingsDialog,
  getFocusable,
  handleDialogKeydown,
  openSettingsDialog,
  renderAccountArea,
  showSettingsAlert,
  signOutFromSettings,
  syncSettingsControls,
  usernameInitial,
  type SettingsDialogElement,
} from './settings';
import { DEFAULT_PREFERENCES, STORAGE_FONT_SIZE, STORAGE_THEME, stopWatchingSystemTheme } from './theme';

function memoryStorage(initial: Record<string, string> = {}): Storage {
  const data = new Map(Object.entries(initial));
  return {
    get length() {
      return data.size;
    },
    key(index: number) {
      return [...data.keys()][index] ?? null;
    },
    getItem(key: string) {
      return data.has(key) ? data.get(key)! : null;
    },
    setItem(key: string, value: string) {
      data.set(key, String(value));
    },
    removeItem(key: string) {
      data.delete(key);
    },
    clear() {
      data.clear();
    },
  } as Storage;
}

type FakeEl = {
  tag: string;
  id?: string;
  name?: string;
  type?: string;
  value?: string;
  checked?: boolean;
  hidden?: boolean;
  disabled?: boolean;
  textContent: string;
  innerHTML: string;
  attrs: Record<string, string>;
  children: FakeEl[];
  parent?: FakeEl;
  focusCalls: number;
  focused: boolean;
  setAttribute(name: string, value: string): void;
  getAttribute(name: string): string | null;
  removeAttribute(name: string): void;
  append(...nodes: FakeEl[]): void;
  querySelector(selector: string): FakeEl | null;
  querySelectorAll(selector: string): FakeEl[];
  addEventListener(type: string, listener: EventListener): void;
  removeEventListener(type: string, listener: EventListener): void;
  dispatchEvent(event: Event): boolean;
  focus(): void;
  showModal?: () => void;
  close?: () => void;
  open?: boolean;
};

function el(tag: string, attrs: Record<string, string> = {}, children: FakeEl[] = []): FakeEl {
  const listeners = new Map<string, Set<EventListener>>();
  const node: FakeEl = {
    tag,
    id: attrs.id,
    name: attrs.name,
    type: attrs.type,
    value: attrs.value,
    checked: attrs.checked === 'true' || attrs.checked === '',
    hidden: attrs.hidden !== undefined,
    disabled: attrs.disabled !== undefined,
    textContent: '',
    innerHTML: '',
    attrs: { ...attrs },
    children: [],
    focusCalls: 0,
    focused: false,
    setAttribute(name, value) {
      this.attrs[name] = value;
      if (name === 'id') this.id = value;
      if (name === 'name') this.name = value;
      if (name === 'type') this.type = value;
      if (name === 'value') this.value = value;
      if (name === 'hidden') this.hidden = true;
    },
    getAttribute(name) {
      return this.attrs[name] ?? null;
    },
    removeAttribute(name) {
      delete this.attrs[name];
      if (name === 'hidden') this.hidden = false;
      if (name === 'open') this.open = false;
    },
    append(...nodes) {
      for (const child of nodes) {
        child.parent = this;
        this.children.push(child);
      }
    },
    querySelector(selector) {
      return this.querySelectorAll(selector)[0] ?? null;
    },
    querySelectorAll(selector) {
      const results: FakeEl[] = [];
      walk(this, (candidate) => {
        if (candidate !== this && matches(candidate, selector)) results.push(candidate);
      });
      return results;
    },
    addEventListener(type, listener) {
      const set = listeners.get(type) ?? new Set();
      set.add(listener);
      listeners.set(type, set);
    },
    removeEventListener(type, listener) {
      listeners.get(type)?.delete(listener);
    },
    dispatchEvent(event) {
      for (const listener of listeners.get(event.type) ?? []) listener(event);
      return true;
    },
    focus() {
      this.focusCalls += 1;
      this.focused = true;
    },
    showModal() {
      this.open = true;
      this.setAttribute('open', '');
    },
    close() {
      this.open = false;
      this.removeAttribute('open');
      this.dispatchEvent(new Event('close'));
    },
    open: false,
  };
  node.append(...children);
  return node;
}

function walk(node: FakeEl, visit: (node: FakeEl) => void): void {
  visit(node);
  for (const child of node.children) walk(child, visit);
}

function matches(node: FakeEl, selector: string): boolean {
  const nameValue = selector.match(/^input\[name="([^"]+)"\]\[value="([^"]+)"\]$/);
  if (nameValue) return node.tag === 'input' && node.name === nameValue[1] && node.value === nameValue[2];
  const nameOnly = selector.match(/^input\[name="([^"]+)"\]$/);
  if (nameOnly) return node.tag === 'input' && node.name === nameOnly[1];
  if (selector.startsWith('#')) return node.id === selector.slice(1);
  const parts = selector.split(',').map((part) => part.trim());
  return parts.some((part) => {
    const tag = part.replace(/:not\(\[[^\]]+\]\)/g, '').replace(/:not\([^)]+\)/g, '').trim();
    if (tag && node.tag !== tag && !tag.startsWith('[') && tag !== 'a') return false;
    if (part.includes(':not([disabled])') && node.disabled) return false;
    if (part.includes('[href]') && node.getAttribute('href') === null) return false;
    if (part.includes('[tabindex]') && node.getAttribute('tabindex') === null) return false;
    if (part.includes('[tabindex]:not([tabindex="-1"])') && node.getAttribute('tabindex') === '-1') return false;
    return ['button', 'input', 'select', 'textarea', 'a'].includes(node.tag) || node.getAttribute('tabindex') !== null;
  });
}

function settingsTree() {
  const mira = el('input', { type: 'radio', name: 'theme', value: 'mira-dark' });
  const sovereign = el('input', { type: 'radio', name: 'theme', value: 'sovereign' });
  const system = el('input', { type: 'radio', name: 'theme', value: 'system' });
  const small = el('input', { type: 'radio', name: 'fontSize', value: 'small' });
  const regular = el('input', { type: 'radio', name: 'fontSize', value: 'default' });
  const large = el('input', { type: 'radio', name: 'fontSize', value: 'large' });
  const inspector = el('input', { type: 'checkbox', id: 'settings-inspector-default', name: 'inspectorOpenByDefault' });
  const inspectLinks = el('input', { type: 'checkbox', id: 'settings-inspect-links', name: 'showInspectLinks' });
  const signOut = el('button', { type: 'button', id: 'settings-sign-out' });
  const close = el('button', { type: 'button', id: 'settings-close' });
  const error = el('p', { id: 'settings-error', role: 'alert', hidden: '' });
  error.hidden = true;
  const dialog = el('dialog', { id: 'settings-dialog' }, [
    mira,
    sovereign,
    system,
    small,
    regular,
    large,
    inspector,
    inspectLinks,
    signOut,
    close,
    error,
  ]);
  return { dialog, mira, sovereign, system, small, regular, large, inspector, inspectLinks, signOut, close, error };
}

afterEach(() => {
  stopWatchingSystemTheme();
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('account area', () => {
  it('writes the username and initial with textContent only', () => {
    expect(usernameInitial('ada')).toBe('A');
    expect(usernameInitial('  mira')).toBe('M');
    expect(usernameInitial('')).toBe('');
    const avatar = el('span', { id: 'account-avatar' });
    const name = el('span', { id: 'account-username' });
    const hostile = '<img src=x onerror=alert(1)>admin';
    renderAccountArea(hostile, avatar as unknown as HTMLElement, name as unknown as HTMLElement);
    expect(name.textContent).toBe(hostile);
    expect(avatar.textContent).toBe('<');
    expect(name.innerHTML).toBe('');
    expect(avatar.innerHTML).toBe('');
    clearAccountArea(avatar as unknown as HTMLElement, name as unknown as HTMLElement);
    expect(name.textContent).toBe('');
    expect(avatar.textContent).toBe('');
  });
});

describe('settings dialog a11y', () => {
  it('opens with focus on the first control, traps tab, closes on Esc, and returns focus to the gear', () => {
    const { dialog, mira, close } = settingsTree();
    const gear = el('button', { type: 'button', id: 'account-settings' });
    openSettingsDialog(dialog as unknown as SettingsDialogElement, gear as unknown as HTMLElement);
    expect(dialog.open).toBe(true);
    expect(mira.focusCalls).toBe(1);

    const prevented: string[] = [];
    const prevent = { preventDefault() { prevented.push('tab'); } };
    handleDialogKeydown(
      { key: 'Tab', shiftKey: false, preventDefault: prevent.preventDefault },
      dialog as unknown as ParentNode,
      () => closeSettingsDialog(dialog as unknown as SettingsDialogElement),
      close as unknown as Element,
    );
    expect(prevented).toContain('tab');
    expect(mira.focusCalls).toBe(2);

    handleDialogKeydown(
      { key: 'Tab', shiftKey: true, preventDefault: prevent.preventDefault },
      dialog as unknown as ParentNode,
      () => closeSettingsDialog(dialog as unknown as SettingsDialogElement),
      mira as unknown as Element,
    );
    expect(close.focusCalls).toBe(1);

    handleDialogKeydown(
      { key: 'Escape', shiftKey: false, preventDefault() { prevented.push('esc'); } },
      dialog as unknown as ParentNode,
      () => closeSettingsDialog(dialog as unknown as SettingsDialogElement),
      mira as unknown as Element,
    );
    expect(prevented).toContain('esc');
    expect(dialog.open).toBe(false);
    expect(gear.focusCalls).toBe(1);
  });

  it('lists only enabled focusable controls', () => {
    const { dialog, signOut } = settingsTree();
    signOut.disabled = true;
    const items = getFocusable(dialog as unknown as ParentNode);
    expect(items).not.toContain(signOut);
  });
});

describe('preferences save and revert', () => {
  it('reverts the UI and shows an assertive alert when PUT fails', async () => {
    const controller = new SettingsController();
    controller.current = { theme: 'mira-dark', fontSize: 'default', inspectorOpenByDefault: false, showInspectLinks: true };
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: false,
        status: 500,
        headers: new Headers({ 'content-type': 'application/json' }),
        json: async () => ({ title: 'Could not save settings.' }),
        text: async () => '',
      }),
    );
    setCsrfToken('csrf-token');
    const { dialog, sovereign, mira, error } = settingsTree();
    const alerts: string[] = [];
    const shown: string[] = [];
    const ok = await controller.change(
      { theme: 'sovereign' },
      {
        showError(message) {
          showSettingsAlert(error as unknown as HTMLElement, message);
          if (message) shown.push(message);
        },
        announce(message) {
          alerts.push(message);
        },
        sync(preferences) {
          syncSettingsControls(dialog as unknown as ParentNode, preferences);
        },
      },
    );
    expect(ok).toBe(false);
    expect(controller.current.theme).toBe('mira-dark');
    expect(mira.checked).toBe(true);
    expect(sovereign.checked).toBe(false);
    expect(error.textContent).toBe('Could not save settings.');
    expect(error.hidden).toBe(false);
    expect(error.getAttribute('role') ?? 'alert').toBe('alert');
    expect(shown.length).toBe(1);
    expect(alerts).toEqual([]);
    expect((fetch as unknown as ReturnType<typeof vi.fn>).mock.calls[0][0]).toBe('/api/me/preferences');
    expect((fetch as unknown as ReturnType<typeof vi.fn>).mock.calls[0][1]).toMatchObject({
      method: 'PUT',
      headers: expect.objectContaining({ 'X-CSRF-TOKEN': 'csrf-token' }),
    });
  });

  it('keeps the new values after a successful PUT', async () => {
    const controller = new SettingsController();
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        status: 200,
        headers: new Headers({ 'content-type': 'application/json' }),
        json: async () => ({
          theme: 'sovereign',
          fontSize: 'large',
          inspectorOpenByDefault: true,
          showInspectLinks: true,
        }),
        text: async () =>
          JSON.stringify({
            theme: 'sovereign',
            fontSize: 'large',
            inspectorOpenByDefault: true,
            showInspectLinks: true,
          }),
      }),
    );
    setCsrfToken('csrf-token');
    const announcements: string[] = [];
    const ok = await controller.change(
      { theme: 'sovereign', fontSize: 'large', inspectorOpenByDefault: true, showInspectLinks: true },
      {
        showError() {},
        announce(message) {
          announcements.push(message);
        },
        sync() {},
      },
    );
    expect(ok).toBe(true);
    expect(controller.current).toEqual({
      theme: 'sovereign',
      fontSize: 'large',
      inspectorOpenByDefault: true,
      showInspectLinks: true,
    });
    expect(announcements).toEqual(['Settings saved.']);
  });

  it('orders overlapping saves so the UI and cache end on the server state of the last change', async () => {
    const storage = memoryStorage();
    vi.stubGlobal('localStorage', storage);
    const pending: Array<(body: object) => void> = [];
    const fetchMock = vi.fn(
      () =>
        new Promise((resolve) => {
          pending.push((body) =>
            resolve({
              ok: true,
              status: 200,
              headers: new Headers({ 'content-type': 'application/json' }),
              json: async () => body,
              text: async () => JSON.stringify(body),
            }),
          );
        }),
    );
    vi.stubGlobal('fetch', fetchMock);
    setCsrfToken('csrf-token');
    const controller = new SettingsController();
    controller.current = { theme: 'mira-dark', fontSize: 'default', inspectorOpenByDefault: false, showInspectLinks: true };
    const announcements: string[] = [];
    const ui = { showError() {}, announce: (m: string) => announcements.push(m), sync() {} };

    const first = controller.change({ theme: 'sovereign' }, ui);
    const second = controller.change({ theme: 'system' }, ui);
    expect(controller.current.theme).toBe('system'); // optimistic: the last choice shows at once
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1)); // serialized: second PUT waits
    pending[0]({ theme: 'sovereign', fontSize: 'default', inspectorOpenByDefault: false, showInspectLinks: true });
    expect(await first).toBe(true); // superseded: its response is not applied
    expect(controller.current.theme).toBe('system');
    await vi.waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2));
    pending[1]({ theme: 'system', fontSize: 'default', inspectorOpenByDefault: false, showInspectLinks: true });
    expect(await second).toBe(true);
    expect(controller.current.theme).toBe('system');
    expect(storage.getItem(STORAGE_THEME)).toBe('system');
    expect(announcements).toEqual(['Settings saved.']);
  });

  it('reverts to the last server-confirmed state when the latest overlapping save fails', async () => {
    const storage = memoryStorage();
    vi.stubGlobal('localStorage', storage);
    const responses = [
      { ok: true, status: 200, body: { theme: 'sovereign', fontSize: 'default', inspectorOpenByDefault: false, showInspectLinks: true } },
      { ok: false, status: 500, body: { title: 'Could not save settings.' } },
    ];
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => {
        const next = responses.shift()!;
        return {
          ok: next.ok,
          status: next.status,
          headers: new Headers({ 'content-type': 'application/json' }),
          json: async () => next.body,
          text: async () => JSON.stringify(next.body),
        };
      }),
    );
    setCsrfToken('csrf-token');
    const controller = new SettingsController();
    controller.current = { theme: 'mira-dark', fontSize: 'default', inspectorOpenByDefault: false, showInspectLinks: true };
    const errors: string[] = [];
    const ui = { showError: (m: string | null) => m && errors.push(m), announce() {}, sync() {} };

    const first = controller.change({ theme: 'sovereign' }, ui);
    const second = controller.change({ theme: 'system' }, ui);
    expect(await first).toBe(true);
    expect(await second).toBe(false);
    expect(controller.current.theme).toBe('sovereign'); // what the server last applied
    expect(storage.getItem(STORAGE_THEME)).toBe('sovereign');
    expect(errors.length).toBe(1);
  });

  it('reverts showInspectLinks when the PUT fails', async () => {
    const controller = new SettingsController();
    controller.current = { theme: 'mira-dark', fontSize: 'default', inspectorOpenByDefault: false, showInspectLinks: true };
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: false,
        status: 500,
        headers: new Headers({ 'content-type': 'application/json' }),
        json: async () => ({ title: 'Could not save settings.' }),
        text: async () => '',
      }),
    );
    setCsrfToken('csrf-token');
    const { dialog, inspectLinks, error } = settingsTree();
    inspectLinks.checked = true;
    const ok = await controller.change(
      { showInspectLinks: false },
      {
        showError(message) {
          showSettingsAlert(error as unknown as HTMLElement, message);
        },
        announce() {},
        sync(preferences) {
          syncSettingsControls(dialog as unknown as ParentNode, preferences);
        },
      },
    );
    expect(ok).toBe(false);
    expect(controller.current.showInspectLinks).toBe(true);
    expect(inspectLinks.checked).toBe(true);
    expect(error.hidden).toBe(false);
  });

  it('starts from the documented defaults', () => {
    expect(new SettingsController().current).toEqual(DEFAULT_PREFERENCES);
    expect(DEFAULT_PREFERENCES).toEqual({
      theme: 'mira-dark',
      fontSize: 'default',
      inspectorOpenByDefault: false,
      showInspectLinks: true,
    });
  });
});

describe('sign out', () => {
  it('posts logout with CSRF and leaves only mira.theme and mira.fontSize in storage', async () => {
    const storage = memoryStorage({
      [STORAGE_THEME]: 'system',
      [STORAGE_FONT_SIZE]: 'large',
      'mira.selectedConversationId': 'conv-1',
      'mira.other': 'nope',
      unrelated: 'keep',
    });
    const fetchMock = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      headers: new Headers({ 'content-type': 'application/json' }),
      json: async () => ({ csrfToken: 'next-token' }),
      text: async () => JSON.stringify({ csrfToken: 'next-token' }),
    });
    vi.stubGlobal('fetch', fetchMock);
    setCsrfToken('csrf-token');
    const result = await signOutFromSettings(undefined, storage);
    expect(result.signedOut).toBe(true);
    expect(fetchMock).toHaveBeenCalled();
    expect(fetchMock.mock.calls[0][0]).toBe('/api/auth/logout');
    expect(fetchMock.mock.calls[0][1]).toMatchObject({
      method: 'POST',
      headers: expect.objectContaining({ 'X-CSRF-TOKEN': 'csrf-token' }),
    });
    expect([...Array(storage.length)].map((_, i) => storage.key(i)).sort()).toEqual([
      STORAGE_FONT_SIZE,
      STORAGE_THEME,
      'unrelated',
    ]);
    expect(storage.getItem(STORAGE_THEME)).toBe('system');
    expect(storage.getItem(STORAGE_FONT_SIZE)).toBe('large');
    expect(storage.getItem('mira.selectedConversationId')).toBeNull();
  });
});

