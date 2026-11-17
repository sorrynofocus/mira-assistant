# M3: account area and settings

Status: **implemented and reviewed** (MIRA Security 08:45 SECURITY REVIEW PASS; MIRA Reviewer 08:46 REVIEW, no Blocker/Major, two Minors resolved 08:50). Committed for push with the owner's approval (08:38, M3 only); **pushed-pending-owner-manual-pass** (the owner's Windows manual pass happens later on another PC). Owner decisions received directly by MIRA Boss on 2026-11-17 at 08:16 MST (scope) and 08:17 MST (MIRA Security role).

Constraints (AGENTS.md): Astro static + vanilla TypeScript, no new dependencies or paid services; .NET 10 Minimal APIs; plain SQLite with the hand-written migration runner (ADR 0002); API bound to 127.0.0.1:5080; default-deny auth; CSRF on unsafe methods; owner ID from the principal only; parameterized SQL; render user data with `textContent` only.

## 1. Decisions

| Topic | Decision |
|---|---|
| Account area | Lower-left corner of the chat workspace (bottom of the conversation sidebar): avatar with the username's first letter, the username, and a gear button. Replaces "Signed in as …" in the top bar. |
| Settings | The gear opens an accessible modal Settings dialog. |
| Themes | `mira-dark` = **MIRA Dark** (today's look, the default); `sovereign` = **Sovereign** (black-and-white, high-contrast, flat; inspired by a black/white aesthetic, no logos or trademarks); `system` = **Match system** (follows the OS `prefers-color-scheme`: dark → MIRA Dark, light → Sovereign; updates live when the OS setting changes). |
| M3 settings | Theme, font size (`small` / `default` / `large`), inspector open by default (boolean), and Sign out (moved into the dialog). More settings later. |
| API path | `/api/me/preferences`: un-versioned, under the existing `/api` group, like `/api/auth/me`, `/api/conversations` and `/api/diagnostics` (MIRA Boss decision). |
| Security | MIRA Security threat-models this plan before chunks A and B start and security-reviews the code after chunk C, alongside MIRA Reviewer (section 6). |

Out of scope (later): profile editing, avatar upload, password change, density, other settings.

## 2. Contract (chunk 0, frozen): `backend/Mira.Core/Preferences.cs`

- `PreferenceKeys`: `theme`, `fontSize`, `inspectorOpenByDefault`.
- `ThemeValues`: `mira-dark`, `sovereign`, `system`. `FontSizeValues`: `small`, `default`, `large`.
- `UserPreferences(Theme, FontSize, InspectorOpenByDefault)` with `Default` = `mira-dark` / `default` / `false`, `FromStored(rows)` (unknown or invalid stored values fall back to the default) and `Apply(update)`.
- `PreferenceUpdate(Theme?, FontSize?, InspectorOpenByDefault?)` (null = unchanged) with `ToStored()` (booleans stored as `"true"`/`"false"`).
- `PreferenceRules.TryParseUpdate(JsonElement, out update, out error)`: the PUT validation rules below. `PreferenceValidationError(Key, Message)` names the key only if it is allowlisted (an unknown key gives `Key = ""` and the fixed message `Unknown preference.`), and never echoes a submitted value.
- `IUserPreferenceStore`: `GetAsync(owner)` (defaults filled in, never null) and `UpdateAsync(owner, update)` (upsert in one transaction, returns the full object).
- Tests: `backend/Mira.Tests/PreferenceContractTests.cs`.

## 3. API

Both routes are in the existing `/api` group: signed-in user required (fallback policy, default deny), `X-CSRF-TOKEN` required on `PUT`, loopback host only. The owner comes from the principal; no user ID is accepted in the path or body.

**`GET /api/me/preferences`** → **200**
```json
{ "theme": "mira-dark", "fontSize": "default", "inspectorOpenByDefault": false }
```
Always the full object with defaults filled in. 401 when signed out.

**`PUT /api/me/preferences`** with a partial object, e.g. `{ "theme": "system" }` → **200** with the full object after the update. Omitted keys are unchanged.

| Key | Allowed values (exact, case-sensitive) |
|---|---|
| `theme` | `"mira-dark"`, `"sovereign"`, `"system"` |
| `fontSize` | `"small"`, `"default"`, `"large"` |
| `inspectorOpenByDefault` | `true`, `false` (JSON booleans) |

**400** (problem JSON; `title` names the key and the rule only for allowlisted keys, never a submitted value or an unknown key name; an unknown key gives the fixed title `Unknown preference.`) when the body is not a JSON object, a key is unknown (including `ownerUserId` or any other field), a key appears twice, a value has the wrong type (`null` and `"true"` included) or is not on the allowlist, or the object is empty. **400** with the fixed title `The request body must be a JSON object.` for invalid JSON or an empty body, and **415** with a fixed title for a non-JSON `Content-Type`; neither includes request content or parser text (also in Development), and neither the body nor the exception message is logged. **403** without the CSRF header, **401** signed out. Preference values are not logged.

## 4. Data: migration 4

Append-only; migrations 1–3 are unchanged. `MiraDatabase.LatestVersion` becomes 4.

```sql
CREATE TABLE UserPreferences (
    OwnerUserId TEXT NOT NULL REFERENCES Users(Id),
    Key         TEXT NOT NULL,
    Value       TEXT NOT NULL,
    UpdatedAt   TEXT NOT NULL,
    PRIMARY KEY (OwnerUserId, Key)
);
```

Key/value rows, so later settings need no schema change. Only keys and values on the allowlist are written; a missing row means the default. Upserts use `INSERT ... ON CONFLICT(OwnerUserId, Key) DO UPDATE`, parameterized and owner-scoped.

## 5. Frontend behaviour

- **CSS custom properties.** Today's hard-coded colours in `index.astro` move into variables (`--bg`, `--surface`, `--surface-2`, `--border`, `--text`, `--text-muted`, `--accent`, `--accent-text`, `--danger`, `--focus`) under `:root, [data-theme="mira-dark"]`, plus a `[data-theme="sovereign"]` set (black background, white text and borders, inverted primary buttons, no tints). MIRA Dark must look exactly as it does today.
- **Font size.** `html { font-size: calc(100% * var(--font-scale, 1)); }` with `[data-font-size="small"] { --font-scale: .875 }`, `default` = 1, `large` = 1.125. The stylesheet already uses `rem`, so everything scales.
- **No flash.** A tiny static script, `frontend/public/theme-init.js`, loaded synchronously as the first thing in `<head>` (`<script src="/theme-init.js"></script>`, before the stylesheet; not inline, so a future CSP needs no hash for it):
  - reads `localStorage` `mira.theme` and `mira.fontSize` and keeps them only if they are on the allowlist, checked with an array `includes` (never an object lookup, so `__proto__` or `constructor` can't pass); otherwise `mira-dark` / `default`;
  - resolves `system` with `matchMedia('(prefers-color-scheme: light)')` → `sovereign`, else `mira-dark`;
  - writes only constants from a fixed lookup table: `data-theme` and `data-font-size` on `<html>` and the `theme-color` meta `content` (e.g. MIRA Dark `#121a2c`, Sovereign `#000000`), using `setAttribute` only, never `innerHTML` or `document.write`;
  - never writes to `localStorage`; is wrapped in `try/catch` (storage may be blocked).
- **Live system changes.** While the chosen theme is `system`, `theme.ts` listens to the media query's `change` event and re-resolves; switching to a fixed theme removes the listener.
- **Server wins.** After sign-in, `GET /api/me/preferences` is applied and only `theme` and `fontSize` are re-cached (`mira.theme`, `mira.fontSize`). Never cache the username, user ID or `inspectorOpenByDefault`. The cache is shared by everyone using the browser, so the login page shows the last user's look (non-sensitive; accepted).
- **Sign out** (in the dialog) reuses the existing flow: `POST /api/auth/logout` with `X-CSRF-TOKEN` (no new or GET sign-out path), then closes the dialog and clears in-memory user state (preferences object, username, conversation list). Only `mira.theme` and `mira.fontSize` stay in `localStorage`.
- **Account area.** Initial and username rendered with `textContent` only.
- **Settings dialog.** Native `<dialog>` with `showModal()`, labelled by its heading. Focus starts on the first control, Tab/Shift+Tab stay inside, Esc and Close dismiss it, and focus returns to the gear. Theme and font size are radio groups (`fieldset`/`legend`); "Open the inspector by default" is a checkbox; Sign out is a button (flow above). A change applies at once and is sent with `PUT`; on failure the change reverts and a `role="alert"` message shows; success is announced through `#announcer`.
- **Inspector open by default.** Applied once on sign-in or page load. Toggling the inspector later doesn't change the preference.
- **Contrast.** Both themes meet WCAG AA (4.5:1 text, 3:1 UI components and focus indicator).

## 6. Chunks and file ownership

One owner per file. A bot that needs a change in another chunk's file asks that owner through MIRA Boss. Chunks A and B touch disjoint files and can run in parallel against the frozen contract.

| Step | Owner | Files (create or edit) | Depends on |
|---|---|---|---|
| **0. Contracts** ✅ | MIRA Boss | `backend/Mira.Core/Preferences.cs`, `backend/Mira.Tests/PreferenceContractTests.cs`, this doc | — |
| **Security: plan threat model** | MIRA Security | `REVIEW.md` (one `SECURITY REVIEW` entry only) | 0 |
| **A. Backend** | MIRA Backend | `backend/Mira.Infrastructure/Sqlite/MiraDatabase.cs` (migration 4); new `backend/Mira.Infrastructure/Sqlite/SqliteUserPreferenceStore.cs`; new `backend/Mira.Infrastructure/InMemoryUserPreferenceStore.cs`; new `backend/Mira.Api/Preferences/PreferenceEndpoints.cs`; `backend/Mira.Api/MiraHost.cs` (register the store, map the endpoints); new `backend/Mira.Tests/PreferencesApiTests.cs`; new `backend/Mira.Tests/PreferenceStoreTests.cs`; `backend/Mira.Tests/ConversationStoreTests.cs` (only the assertions pinned to schema version 3) | 0, threat model |
| **B. Frontend** | MIRA Frontend | new `frontend/public/theme-init.js` (pre-paint script); `frontend/src/pages/index.astro` (CSS variables, both themes, font scale, `<script src="/theme-init.js">` first in `<head>`, account area, Settings dialog markup); new `frontend/src/scripts/theme.ts` + `theme.test.ts` (allowlists, resolve `system`, apply, cache, media listener); new `frontend/src/scripts/settings.ts` + `settings.test.ts` (dialog, controls, PUT/revert, account area); `frontend/src/scripts/api.ts` (`getPreferences`, `updatePreferences`, `Preferences` type); `frontend/src/scripts/chat.ts` (wire-up, remove "Signed in as", sign-out from the dialog, inspector default) | 0, threat model |
| **C. Integration** | MIRA Boss | `README.MD`, `docs/M3_ACCOUNT_SETTINGS.md` (final), `REVIEW.md` (one IMPLEMENTATION entry); full clean run; curl smoke test | A, B |
| **Review** | MIRA Reviewer + MIRA Security | `REVIEW.md` only (`REVIEW` and `SECURITY REVIEW` entries) | C |

Untouched by M3: auth, sessions, conversations, diagnostics, chat endpoints and stores; `scripts/`; `docs/agent-sync/`; `AGENTS.md`.

**Chunk A brief (MIRA Backend).** Implement `IUserPreferenceStore` exactly as declared in `Preferences.cs`: SQLite (migration 4, upsert, owner-scoped, one transaction per update) and in-memory (tests). `GET` returns `store.GetAsync(owner)`. `PUT` reads the body as `JsonElement`, calls `PreferenceRules.TryParseUpdate`, maps an error to 400 problem JSON with `title` = `"<key>: <message>"` (or the message alone for an empty key), and returns `store.UpdateAsync(owner, update)`. Serialize with the web defaults (camelCase). No logging of values. Security Minor 2: invalid JSON, an empty body and a non-JSON `Content-Type` must return a fixed 400 / 415 problem with no request content or parser text (read the body yourself or handle `BadHttpRequestException`; don't rely on the default binder message), and log neither the body nor the exception message. Security Minor 3: test that after `POST /api/auth/logout` the old cookie gets 401 on `GET /api/me/preferences`. Don't touch `frontend/`, `Mira.Core/` or the chat, conversation or diagnostics code.

**Chunk B brief (MIRA Frontend).** Build the account area, Settings dialog, both themes, font scale, `system` handling and the pre-paint script as in section 5, against the API in section 3 (mock `fetch` in tests). Keep the allowlists in `theme.ts` identical to section 3 and test that `public/theme-init.js` and `theme.ts` agree. Security Minor 4: `theme-init.js` checks with `includes`, writes only constants via `setAttribute`, never writes storage. Security Minor 3: Sign out calls the existing `POST /api/auth/logout` with CSRF, closes the dialog, clears in-memory user state and leaves only `mira.theme` and `mira.fontSize` in `localStorage`. No `innerHTML`, `insertAdjacentHTML` or `set:html`; no new dependencies. Don't touch `backend/`.

## 7. Acceptance criteria

1. The lower-left account area shows the initial, username and gear; it is keyboard reachable and named for screen readers.
2. The gear opens a modal Settings dialog: focus starts inside, Tab is trapped, Esc and Close dismiss it, focus returns to the gear.
3. Theme, font size and inspector default apply at once; MIRA Dark looks exactly as today; Sovereign is black and white with no trademarks; Match system follows the OS and updates live.
4. Choices persist per user across reload, restart and sign-out/in; another user's preferences are unaffected.
5. No flash of the wrong theme or font size on reload.
6. The API follows section 3: allowlists enforced, 400 rules, CSRF on `PUT`, 401 when signed out, no user ID accepted from the client, values not logged. Invalid JSON, an empty body and a non-JSON content type return a fixed 400/415 with no request content; unknown key names are not echoed.
7. Sign out in the dialog uses the existing CSRF-protected `POST /api/auth/logout`, revokes the session (the old cookie gets 401), and leaves only the theme and font-size cache in `localStorage`.
8. M1/M2 behaviour is unchanged and all existing tests pass.
9. MIRA Reviewer and MIRA Security report no open Blocker or Major finding; the owner's Windows manual pass is done.

## 8. Tests

- **Contract (chunk 0, done):** JSON shape and defaults; valid partial updates; every 400 rule (unknown key, duplicate key, wrong type, `null`, off-allowlist, empty, not an object) with the value never echoed; unknown key names (markup, a 10 KB name, wrong case) never echoed; stored-row fallback to defaults.
- **Backend (A, xUnit):** migration 4 on a fresh DB and on an M2 (version 3) DB with users, sessions and conversations intact; idempotent; defaults with no rows; `PUT` then `GET` round-trip for each key; partial update keeps other keys; 400 cases through HTTP (problem title names only allowlisted keys, no value); invalid JSON, empty body and non-JSON content type give a fixed 400/415 with no request content; the old cookie gets 401 on `GET /api/me/preferences` after logout; 403 without CSRF; 401 signed out; two users isolated; a restart keeps the values; no preference value in the logs.
- **Frontend (B, Vitest):** allowlist validation of cached values (bad, missing or hostile: markup, quotes, a very long string, `__proto__`, `constructor` → the DOM gets the defaults, and `theme-init.js` writes nothing to storage); `system` resolves by the media query and follows a `change` event, and a fixed theme drops the listener; applying sets `data-theme`, `data-font-size` and `theme-color`; the server value overrides the cache; a failed `PUT` reverts and shows an alert; the dialog opens, traps focus, closes on Esc and returns focus; the account area uses `textContent`; `theme-init.js` and `theme.ts` agree; sign-out calls `POST /api/auth/logout` with CSRF and leaves only `mira.theme` and `mira.fontSize` in `localStorage`.
- **Whole suite (C):** `npm ci`, Vitest, `astro check`, `astro build`, `dotnet build --no-incremental`, `dotnet test` twice; curl smoke test of both routes (including 400 and 403).
- **Manual (owner, Windows):** all three themes and font sizes by keyboard and mouse, an OS light/dark switch while on Match system, the reload flash check, a contrast spot-check.

## 9. Optional (MIRA Security nits, not required for M3)

- Migration 4 hardening: `WITHOUT ROWID` and `CHECK (length(Key) <= 64 AND length(Value) <= 64)` as a backstop to the allowlist (chunk A may add them). If user deletion is ever added, delete the user's preferences with their conversations.
- Shared browser: the theme/font-size cache isn't per user, so the login page shows the previous user's look. Accepted as non-sensitive.

## 10. Final state (chunk C, 2026-11-17)

- **As built:** matches sections 2–6. Migration 4 adds the optional hardening from section 9 (`WITHOUT ROWID`, length `CHECK`s). `PUT` reads the body itself: non-JSON `Content-Type` → 415, invalid JSON or empty body → 400, and (integration fix) a body over Kestrel's 256 KB limit → fixed 413 `The request body is too large.` instead of the Development exception page. All 400/413/415 bodies are fixed titles, identical in Development and Production.
- **Frontend deviations (accepted):** the charset, viewport and theme-color metas come before `<script src="/theme-init.js">` (still the first script, ahead of the stylesheet); sign-out clears `ConversationWorkspace`'s public fields directly.
- **MIRA Boss decisions:** the pre-existing MIRA Dark contrast gaps (some muted, warning and error text on the selected item; focus ring against the accent; input and panel borders; hover) are **accepted for M3** to keep MIRA Dark pixel-identical, and go to the backlog. The **Show inspector** toggle doesn't write the preference by design: the Settings checkbox is the default, the toggle is per session.
- **Overlapping saves (Reviewer Minor 2):** `SettingsController.change` serializes `PUT`s and applies only the latest change's outcome; a failed latest save reverts to the last server-confirmed state.
- **Verification:** see the REVIEW.md chunk C IMPLEMENTATION entry (full clean run and curl smoke test) and the 08:50 RESOLUTION.

## 11. Backlog

- MIRA Dark contrast pass (bring every text and UI pair to WCAG AA; changes MIRA Dark's look, so it needs its own owner-visible change).
- From M2: tests for another user's delete during a stream, and for the 5 s `DeleteWait` timeout when a provider ignores cancellation.
- Preference rows on user delete: if user deletion is added, delete the user's `UserPreferences` rows along with their conversations (no cascade today).
- Optional: an in-process test for the 413 body limit (Kestrel's limit isn't enforced by the test server; covered by the smoke test).
- Security Nit 1 (08:45): map `BadHttpRequestException` (e.g. 413) to fixed problem JSON globally, so other body-reading `/api` routes don't show the Development exception page.
- Security Nit 2 (08:45): a strict CSP (`default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'`) plus `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`. All scripts are external now, so no hashes are needed.
- Owner's Windows manual pass for M3 (themes, font sizes, an OS switch on Match system, reload flash, keyboard use of the Settings dialog), on another PC; required before M3 is accepted.
