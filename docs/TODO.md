# MIRA TODO and follow-ups

The durable list of open follow-ups, so nothing is lost when `REVIEW.md` is archived at a milestone close (owner request, 2026-11-17 09:06 MST).

**Rules**
- Any agent that defers something (a finding, a nit, a test gap, a follow-up) adds an item here in the same change.
- Tick an item (`[x]`) and move it to **Done** with a short "how/where" when it is finished; never delete open items.
- Milestone archiving never drops open items: they stay here (and in the carry-over list).
- Each item: owner role · source (REVIEW entry timestamp or doc §) · size (cheap / medium) · why.
- This file is a list, not a log: evidence stays in `REVIEW.md`.

Last verified against the code: 2026-11-17 (`main` = `1bface7`).

## Owner

- [ ] **M3 Windows manual pass** · Owner · REVIEW 2026-11-17 08:51 DECISION; `M3_ACCOUNT_SETTINGS.md` §8, §11 · medium · M3 is pushed but not accepted until this pass: three themes, font sizes, an OS light/dark switch on Match system, reload flash, keyboard use of the Settings dialog, and `scripts\test.cmd` on Windows (screenshots waived).
- [ ] **Re-plan the roadmap numbering** · Owner + MIRA Boss · `STT3_HANDOFF.md` §10 (M3 = personas) vs `M3_ACCOUNT_SETTINGS.md` · cheap · The handoff's M3 is the persona system, but M3 became account settings; decide where personas go so later milestones aren't mislabelled.

## Next up

- [ ] **Independent verification of the 08:50 RESOLUTION** · MIRA Reviewer · REVIEW 2026-11-17 09:04 QUESTION / ADVICE · cheap · Confirm in `1bface7`: the 48 h cap in `DiagnosticsApiTests.cs` (7-day interval mutation must fail) and the save ordering in `settings.ts` / `settings.test.ts`. Doesn't block the manual pass.
- [ ] **Refresh stale status lines after the manual pass** · MIRA Boss · `README.MD` §Status/§Project status ("M3 … in review"); `M2_PERSISTENCE_AND_INSPECTOR.md` header ("browser pass not run") · cheap · M2 is accepted and M3 is pushed; the docs still describe the earlier state.

## Backlog

- [ ] **Strict CSP and security headers** · Implementer · REVIEW 2026-11-17 08:45 SECURITY REVIEW Nit 2; `M3_ACCOUNT_SETTINGS.md` §11 · cheap · All scripts are external now, so `default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'` plus `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer` costs little and limits XSS impact.
- [ ] **Global 413 / bad-request problem JSON** · Implementer · REVIEW 2026-11-17 08:45 SECURITY REVIEW Nit 1 · cheap · Only `PUT /api/me/preferences` maps `BadHttpRequestException`; other body-reading `/api` routes still show the Development exception page (stack trace) on an oversized body.
- [ ] **MIRA Dark contrast pass** · MIRA Frontend · REVIEW 2026-11-17 08:31 (Frontend) and 08:42 IMPLEMENTATION decision (a) · medium · Pre-existing WCAG AA misses (muted/warning/error text on the selected item, focus ring vs accent, input and panel borders, hover). Changes MIRA Dark's look, so it needs an owner-visible change.
- [ ] **Test: another user's delete leaves the owner's stream alone** · MIRA Backend · REVIEW 2026-11-16 23:06 VERIFICATION Minor · cheap · Safe by construction (foreign delete is 404 before any cancel) and checked live, but no automated test.
- [ ] **Test: provider ignores cancellation (5 s `DeleteWait`)** · MIRA Backend · REVIEW 2026-11-16 23:06 VERIFICATION Minor · cheap · The bounded wait and late slot release on delete aren't covered by a test.
- [ ] **User-delete cleanup** · Implementer · `M3_ACCOUNT_SETTINGS.md` §9, §11; REVIEW 2026-11-16 22:24 REVIEW (FKs are NO ACTION) · medium · If user deletion is ever added, delete the user's `UserPreferences`, conversations, messages and pipeline events with it (no cascade today).
- [ ] **In-process test for the 413 body limit** · MIRA Backend · `M3_ACCOUNT_SETTINGS.md` §11 · cheap, optional · The test server doesn't enforce Kestrel's 256 KB limit; only the curl smoke test covers the fixed 413.
- [ ] **Focus after selecting a deleted conversation** · MIRA Frontend · REVIEW 2026-11-16 22:34 VERIFICATION (Minor leftover) · cheap, optional · If a selected conversation turns out to be gone (404), focus isn't moved anywhere. Cosmetic.
- [ ] **Accessibility audit (screen reader, visual/layout)** · Implementer · M0-M1 carry-over (REVIEW 2026-11-16 21:50 MILESTONE CLOSE); `M2_PERSISTENCE_AND_INSPECTOR.md` §10 · medium · No screen-reader or visual check has been run; planned for M8.
- [ ] **Backup, encryption at rest and password reset** · Owner + Implementer · `STT3_HANDOFF.md` §13 item 3; `M2_PERSISTENCE_AND_INSPECTOR.md` §10 · medium · Message text is stored unencrypted in the local SQLite file; agree on backup/encryption and reset before real private data.

## Done

- [x] **`ApiHostTests` LAN-exposure nit (`0.0.0.0:5081`)** · M0-M1 carry-over (archive "2026-10-16 23:20 MST | Claude | VERIFICATION") · The real-process test now uses `http://127.0.0.2:5081` (`ApiHostTests.cs:118`); the remaining `0.0.0.0:5081` cases (lines 49-50) run in-process on the test server and never open a socket.
- [x] **Retention test guards the 24 h interval; ordered settings saves** · REVIEW 2026-11-17 08:46 REVIEW Minors · Fixed in `1bface7` (08:50 RESOLUTION); independent check is under Next up.
- [x] **Migration 4 hardening** (`WITHOUT ROWID`, length `CHECK`s) · REVIEW 2026-11-17 08:22 SECURITY REVIEW Nit 2 · In `MiraDatabase.cs` migration 4.
