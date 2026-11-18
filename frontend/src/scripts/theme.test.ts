import { afterEach, describe, expect, it } from 'vitest';
import {
  DEFAULT_FONT_SIZE,
  DEFAULT_THEME,
  FONT_SIZE_VALUES,
  STORAGE_FONT_SIZE,
  STORAGE_THEME,
  THEME_COLORS,
  THEME_VALUES,
  applyAppearance,
  applyPreferences,
  cacheAppearance,
  isFontSizeValue,
  isThemeValue,
  isWatchingSystemTheme,
  readCachedFontSize,
  readCachedTheme,
  resolveTheme,
  stopWatchingSystemTheme,
  type MediaQueryLike,
} from './theme';

// Astro check uses the browser typings, so Node builtins are loaded untyped at runtime.
const nodeProcess = (globalThis as unknown as {
  process: { getBuiltinModule(name: string): unknown };
}).process;
const { readFileSync } = nodeProcess.getBuiltinModule('fs') as {
  readFileSync(path: string, encoding: string): string;
};
const { dirname, join } = nodeProcess.getBuiltinModule('path') as {
  dirname(path: string): string;
  join(...parts: string[]): string;
};
const { fileURLToPath } = nodeProcess.getBuiltinModule('url') as {
  fileURLToPath(url: string): string;
};
const { runInNewContext } = nodeProcess.getBuiltinModule('vm') as {
  runInNewContext(code: string, context: object, options?: { filename?: string }): unknown;
};

const themeInitSource = readFileSync(
  join(dirname(fileURLToPath(import.meta.url)), '../../public/theme-init.js'),
  'utf8',
);

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

function fakeTarget() {
  const attrs: Record<string, string> = {};
  const root = {
    attrs,
    setAttribute(name: string, value: string) {
      attrs[name] = value;
    },
  };
  const meta = {
    content: '#121a2c',
    setAttribute(name: string, value: string) {
      if (name === 'content') this.content = value;
    },
  };
  return { root, meta, attrs };
}

function fakeMedia(matches: boolean): MediaQueryLike & { matches: boolean; fire(): void } {
  const listeners = new Set<() => void>();
  return {
    matches,
    addEventListener(_type: 'change', listener: () => void) {
      listeners.add(listener);
    },
    removeEventListener(_type: 'change', listener: () => void) {
      listeners.delete(listener);
    },
    fire() {
      for (const listener of listeners) listener();
    },
    get listening() {
      return listeners.size;
    },
  } as MediaQueryLike & { matches: boolean; fire(): void; listening: number };
}

function runThemeInit(options: {
  theme?: string;
  fontSize?: string;
  prefersLight?: boolean;
  storageWrites?: { count: number };
}): { attrs: Record<string, string>; content: string; writes: number } {
  const attrs: Record<string, string> = {};
  const data: Record<string, string> = {};
  if (options.theme !== undefined) data['mira.theme'] = options.theme;
  if (options.fontSize !== undefined) data['mira.fontSize'] = options.fontSize;
  let writes = 0;
  const meta = {
    content: '#121a2c',
    setAttribute(name: string, value: string) {
      if (name === 'content') this.content = value;
    },
  };
  const html = {
    setAttribute(name: string, value: string) {
      attrs[name] = value;
    },
  };
  runInNewContext(
    themeInitSource,
    {
      localStorage: {
        getItem(key: string) {
          return data[key] ?? null;
        },
        setItem() {
          writes += 1;
        },
        removeItem() {
          writes += 1;
        },
        clear() {
          writes += 1;
        },
      },
      document: {
        documentElement: html,
        querySelector(selector: string) {
          return selector.includes('theme-color') ? meta : null;
        },
      },
      window: {
        matchMedia() {
          return { matches: options.prefersLight === true };
        },
      },
    },
    { filename: 'theme-init.js' },
  );
  return { attrs, content: meta.content, writes };
}

afterEach(() => {
  stopWatchingSystemTheme();
});

