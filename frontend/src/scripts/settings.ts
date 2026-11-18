import {
  ApiError,
  getPreferences,
  updatePreferences,
  type UserPreferences,
} from './api';
import { signOut, type SignOutResult } from './session';
import {
  DEFAULT_PREFERENCES,
  applyPreferences,
  isFontSizeValue,
  isThemeValue,
  readCachedFontSize,
  readCachedTheme,
  retainAppearanceCache,
  type AppearancePreferences,
} from './theme';

export function usernameInitial(username: string): string {
  const first = Array.from(username.trim())[0];
  return first ? first.toLocaleUpperCase() : '';
}

// User-provided names are untrusted: assigned with textContent only, never innerHTML.
export function renderAccountArea(username: string, avatar: HTMLElement, name: HTMLElement): void {
  avatar.textContent = usernameInitial(username);
  name.textContent = username;
}

export function clearAccountArea(avatar: HTMLElement, name: HTMLElement): void {
  avatar.textContent = '';
  name.textContent = '';
}

const FOCUSABLE =
  'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled]), a[href], [tabindex]:not([tabindex="-1"])';

export function getFocusable(root: ParentNode): HTMLElement[] {
  return Array.from(root.querySelectorAll(FOCUSABLE)).filter((node): node is HTMLElement => {
    const element = node as HTMLElement;
    return !element.hidden && element.getAttribute('aria-hidden') !== 'true';
  });
}

export function handleDialogKeydown(
  event: Pick<KeyboardEvent, 'key' | 'shiftKey'> & { preventDefault(): void },
  dialog: ParentNode,
  onClose: () => void,
  active: Element | null,
): void {
  if (event.key === 'Escape') {
    event.preventDefault();
    onClose();
    return;
  }
  if (event.key !== 'Tab') return;
  const items = getFocusable(dialog);
  if (items.length === 0) return;
  const first = items[0];
  const last = items[items.length - 1];
  if (event.shiftKey && (active === first || !active)) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && (active === last || !active)) {
    event.preventDefault();
    first.focus();
  }
}

export type SettingsDialogElement = HTMLElement & {
  showModal?: () => void;
  close?: () => void;
  open?: boolean;
};

export function openSettingsDialog(dialog: SettingsDialogElement, restoreTo: HTMLElement): void {
  if (typeof dialog.showModal === 'function') dialog.showModal();
  else {
    dialog.setAttribute('open', '');
    dialog.open = true;
  }
  const first = getFocusable(dialog)[0];
  first?.focus();
  const onClose = () => {
    dialog.removeEventListener('close', onClose);
    restoreTo.focus();
  };
  dialog.addEventListener('close', onClose);
}

export function closeSettingsDialog(dialog: SettingsDialogElement): void {
  if (typeof dialog.close === 'function') dialog.close();
  else {
    dialog.removeAttribute('open');
    dialog.open = false;
    dialog.dispatchEvent(new Event('close'));
  }
}

export function syncSettingsControls(root: ParentNode, preferences: AppearancePreferences): void {
  for (const input of root.querySelectorAll<HTMLInputElement>('input[name="theme"]')) {
    input.checked = input.value === preferences.theme;
  }
  for (const input of root.querySelectorAll<HTMLInputElement>('input[name="fontSize"]')) {
    input.checked = input.value === preferences.fontSize;
  }
  const inspector = root.querySelector<HTMLInputElement>('#settings-inspector-default');
  if (inspector) inspector.checked = preferences.inspectorOpenByDefault;
  const inspectLinks = root.querySelector<HTMLInputElement>('#settings-inspect-links');
  if (inspectLinks) inspectLinks.checked = preferences.showInspectLinks;
}

export function showSettingsAlert(target: HTMLElement, message: string | null): void {
  target.textContent = message ?? '';
  target.hidden = !message;
}

export class SettingsController {
  current: AppearancePreferences = {
    theme: readCachedTheme(),
    fontSize: readCachedFontSize(),
    inspectorOpenByDefault: DEFAULT_PREFERENCES.inspectorOpenByDefault,
    showInspectLinks: DEFAULT_PREFERENCES.showInspectLinks,
  };

