# MIRA TODO and follow-ups

The durable list of open follow-ups, so nothing is lost when `REVIEW.md` is archived at a milestone close (owner request, 2026-11-17 09:06 MST).

**Rules**
- Any agent that defers something (a finding, a nit, a test gap, a follow-up) adds an item here in the same change.
- Tick an item (`[x]`) and move it to **Done** with a short "how/where" when it is finished; never delete open items.
- Milestone archiving never drops open items: they stay here (and in the carry-over list).
- Each item: owner role · source (REVIEW entry timestamp or doc §) · size (cheap / medium) · why.
- This file is a list, not a log: evidence stays in `REVIEW.md`.

Last verified against the code: 2026-11-18 00:39 MST (local `main`, after chunk C): `dotnet build --no-incremental` 0 warnings/0 errors, 293/293 xUnit (twice); frontend `npm ci`, Vitest 64/64, `astro check` 0/0/0, `astro build` 1 page.

## Owner

- [ ] **M3 Windows manual pass** · Owner · REVIEW 2026-11-17 08:51 DECISION; `M3_ACCOUNT_SETTINGS.md` §8, §11 · medium · M3 is pushed but not accepted until this pass: three themes, font sizes, an OS light/dark switch on Match system, reload flash, keyboard use of the Settings dialog, and `scripts\test.cmd` on Windows (screenshots waived).
- [ ] **M4 Windows manual pass** · Owner · REVIEW 2026-11-18 00:49 RESOLUTION / DECISION; `M4_PERSONAS.md` §8, §9 · medium · M4 is accepted for push, but the owner still owes the Windows pass: create Jack, default vs this-chat, revert of name+body, deleted-persona status, inspector shows the live name and not the body, both themes.

## Next up

- [ ] **Independent verification of the 08:50 RESOLUTION** · MIRA Reviewer · REVIEW 2026-11-17 09:04 QUESTION / ADVICE · cheap · Confirm in `1bface7`: the 48 h cap in `DiagnosticsApiTests.cs` (7-day interval mutation must fail) and the save ordering in `settings.ts` / `settings.test.ts`. Doesn't block the manual pass.

## Backlog