describe('theme allowlists', () => {
  it('keeps the same allowlists and theme colours as theme-init.js', () => {
    expect(THEME_VALUES).toEqual(['mira-dark', 'sovereign', 'system']);
    expect(FONT_SIZE_VALUES).toEqual(['small', 'default', 'large']);
    expect(THEME_COLORS).toEqual({ 'mira-dark': '#121a2c', sovereign: '#000000' });
    expect(themeInitSource).toContain("['mira-dark', 'sovereign', 'system']");
    expect(themeInitSource).toContain("['small', 'default', 'large']");
    expect(themeInitSource).toContain("'mira-dark': '#121a2c'");
    expect(themeInitSource).toContain('sovereign: \'#000000\'');
    expect(themeInitSource).toMatch(/themes\.includes\(/);
    expect(themeInitSource).toMatch(/fonts\.includes\(/);
    expect(themeInitSource).toMatch(/setAttribute\(/);
    expect(themeInitSource).not.toMatch(/innerHTML|document\.write|\beval\s*\(/);
    expect(themeInitSource).not.toMatch(/localStorage\.setItem|localStorage\.removeItem|localStorage\.clear/);
  });

  it('treats bad, missing or hostile cached values as the defaults and never writes storage from theme-init.js', () => {
    const hostile = [
      undefined,
      '',
      'MIRA-DARK',
      'dark',
      '<img src=x onerror=alert(1)>',
      '"quoted"',
      'a'.repeat(10_000),
      '__proto__',
      'constructor',
    ];
    for (const value of hostile) {
      expect(isThemeValue(value)).toBe(false);
      expect(isFontSizeValue(value)).toBe(false);
      const storage = memoryStorage(value === undefined ? {} : { [STORAGE_THEME]: value, [STORAGE_FONT_SIZE]: value });
      expect(readCachedTheme(storage)).toBe(DEFAULT_THEME);
      expect(readCachedFontSize(storage)).toBe(DEFAULT_FONT_SIZE);
      const result = runThemeInit({ theme: value, fontSize: value });
      expect(result.attrs['data-theme']).toBe('mira-dark');
      expect(result.attrs['data-font-size']).toBe('default');
      expect(result.content).toBe('#121a2c');
      expect(result.writes).toBe(0);
    }
  });
});

describe('resolve and apply', () => {
  it('resolves system from the light/dark media query and follows a change event', () => {
    expect(resolveTheme('system', { matches: true })).toBe('sovereign');
    expect(resolveTheme('system', { matches: false })).toBe('mira-dark');
    expect(resolveTheme('sovereign', { matches: false })).toBe('sovereign');

    const media = fakeMedia(false);
    const { root, meta, attrs } = fakeTarget();
    const prefs = { theme: 'system' as const, fontSize: 'large' as const, inspectorOpenByDefault: false, showInspectLinks: true };
    expect(applyPreferences(prefs, { root, themeColorMeta: meta }, media, memoryStorage())).toBe('mira-dark');
    expect(attrs['data-theme']).toBe('mira-dark');
    expect(attrs['data-font-size']).toBe('large');
    expect(attrs['data-show-inspect-links']).toBe('true');
    expect(meta.content).toBe('#121a2c');
    expect(isWatchingSystemTheme()).toBe(true);

    media.matches = true;
    media.fire();
    expect(attrs['data-theme']).toBe('sovereign');
    expect(meta.content).toBe('#000000');

    applyPreferences({ ...prefs, theme: 'mira-dark' }, { root, themeColorMeta: meta }, media, memoryStorage());
    expect(isWatchingSystemTheme()).toBe(false);
    const before = attrs['data-theme'];
    media.matches = false;
    media.fire();
    expect(attrs['data-theme']).toBe(before);
  });

  it('sets data-theme, data-font-size and theme-color from a fixed theme', () => {
    const { root, meta, attrs } = fakeTarget();
    applyAppearance('sovereign', 'small', { root, themeColorMeta: meta, media: { matches: false } });
    expect(attrs).toEqual({ 'data-theme': 'sovereign', 'data-font-size': 'small' });
    expect(meta.content).toBe('#000000');
  });

  it('lets the server value override the cache', () => {
    const storage = memoryStorage({ [STORAGE_THEME]: 'sovereign', [STORAGE_FONT_SIZE]: 'large' });
    expect(readCachedTheme(storage)).toBe('sovereign');
    const { root, meta, attrs } = fakeTarget();
    applyPreferences(
      { theme: 'mira-dark', fontSize: 'default', inspectorOpenByDefault: true, showInspectLinks: false },
      { root, themeColorMeta: meta },
      fakeMedia(true),
      storage,
    );
    expect(storage.getItem(STORAGE_THEME)).toBe('mira-dark');
    expect(storage.getItem(STORAGE_FONT_SIZE)).toBe('default');
    expect(storage.getItem('inspectorOpenByDefault')).toBeNull();
    expect(storage.getItem('mira.showInspectLinks')).toBeNull();
    expect(storage.getItem('showInspectLinks')).toBeNull();
    expect(attrs['data-theme']).toBe('mira-dark');
    expect(attrs['data-font-size']).toBe('default');
    expect(attrs['data-show-inspect-links']).toBe('false');
  });

  it('caches only allowlisted theme and font-size keys', () => {
    const storage = memoryStorage();
    cacheAppearance('system', 'large', storage);
    expect(storage.getItem(STORAGE_THEME)).toBe('system');
    expect(storage.getItem(STORAGE_FONT_SIZE)).toBe('large');
    expect([...THEME_VALUES]).toContain('system');
  });
});