  // Saves are serialized (one PUT at a time, in the order the user made the changes) and only the latest change's
  // outcome is applied, so the UI and the theme cache end on the server's last-applied state even when several
  // changes overlap (e.g. arrowing through a radio group). #confirmed is the last state the server returned.
  #seq = 0;
  #queue: Promise<unknown> = Promise.resolve();
  #confirmed: AppearancePreferences | null = null;

  async loadFromServer(): Promise<AppearancePreferences> {
    const seq = ++this.#seq;
    const preferences = await getPreferences();
    const loaded: AppearancePreferences = {
      theme: isThemeValue(preferences.theme) ? preferences.theme : DEFAULT_PREFERENCES.theme,
      fontSize: isFontSizeValue(preferences.fontSize) ? preferences.fontSize : DEFAULT_PREFERENCES.fontSize,
      inspectorOpenByDefault: preferences.inspectorOpenByDefault === true,
      showInspectLinks: preferences.showInspectLinks !== false,
    };
    this.#confirmed = loaded;
    if (seq === this.#seq) {
      this.current = loaded;
      applyPreferences(this.current);
    }
    return this.current;
  }

  applyCached(): AppearancePreferences {
    applyPreferences(this.current);
    return this.current;
  }

  // Resolves true when the change was saved, or when a later change superseded it (its outcome is then ignored);
  // false only when this is the latest change and its save failed (the UI is reverted to the last confirmed state).
  async change(
    update: Partial<UserPreferences>,
    ui: {
      showError: (message: string | null) => void;
      announce: (message: string) => void;
      sync: (preferences: AppearancePreferences) => void;
    },
  ): Promise<boolean> {
    const seq = ++this.#seq;
    const previous = { ...this.current };
    this.current = { ...this.current, ...update };
    applyPreferences(this.current);
    ui.sync(this.current);
    ui.showError(null);
    const request = this.#queue.then(() => updatePreferences(update));
    this.#queue = request.catch(() => undefined);
    try {
      const saved = await request;
      this.#confirmed = {
        theme: isThemeValue(saved.theme) ? saved.theme : DEFAULT_PREFERENCES.theme,
        fontSize: isFontSizeValue(saved.fontSize) ? saved.fontSize : DEFAULT_PREFERENCES.fontSize,
        inspectorOpenByDefault: saved.inspectorOpenByDefault === true,
        showInspectLinks: saved.showInspectLinks !== false,
      };
      if (seq !== this.#seq) return true; // superseded by a later change
      this.current = { ...this.#confirmed };
      applyPreferences(this.current);
      ui.sync(this.current);
      ui.announce('Settings saved.');
      return true;
    } catch (error) {
      if (seq !== this.#seq) return true; // superseded; the later change decides the final state
      const restored = this.#confirmed ? { ...this.#confirmed } : previous;
      this.current = restored;
      applyPreferences(restored);
      ui.sync(restored);
      ui.showError(error instanceof ApiError ? error.message : 'Could not save settings.');
      return false;
    }
  }

  clearMemory(): void {
    this.#seq++; // ignore any save still in flight from the previous session
    this.#confirmed = null;
    this.current = {
      theme: readCachedTheme(),
      fontSize: readCachedFontSize(),
      inspectorOpenByDefault: DEFAULT_PREFERENCES.inspectorOpenByDefault,
      showInspectLinks: DEFAULT_PREFERENCES.showInspectLinks,
    };
    applyPreferences(this.current);
  }
}

export async function signOutFromSettings(
  performSignOut: () => Promise<SignOutResult> = signOut,
  storage: Storage | null = defaultStorage(),
): Promise<SignOutResult> {
  const result = await performSignOut();
  if (result.signedOut) retainAppearanceCache(storage);
  return result;
}

function defaultStorage(): Storage | null {
  try {
    return globalThis.localStorage;
  } catch {
    return null;
  }
}
