export const THEME_VALUES = ['mira-dark', 'sovereign', 'system'] as const;
export const FONT_SIZE_VALUES = ['small', 'default', 'large'] as const;
export const THEME_COLORS = { 'mira-dark': '#121a2c', sovereign: '#000000' } as const;

export const STORAGE_THEME = 'mira.theme';
export const STORAGE_FONT_SIZE = 'mira.fontSize';

export const DEFAULT_THEME = 'mira-dark' as const;
export const DEFAULT_FONT_SIZE = 'default' as const;

export type ThemeValue = (typeof THEME_VALUES)[number];
export type FontSizeValue = (typeof FONT_SIZE_VALUES)[number];
export type ResolvedTheme = 'mira-dark' | 'sovereign';

export interface AppearancePreferences {
  theme: ThemeValue;
  fontSize: FontSizeValue;
  inspectorOpenByDefault: boolean;
  showInspectLinks: boolean; // not cached; default true
}

export const DEFAULT_PREFERENCES: AppearancePreferences = {
  theme: DEFAULT_THEME,
  fontSize: DEFAULT_FONT_SIZE,
  inspectorOpenByDefault: false,
  showInspectLinks: true,
};

export function isThemeValue(value: unknown): value is ThemeValue {
  return typeof value === 'string' && (THEME_VALUES as readonly string[]).includes(value);
}

export function isFontSizeValue(value: unknown): value is FontSizeValue {
  return typeof value === 'string' && (FONT_SIZE_VALUES as readonly string[]).includes(value);
}

export function readCachedTheme(storage: Pick<Storage, 'getItem'> | null = availableStorage()): ThemeValue {
  if (!storage) return DEFAULT_THEME;
  try {
    const value = storage.getItem(STORAGE_THEME);
    return isThemeValue(value) ? value : DEFAULT_THEME;
  } catch {
    return DEFAULT_THEME;
  }
}

export function readCachedFontSize(storage: Pick<Storage, 'getItem'> | null = availableStorage()): FontSizeValue {
  if (!storage) return DEFAULT_FONT_SIZE;
  try {
    const value = storage.getItem(STORAGE_FONT_SIZE);
    return isFontSizeValue(value) ? value : DEFAULT_FONT_SIZE;
  } catch {
    return DEFAULT_FONT_SIZE;
  }
}

export function cacheAppearance(
  theme: ThemeValue,
  fontSize: FontSizeValue,
  storage: Pick<Storage, 'setItem'> | null = availableStorage(),
): void {
  if (!storage) return;
  try {
    storage.setItem(STORAGE_THEME, theme);
    storage.setItem(STORAGE_FONT_SIZE, fontSize);
  } catch {
    // Storage may be blocked; the in-memory appearance still applies.
  }
}

export function resolveTheme(
  theme: ThemeValue,
  media: { matches: boolean } | null = systemColorScheme(),
): ResolvedTheme {
  if (theme !== 'system') return theme;
  return media?.matches ? 'sovereign' : 'mira-dark';
}

export interface ApplyTarget {
  root?: Pick<HTMLElement, 'setAttribute'>;
  themeColorMeta?: Pick<HTMLElement, 'setAttribute'> | null;
  media?: { matches: boolean } | null;
}

export function applyAppearance(theme: ThemeValue, fontSize: FontSizeValue, target: ApplyTarget = {}): ResolvedTheme {
  const resolved = resolveTheme(theme, target.media === undefined ? systemColorScheme() : target.media);
  const root = target.root ?? (typeof document !== 'undefined' ? document.documentElement : null);
  root?.setAttribute('data-theme', resolved);
  root?.setAttribute('data-font-size', fontSize);
  const meta =
    target.themeColorMeta !== undefined
      ? target.themeColorMeta
      : typeof document !== 'undefined'
        ? document.querySelector('meta[name="theme-color"]')
        : null;
  meta?.setAttribute('content', THEME_COLORS[resolved]);
  return resolved;
}

export type MediaQueryLike = {
  matches: boolean;
  addEventListener(type: 'change', listener: () => void): void;
  removeEventListener(type: 'change', listener: () => void): void;
};

let watching: { media: MediaQueryLike; listener: () => void } | null = null;

export function watchSystemTheme(
  getTheme: () => ThemeValue,
  getFontSize: () => FontSizeValue,
  media: MediaQueryLike | null = systemMediaQuery(),
  target: ApplyTarget = {},
): void {
  stopWatchingSystemTheme();
  if (!media || getTheme() !== 'system') return;
  const listener = () => {
    applyAppearance(getTheme(), getFontSize(), { ...target, media });
  };
  media.addEventListener('change', listener);
  watching = { media, listener };
}

export function stopWatchingSystemTheme(): void {
  if (!watching) return;
  watching.media.removeEventListener('change', watching.listener);
  watching = null;
}

export function isWatchingSystemTheme(): boolean {
  return watching !== null;
}

export function applyPreferences(
  preferences: AppearancePreferences,
  target: ApplyTarget = {},
  media: MediaQueryLike | null = systemMediaQuery(),
  storage: Pick<Storage, 'setItem'> | null = availableStorage(),
): ResolvedTheme {
  const resolved = applyAppearance(preferences.theme, preferences.fontSize, {
    ...target,
    media: preferences.theme === 'system' ? (target.media ?? media) : target.media,
  });
  const root = target.root ?? (typeof document !== 'undefined' ? document.documentElement : null);
  root?.setAttribute('data-show-inspect-links', preferences.showInspectLinks ? 'true' : 'false');
  cacheAppearance(preferences.theme, preferences.fontSize, storage);
  watchSystemTheme(() => preferences.theme, () => preferences.fontSize, media, target);
  return resolved;
}

export function retainAppearanceCache(storage: Storage | null = availableStorage()): void {
  if (!storage) return;
  try {
    const theme = storage.getItem(STORAGE_THEME);
    const fontSize = storage.getItem(STORAGE_FONT_SIZE);
    const keys: string[] = [];
    for (let i = 0; i < storage.length; i++) {
      const key = storage.key(i);
      if (key && key.startsWith('mira.')) keys.push(key);
    }
    for (const key of keys) storage.removeItem(key);
    if (isThemeValue(theme)) storage.setItem(STORAGE_THEME, theme);
    if (isFontSizeValue(fontSize)) storage.setItem(STORAGE_FONT_SIZE, fontSize);
  } catch {
    // Ignore blocked storage.
  }
}

function availableStorage(): Storage | null {
  try {
    return globalThis.localStorage;
  } catch {
    return null;
  }
}

function systemMediaQuery(): MediaQueryLike | null {
  try {
    return typeof window !== 'undefined' ? window.matchMedia('(prefers-color-scheme: light)') : null;
  } catch {
    return null;
  }
}

function systemColorScheme(): { matches: boolean } | null {
  return systemMediaQuery();
}