- [ ] **Ephemeral conversation style** · Implementer · `STT3_HANDOFF.md` §4.3; `M4_PERSONAS.md` §10 · medium · Formality, verbosity and similar per-chat sliders are distinct from a saved persona; deferred from M4.
- [ ] **On-disk `whoami.md` editing** · Owner + Implementer · `STT3_HANDOFF.md` §13 item 6; `M4_PERSONAS.md` §10 · medium · M4 stores personas in SQLite only; still open whether files are also editable on disk and how external edits are versioned.
- [ ] **Real-model persona drafts** · Owner + Implementer · `M4_PERSONAS.md` §10 · medium · "Suggest a personality" is a deterministic template preview in M4; a live-model drafter needs an approved non-Mock provider (no new paid service in M4).
- [ ] **Safe Markdown preview (if ever added)** · Implementer + MIRA Security · REVIEW 2026-11-17 23:42; `M4_PERSONAS.md` §5, §6, §10 · medium · M4 shows raw text only; any rendered preview needs a sanitizer with no new dependency, or a threat model, before it ships.
- [ ] **Conversation-persona pin uses the SQLite store directly** · MIRA Boss · chunk A report 2026-11-18 · cheap · `PUT /api/conversations/{id}/persona` uses `SqlitePersonaStore` directly so conversation and persona ownership can be checked in one transaction without changing the frozen Core contract. Consider a Core contract for it later.
- [ ] **Mock gets the persona name via an extra interface** · MIRA Boss · chunk A report 2026-11-18 · cheap · `IPersonaAwareChatProvider` exists because `ChatRequest` has no name field. Revisit when a real provider is added.
- [ ] **personaStatus does an extra lookup per conversation** · MIRA Backend · chunk A report 2026-11-18 · cheap · `PersonaStatuses.ForAsync` does one extra persona lookup per single conversation response (the list does one per page). Fine now; join it into the conversation query if it gets slow.
- [ ] **Review draft template wording** · MIRA Security · chunk A report 2026-11-18; `M4_PERSONAS.md` §10 · cheap · `PersonaEndpoints.TemplateDraft` puts the user's own text into fixed Markdown. Review the wording once a real drafting model is approved.
- [ ] **Optional: inspector.ts event rows show allowlisted personaId/personaVersion** · MIRA Frontend · chunk C 2026-11-18 · cheap, optional · Inspector event rows could show allowlisted `personaId`/`personaVersion`, with the name resolved live or `(deleted)`.
- [ ] **Persona prompt fence hardening** · Implementer · REVIEW 2026-11-18 00:44 SECURITY REVIEW Nit 2 · cheap · Before a real provider ships: also strip DEL and C1 (U+007F–U+009F) and bidi/zero-width format characters (U+200B–U+200F, U+202A–U+202E, U+2066–U+2069); neutralize delimiters with `OrdinalIgnoreCase`; add tests. Harmless with the Mock.
- [ ] **picomatch ReDoS advisory GHSA-fc5q-39xc-vjpf via the Astro toolchain** · Implementer · REVIEW 2026-11-18 00:44 SECURITY REVIEW (pre-existing Minor, not M4) · cheap · Build- and dev-time only (astro@7.4.12 → tinyglobby/fdir, anymatch, unstorage). The shipped static output does not include picomatch; glob patterns are developer-controlled. Bump astro or add a picomatch `overrides` once a patched release exists, then re-audit. Never run `npm audit fix --force` (it would downgrade astro to 5.3.0).
- [ ] **Single-program mode: the API serves the built Astro frontend on 127.0.0.1:5080, optionally as a self-contained `dotnet publish` single executable (win-x64, osx-arm64, linux-x64)** · Owner + MIRA Boss · owner request 2026-11-17 22:34 MST · medium · Needs MIRA Security threat model first (static-file path containment, CSP/security headers, same-origin cookie and AllowedOrigins effects) and owner approval as a serving change; cross-platform also needs .sh scripts and documenting the non-Windows default DB path.
- [ ] **Strict CSP and security headers** · Implementer · REVIEW 2026-11-17 08:45 SECURITY REVIEW Nit 2; `M3_ACCOUNT_SETTINGS.md` §11 · cheap · All scripts are external now, so `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'` plus `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer` costs little and limits XSS impact.
- [ ] **Global 413 / bad-request problem JSON** · Implementer · REVIEW 2026-11-17 08:45 SECURITY REVIEW Nit 1 · cheap · `PUT /api/me/preferences` and the persona JSON routes (`PersonaEndpoints.ReadJsonAsync`, including `PUT /api/conversations/{id}/persona`) now return a fixed 413 problem. Other body-reading `/api` routes still show the Development exception page (stack trace) on an oversized body.
- [ ] **MIRA Dark contrast pass** · MIRA Frontend · REVIEW 2026-11-17 08:31 (Frontend) and 08:42 IMPLEMENTATION decision (a) · medium · Pre-existing WCAG AA misses (muted/warning/error text on the selected item, focus ring vs accent, input and panel borders, hover). Changes MIRA Dark's look, so it needs an owner-visible change.
- [ ] **Test: another user's delete leaves the owner's stream alone** · MIRA Backend · REVIEW 2026-11-16 23:06 VERIFICATION Minor · cheap · Safe by construction (foreign delete is 404 before any cancel) and checked live, but no automated test.
- [ ] **Test: provider ignores cancellation (5 s `DeleteWait`)** · MIRA Backend · REVIEW 2026-11-16 23:06 VERIFICATION Minor · cheap · The bounded wait and late slot release on delete aren't covered by a test.
- [ ] **User-delete cleanup** · Implementer · `M3_ACCOUNT_SETTINGS.md` §9, §11; `M4_PERSONAS.md` §10; REVIEW 2026-11-16 22:24 REVIEW (FKs are NO ACTION) · medium · If user deletion is ever added, delete the user's `UserPreferences`, personas, persona versions, `UserPersonaSettings`, conversations, messages and pipeline events with it (no cascade today).
- [ ] **In-process test for the 413 body limit** · MIRA Backend · `M3_ACCOUNT_SETTINGS.md` §11 · cheap, optional · The test server doesn't enforce Kestrel's 256 KB limit; only the curl smoke test covers the fixed 413.
- [ ] **Focus after selecting a deleted conversation** · MIRA Frontend · REVIEW 2026-11-16 22:34 VERIFICATION (Minor leftover) · cheap, optional · If a selected conversation turns out to be gone (404), focus isn't moved anywhere. Cosmetic.
- [ ] **Accessibility audit (screen reader, visual/layout)** · Implementer · M0-M1 carry-over (REVIEW 2026-11-16 21:50 MILESTONE CLOSE); `M2_PERSISTENCE_AND_INSPECTOR.md` §10 · medium · No screen-reader or visual check has been run; planned for hardening (MIRA M9; was handoff M8).
- [ ] **Backup, encryption at rest and password reset** · Owner + Implementer · `STT3_HANDOFF.md` §13 item 3; `M2_PERSISTENCE_AND_INSPECTOR.md` §10 · medium · Message text is stored unencrypted in the local SQLite file; agree on backup/encryption and reset before real private data.

## Done

- [x] **Persona-route 413** · MIRA Boss · REVIEW 2026-11-18 00:44 SECURITY Nit 1; 00:40 REVIEW Minor 2 · `PersonaEndpoints.ReadJsonAsync` catches `BadHttpRequestException` (413) and returns a fixed "The request body is too large." problem, with a test that the body has no exception details. The global 413 item remains for other routes.
- [x] **Touch up README Configuration table** · MIRA Boss · owner question 2026-11-17 20:48 MST · Chunk C (2026-11-18): `Mira:ProviderMode` note is M1–M4 Mock-only; `Mira:DatabasePath` now lists conversations, inspector events, preferences and personas.
- [x] **Refresh stale status lines** · MIRA Boss · `README.MD` §Status/§Project status; `M2_PERSISTENCE_AND_INSPECTOR.md` header · Chunk C (2026-11-18): README now has M2 accepted, M3 pushed pending the owner's Windows manual pass, and M4 implemented awaiting implementation review. The M3 manual pass itself remains open under Owner.

- [x] **Re-plan the roadmap numbering** · Owner + MIRA Boss · `STT3_HANDOFF.md` §10 vs `M3_ACCOUNT_SETTINGS.md` · Owner confirmed 2026-11-17 23:39 MST: personas is M4, memory M5, STT M6, TTS M7, Tavily M8, hardening M9. Short note added at the top of handoff §10; plan in `docs/M4_PERSONAS.md`.
- [x] **`ApiHostTests` LAN-exposure nit (`0.0.0.0:5081`)** · M0-M1 carry-over (archive "2026-10-16 23:20 MST | Claude | VERIFICATION") · The real-process test now uses `http://127.0.0.2:5081` (`ApiHostTests.cs:118`); the remaining `0.0.0.0:5081` cases (lines 49-50) run in-process on the test server and never open a socket.
- [x] **Retention test guards the 24 h interval; ordered settings saves** · REVIEW 2026-11-17 08:46 REVIEW Minors · Fixed in `1bface7` (08:50 RESOLUTION); independent check is under Next up.
- [x] **Migration 4 hardening** (`WITHOUT ROWID`, length `CHECK`s) · REVIEW 2026-11-17 08:22 SECURITY REVIEW Nit 2 · In `MiraDatabase.cs` migration 4.
