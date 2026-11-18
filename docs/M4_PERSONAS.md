# M4: persona system

Status: **planned; MIRA Reviewer's 00:10 MST re-check is PASS (no open Blocker or Major; Majors 1–2 closed). Chunk 0 may start.** The owner chose personas as M4 on 2026-11-17 at 23:22 MST, confirmed all six recommended defaults at 23:39 MST, and answered the plan-review questions on 2026-11-18 at 00:05 MST, all directly to MIRA Boss. MIRA Security's plan threat model (23:43) is **PASS** with Minors folded in below.

Constraints (AGENTS.md): Astro static + vanilla TypeScript, no new dependencies or paid services; .NET 10 Minimal APIs; plain SQLite with the hand-written migration runner (ADR 0002); API bound to 127.0.0.1:5080; default-deny auth and governance; CSRF on unsafe methods; owner ID from the principal only; parameterized SQL; render user data with `textContent` only. Persistent persona edits by *development agents* need the human owner's direct approval. A persona must never weaken governance.

## Owner decisions (confirmed 2026-11-17 23:39 MST; amended 2026-11-18 00:05 MST)

1. **Definition.** One built-in preset (**MIRA**, fixed id `PersonaIds.BuiltInMira` = `00000000-0000-0000-0000-000000000001`) plus user-created personas. Copy MIRA to customize (the built-in text is not overwritten in place). Each persona has a name, an optional short description, and a Markdown body (the `whoami.md` identity / style / boundaries text). No separate trait schema in M4; tone and traits are written in the body. SQLite is the source of truth; no on-disk user `whoami.md` in M4.
2. **Assignment (pinned chats, 00:05 MST).** A per-user **default persona** (the built-in MIRA until changed) for new chats, changeable per conversation. A conversation is **pinned** to a concrete persona id: `POST /api/conversations` and the M1 `POST /api/chat/stream` get-or-create path both *copy* that default. Changing the default does not rewrite existing chats. `PersonaId` is never "follow the default". Migration 5 backfills pre-M4 conversations to the built-in MIRA id. When a persona is deleted, stored messages stay unchanged and the UI reports **Persona no longer exists** (the conversation keeps its stored `PersonaId` as a soft pin, with no FK cascade). New turns in that chat use the built-in MIRA, and the UI says so. A persona is a system/instruction message for the turn, not a stored chat role.
3. **Confirmation, versioning and AGENTS.md (amended 00:05 MST).** An explicit **Save** for every persistent write (a model or mock draft is never auto-saved). Each version stores **name, description and body**; revert restores all three. Keep at most **5** versions per persona (`PersonaLimits.MaxVersionsPerPersona = 5`, one constant, raisable later), pruning the oldest on save. End users may create, edit, revert, and delete *their own* personas; delete and revert also need confirm. AGENTS.md's "persistent persona edits need owner approval" applies to *development agents* changing shipped defaults or repo files, not to a user editing their own local personas. No reauthentication for user personas (that remains a governance-editor rule).
4. **Limits.** Max **20** user-created personas per account (`PersonaLimits.MaxUserPersonas`); name 1–80 characters; description 0–200; body 1–8,000 characters; at most 5 versions (decision 3). Markdown is stored and injected as plain text only: no file includes, no path loading, no HTML rendering of the body in the UI (preview is the raw text).
5. **Inspector (amended 00:05 MST / reviews).** Show only persona **id**, **name**, and **version**. Never the body or description. Pipeline events carry `personaId` and `personaVersion` only (no name). The inspector resolves the name live through an owner-scoped lookup, or shows `(deleted)` if the persona is gone.
6. **Mock provider and drafts.** Stay on Mock only (no paid chat API). Prompt assembly inserts the selected persona body as an instruction/system channel (tested in unit tests). Stored messages can never carry a system role. The mock's visible reply is deterministic and reflects the persona, e.g. `MIRA mock (Jack) response to "…". No model was called.` **Suggest a personality** is a deterministic template preview from the user's description (`POST /api/personas/draft` returns a preview, writes nothing) until a real model is approved.

## 1. Decisions

| Topic | Decision (owner, 23:39 MST, amended 00:05 MST) |
|---|---|
| Built-in | Seeded **MIRA** persona at fixed id `PersonaIds.BuiltInMira`, `OwnerUserId` null: helpful, candid, concise; no fabricated actions or memories. Not deletable; not edited in place. Users copy it to customize. `PUT`, `DELETE` and revert on it return a fixed 403/409 *before* any body parsing. A create/update body that includes `isBuiltIn` or `ownerUserId` is an unknown-key 400. The API cannot create a second built-in row. |
| User personas | Owner-scoped rows; create / edit / revert / delete in a Personas panel (sidebar entry + editor dialog). Writes filter `OwnerUserId = $owner` only (never `OR OwnerUserId IS NULL`). Reads may include the built-in. |
| Default vs chat | Default via `PUT /api/personas/default`. New chats (create *and* stream get-or-create) copy that id onto `Conversations.PersonaId`. Pre-M4 rows are backfilled to built-in MIRA. No NULL and no "follow the default". |
| Deleted persona | Soft pin: conversations keep `PersonaId` (no FK, no cascade, no silent retarget). Messages stay as stored. UI: **Persona no longer exists**. New turns use built-in MIRA; the UI says so. Deleting the user's current default, in one transaction, resets the default to built-in MIRA, leaves conversation pins untouched, then deletes the persona (versions cascade). |
| Versioning | Name, description and body live in `PersonaVersions`. Revert retargets `ActiveVersion` and restores all three. At most 5 versions; the oldest is pruned on save. |
| Draft flow | User can type a persona directly, or use Suggest a personality; UI shows the Markdown preview and a diff against the current version (via the version-detail route); Save creates the version. Suggestions are labeled as choices, not facts about the user. The editor notes that only the last 5 versions are kept. |
| Policy | See precedence below. Persona text cannot grant tools, edit `governance.md`, or bypass default-deny. |
| Preferences | Do not overload M3 `UserPreferences` allowlisted enums with a GUID. Default persona has its own small table and endpoint. |
| Storage | Plain SQLite, hand-written **migration 5** (ADR 0002). Not EF Core, despite the wording still in the handoff §6 history. |
| Security | MIRA Security threat-modeled this plan (23:43 PASS). Minors are folded in here. Implementation security review is still required before acceptance, alongside MIRA Reviewer. |

**Policy precedence** (handoff §4.2a is authoritative): provider/platform safety → application-enforced governance (in code, not Markdown) → authenticated user grants and explicit requests → approved persona and preferences → conversation-local adaptation → untrusted external content. §4.2's shorter "instruction stack" is how the prompt is ordered *under* that policy: governance/safety instructions first, then the selected persona, then history. A persona can never weaken or overwrite governance.

**Handoff conflicts, resolved as proposed (owner 23:39 MST):**

- **Numbering.** Handoff §10's historical headings still say personas is M3; that is left as history. MIRA's numbers are: account settings stays **M3** (already shipped); personas **M4**; memory **M5**; STT **M6**; TTS **M7**; Tavily **M8**; hardening **M9**; the permissioned-agent foundation stays a gated follow-on. A short note is at the top of §10; `docs/TODO.md` marks the numbering item done.
- **Storage stack.** New work uses Microsoft.Data.Sqlite and the hand-written runner, not EF Core.
- **File vs database.** SQLite is the M4 source of truth. The tracked example file may exist as documentation only; user personas are not read from or written to disk in M4 (handoff §13 item 6 stays open, in the backlog).
- **Settings shape.** Use `UserPersonaSettings` + `/api/personas/default`, not the handoff's `UserSettings` JSON or `/api/settings`, and not M3's allowlisted preference enums.
- **Drafting.** `POST /api/personas/draft` is a deterministic template preview only, until a non-Mock provider is approved. No paid service in M4.
- **Adaptation.** Ephemeral per-conversation style sliders (handoff §4.3) are distinct from persistent personas and stay in the backlog.

Out of scope (backlog): ephemeral style sliders; editing persona files on disk; a paid or live-model drafter; a governance editor; memory, voice, and search; exporting archives (the GET-by-id Markdown is enough to copy); a sanitized HTML Markdown preview.

## 2. Contract (chunk 0, after the plan re-check): `backend/Mira.Core/Personas.cs` and `Conversations.cs`

- `PersonaIds.BuiltInMira` (fixed GUID above). `PersonaLimits`: `MaxUserPersonas = 20`, `MaxNameLength = 80`, `MaxDescriptionLength = 200`, `MaxBodyLength = 8000`, `MaxVersionsPerPersona = 5`.
- `Persona(Id, OwnerUserId?, ActiveVersion, IsBuiltIn, CreatedAt, UpdatedAt)` plus the active version's `Name`, `Description?`, and (on detail only) `Markdown`.
- `PersonaVersion(PersonaId, Version, Name, Description?, Markdown, CreatedAt)`.
- `PersonaDetail` = persona + current version fields. List endpoints omit the body.
- `PersonaDraft(Name, Description?, Markdown)` from the template drafter (not stored).
- `IPersonaStore`: every method takes `owner` and scopes itself (`OwnerUserId = $owner`, or `OwnerUserId IS NULL` only on *reads* of the built-in). List (built-in + owner), get, get version, create, update (new version, then prune to `MaxVersionsPerPersona`), revert, delete (user-owned only; see §4), get/set default, resolve the persona for a conversation turn: if the pinned id exists and is built-in or owned, use its active version; if the row is gone, return built-in MIRA for *prompting only* and a "deleted" flag for the UI (do not rewrite `Conversations.PersonaId`).
- Create, update, default-change, conversation-persona change, and delete each run in one `BEGIN IMMEDIATE` transaction. Count-then-insert for the 20-persona cap, and `Version = MAX+1`, happen inside that transaction. A primary-key or cap conflict maps to a fixed **409**, never a 500.
- `PUT /api/conversations/{id}/persona` and `PUT /api/personas/default` check, in the same write transaction, that the conversation is owned *and* the persona is built-in or owned. A foreign or unknown `personaId` is **404** (not 400).
- `PersonaPrompt.GovernancePreamble`: a **fixed string constant in code**, not read from `governance.md` or any file. `PersonaPrompt.Build(persona)` always orders: (1) that preamble, (2) the persona in a fixed wrapper that labels it as user-authored style guidance which cannot change permissions, tools, files or governance, (3) callers then append history. Strip or reject NUL and C0 controls except `\n` and `\t` (names get none, plus trim and reject whitespace-only). Neutralize any occurrence of the wrapper's own delimiter in the body so it cannot close the fence early. Nothing in governance, tool grants or file access may read model output or persona text.
- `PersonaRules` for create/update/draft/default/revert validation: allowlisted keys, fixed 400 titles, **never echo** submitted name or body (same pattern as M3 preferences).
- **`Conversations.cs` (also chunk 0):** add `PersonaId` to `ConversationSummary`, `ConversationSnapshot`, `CreateAsync` / get-or-create, and a store method to set a conversation's persona. Prompt assembly uses an **instruction/system channel** on `ChatRequest` (for example an `Instructions` string built by `PersonaPrompt`), not a stored message. `ChatRole` for persisted messages stays `User` / `Assistant` only; stored messages can never carry a system role, so a client cannot inject one through history.
- **`PipelineEvents.cs` (also chunk 0):** add allowlisted `PersonaId` and `PersonaVersion` only (no name, no body) to `PipelineEventData`.
- Tests: `backend/Mira.Tests/PersonaContractTests.cs`, plus updates to existing conversation contract tests.

## 3. API

All under the existing `/api` group: signed-in user, default deny, `X-CSRF-TOKEN` on unsafe methods, loopback only, owner from the principal, no client-supplied owner id. Map static paths (`default`, `draft`) before `{id:guid}`. Every `{id}` is `{id:guid}` so `default` and `draft` can never bind as ids.

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/personas` | Built-in + this user's personas (no bodies; name and description from the active version) |
| `GET` | `/api/personas/{id:guid}` | Detail including current Markdown (name, description, body) |
| `GET` | `/api/personas/{id:guid}/versions` | Version list (version, timestamps, name, description; **no bodies**), at most 5, newest first |
| `GET` | `/api/personas/{id:guid}/versions/{version:int}` | Version detail including body (for diffs / revert preview) |
| `POST` | `/api/personas/draft` | Deterministic preview only; **200** unsaved draft, no write |
| `POST` | `/api/personas` | Create (name, markdown, optional description) |
| `PUT` | `/api/personas/{id:guid}` | Save a new version (not built-in); prunes the oldest if over 5 |
| `POST` | `/api/personas/{id:guid}/revert` | `{ "version": n }` retargets `ActiveVersion` (restores name, description, body) |
| `DELETE` | `/api/personas/{id:guid}` | Delete a user persona (see §1 Deleted persona) |
| `GET`/`PUT` | `/api/personas/default` | The owner's default persona id |
| `PUT` | `/api/conversations/{id:guid}/persona` | `{ "personaId": "…" }` for future turns (must be built-in or owned) |

**400** fixed problem titles (key named only if allowlisted; never a submitted value). **404** for another user's id, an unknown id, or a missing version (no existence leak), including version list, version detail and revert. **403** without CSRF, **401** signed out. **403/409** (fixed) for `PUT`, revert or `DELETE` on the built-in, decided *before* reading the body. **409** for a cap or version-number conflict, and not used to mean "cannot delete the default" (that case resets the default and returns **204**). Persona bodies, names and descriptions are not logged; `ILogger` may record `personaId` and version only. The draft endpoint's input and preview are never logged. Existing chat and conversation routes gain `personaId` on snapshots/summaries; message text is unchanged. Conversation responses may include a `personaStatus` of `active` or `deleted` (no persona body).

## 4. Data: migration 5

Append-only; migrations 1–4 unchanged. `LatestVersion` becomes 5.

```sql
CREATE TABLE Personas (
    Id            TEXT NOT NULL PRIMARY KEY,
    OwnerUserId   TEXT NULL REFERENCES Users(Id),  -- NULL = built-in
    ActiveVersion INTEGER NOT NULL,
    CreatedAt     TEXT NOT NULL,
    UpdatedAt     TEXT NOT NULL
);
CREATE TABLE PersonaVersions (
    PersonaId    TEXT NOT NULL REFERENCES Personas(Id) ON DELETE CASCADE,
    Version      INTEGER NOT NULL,
    Name         TEXT NOT NULL CHECK (length(Name) BETWEEN 1 AND 80),
    Description  TEXT NULL CHECK (length(Description) <= 200),
    Markdown     TEXT NOT NULL CHECK (length(Markdown) BETWEEN 1 AND 8000),
    CreatedAt    TEXT NOT NULL,
    PRIMARY KEY (PersonaId, Version)
);
CREATE TABLE UserPersonaSettings (
    OwnerUserId      TEXT NOT NULL PRIMARY KEY REFERENCES Users(Id),
    DefaultPersonaId TEXT NOT NULL REFERENCES Personas(Id),
    UpdatedAt        TEXT NOT NULL
);
-- Soft pin: no REFERENCES, so a later persona delete can leave the id in place.
ALTER TABLE Conversations ADD COLUMN PersonaId TEXT NOT NULL
    DEFAULT '00000000-0000-0000-0000-000000000001';
```

Seed the built-in MIRA row (that fixed id) and version 1 in the same migration. The `DEFAULT` backfills pre-M4 conversations to built-in MIRA (also write an explicit `UPDATE` for clarity). `PRAGMA foreign_keys` remains on for store connections; the conversation pin is intentionally not an FK. Parameterized SQL; owner filters everywhere.

## 5. Frontend behaviour

- Sidebar: a **Personas** control (list + New). The chat header (or inspector bar) shows the **current conversation's** persona name and a picker. If the pinned persona row is gone: show **Persona no longer exists**. When the user sends in that chat, show that new turns use the built-in MIRA (do not silently retarget the stored pin).
- Editor: accessible dialog; name, description, body in a `<textarea>`; Save/Cancel; Revert to version *n* and Delete need a confirm step. Template drafts land in the editor and still need Save. Note that only the last 5 versions are kept. Diffs use `GET /api/personas/{id}/versions/{version}`.
- Settings: "Default persona" (select), distinct from theme/font. Failed `PUT`s revert and `role="alert"`, like M3.
- All persona fields use `textContent` / form values only: no `innerHTML`, no Markdown-to-HTML preview in M4.
- Picker change calls `PUT /api/conversations/{id}/persona` and is visible on the next send.
- Inspector: show id and version from the event, and resolve the name live (or `(deleted)`).

## 6. Security (for the MIRA Security threat model)

Assets: persona bodies (private style instructions), names, descriptions, default id, conversation-to-persona pins.

Threats and invariants (plan review 23:43, folded here):

- **Prompt injection** in a user-written body. `PersonaPrompt` puts governance first, then a fenced, labeled, lower-precedence persona slot, then history. Delimiter neutralization; NUL/C0 stripping. It must not write files, change governance, grant tools, or escape the chat provider. GovernanceService stays default-deny and is not consulted "by prompt." Nothing in governance, grants or file access reads model output or persona text.
- **Cross-user / version-level IDOR.** Every version list, version detail, revert, and turn-time body lookup joins `Personas` and filters `OwnerUserId = $owner` (built-in reads only: `OwnerUserId IS NULL`). Cross-user is 404.
- **Write-path ownership** is checked inside the same `BEGIN IMMEDIATE` transaction as the write (conversation owned *and* persona built-in or owned). Foreign or unknown ids are 404.
- **Delete consistency.** Reset the owner's default to built-in MIRA when needed, leave conversation pins in place, then delete (versions cascade). No FK error, no dangling default.
- **Oversized or hostile bodies:** reject or strip in `PersonaRules` / `PersonaPrompt`; 413 already exists for oversized HTTP bodies.
- **Inspector and logs** must not contain the body, name or description. Events: `personaId` + `personaVersion` only. Logs: `personaId` (and version) only.
- **XSS:** M4 avoids HTML rendering of Markdown.
- **Built-in integrity:** fixed id; 403/409 before body parsing; no second built-in; writes never use `OR OwnerUserId IS NULL`.
- **Draft endpoint** must not persist or have side effects; Mock-only, no outbound provider call; do not log its input or output.
- **Races:** cap and version increment in `BEGIN IMMEDIATE`; conflicts are 409.

## 7. Chunks and file ownership

One owner per file. If this file and the M4 INTENT / RESOLUTION in `REVIEW.md` disagree, the RESOLUTION wins; ask MIRA Boss. Chunk 0 may start (MIRA Reviewer's 00:10 MST re-check is PASS, no open Blocker or Major). A and B then run in parallel against the frozen contract.

| Step | Owner | Files (create or edit) | Depends on |
|---|---|---|---|
| **Plan** | MIRA Boss | this doc, handoff §10 note, `docs/TODO.md`, `REVIEW.md` INTENT + RESOLUTION | owner 23:39 and 00:05 MST |
| **Security: plan threat model** | MIRA Security | `REVIEW.md` (`SECURITY REVIEW`, 23:43 PASS) | Plan |
| **Plan review** | MIRA Reviewer | `REVIEW.md` (`REVIEW`, 23:42) then a short re-check of this revision | Plan, RESOLUTION |
| **0. Contracts** | MIRA Boss | `Personas.cs`, `Conversations.cs` (`PersonaId`, instruction channel, no stored system role), `PipelineEvents.cs` (`PersonaId`, `PersonaVersion`), contract tests, this plan if they must freeze a change | plan re-check, no open Blocker/Major |
| **A. Backend** | MIRA Backend | see brief below | 0, threat model |
| **B. Frontend** | MIRA Frontend | see brief below | 0, threat model |
| **C. Integration** | MIRA Boss | `README.MD`, this plan (final), `REVIEW.md` IMPLEMENTATION; full clean run; curl smoke | A, B |
| **Review** | MIRA Reviewer + MIRA Security | `REVIEW.md` only | C |

### Every bot, before starting

1. Read `AGENTS.md`, `docs/agent-sync/AGENT_SYNC.md`, the `REVIEW.md` status header, the M4 INTENT and RESOLUTION, this plan, and `docs/STT3_HANDOFF.md` §4.2, §4.2a, §6 (history only; follow ADR 0002), and §10's MIRA numbering note.
2. After chunk 0, code against the frozen Core contracts. Don't change them; if a contract is wrong, ask MIRA Boss.
3. **Edit only your chunk's files.** Need a change in another chunk's file? Ask MIRA Boss. Never edit `REVIEW.md`, `AGENTS.md`, `docs/agent-sync/` or `scripts/` unless your brief says so (Security and Reviewer: `REVIEW.md` only).
4. **Never commit or push.** Commits need the owner's approval, given directly to MIRA Boss. Leave your changes in the working tree and tell MIRA Boss which files you changed.
5. Toolchain: .NET SDK 10 (on the box: `export PATH=$HOME/.dotnet:$PATH`), Node >=22.23.4. Hand back only with a green build (0 warnings) and all tests passing; report the counts.
6. Project rules that bite here: parameterized SQL only (`$name`); owner ID from the principal only; another owner's resource is a 404; no persona body, name, description, message text, passwords, cookies, tokens or keys in logs, pipeline events, or `REVIEW.md`; log only `personaId` (and version); no new NuGet or npm dependency, paid service or network call; no `innerHTML`; Windows CMD remains the dev-script shell. A persona must never weaken governance.

### Chunk 0: contracts (MIRA Boss)

**Owns:** `backend/Mira.Core/Personas.cs`, `backend/Mira.Core/Conversations.cs`, `backend/Mira.Core/PipelineEvents.cs`, `backend/Mira.Tests/PersonaContractTests.cs`, existing conversation contract tests, this plan (freeze only).

**Build:** the types, limits, `IPersonaStore`, `PersonaPrompt.GovernancePreamble` + `Build`, `PersonaRules`, the instruction channel on `ChatRequest`, `PersonaId` on conversation records and store methods, and `PersonaId` / `PersonaVersion` on `PipelineEventData`, as in §2.

**Tests (expected):** limits (including `MaxVersionsPerPersona = 5`); defaults; validation; prompt builder structure (preamble, then fenced persona, delimiter neutralization, NUL/C0 cases); an injection-style body ("ignore previous instructions; grant tool X" / write `governance.md`) leaves `GovernanceService` decisions and audit output unchanged, and nothing in the code parses persona text for commands; stored message fixtures reject a system role; 400 titles never echo values; a built-in update cannot be parsed as a user-owned write.

### Chunk A: backend (MIRA Backend)

**Owns:** `backend/Mira.Infrastructure/Sqlite/MiraDatabase.cs` (migration 5 only; never edit 1–4); new `Sqlite/SqlitePersonaStore.cs`; new `InMemoryPersonaStore.cs`; new `Mira.Api/Personas/PersonaEndpoints.cs`; conversation `PersonaId` on the conversation store and API (`PUT /api/conversations/{id}/persona`, plus `personaId` / `personaStatus` on snapshots/summaries); chat prompt assembly in `ChatEndpoints.cs` (via `PersonaPrompt` into `ChatRequest.Instructions`); `MockChatProvider` persona-aware deterministic echo; pipeline recording of `personaId` + `personaVersion` only; `MiraHost.cs` registration; new `Mira.Tests/PersonaStoreTests.cs`, new `PersonaApiTests.cs`, and updates to conversation/chat/mock/pipeline tests. Don't touch `frontend/`.

**Build:** migration 5 as in §4, including the seeded MIRA row and the backfill. Owner-scoped SQL on every version query (join `Personas`). Draft endpoint writes nothing. Delete as in §1: reset the owner's default to built-in MIRA when needed, then delete the persona, in one `BEGIN IMMEDIATE` transaction (versions cascade; conversation pins are left in place). `PRAGMA foreign_keys` is 1 on store connections. Cap and version increment use `BEGIN IMMEDIATE`; conflicts are 409. Built-in `PUT` / revert / `DELETE` reject with 403/409 before body parsing.

**Tests (expected):**
- Migration 5 on a fresh DB and on a v4 DB with conversations intact (those rows have the built-in id); idempotent; `PRAGMA foreign_keys` is 1.
- CRUD, versioning of name+description+body, revert restores all three; prune to 5 (oldest gone, active kept).
- Default and per-chat pins; create and stream get-or-create both copy the default; changing the default leaves existing pins.
- Delete of the current default returns 204 and the default becomes MIRA; conversations that used it keep their `PersonaId`; a following turn prompts with built-in MIRA and does not rewrite the pin.
- A dangling pin (the persona row is already gone) resolves to built-in MIRA at turn time without rewriting the stored `PersonaId`, and the conversation reports `personaStatus: deleted`.
- Delete of a persona used by ≥1 conversation: 204, no dangling default, pins kept, messages intact.
- 20-persona cap; a 21st create is 409/400; two parallel saves get distinct versions or one 409, never a 500; a concurrent-create test does not reach 21.
- Cross-user 404 on version list, version detail, revert, `PUT /api/personas/default` (foreign or unknown id), and `PUT /api/conversations/{id}/persona` in both directions (foreign conversation + my persona, and my conversation + foreign persona).
- Revert to a nonexistent version: fixed 400/404, no echo.
- GET of the built-in returns its text; `PUT`, revert and `DELETE` on the built-in are rejected before body parsing; create cannot add a second built-in (`isBuiltIn` / `ownerUserId` keys are unknown-key 400).
- CSRF/401; draft does not write and is not logged; mock output includes the persona name; prompt contains the fenced body after the preamble; a hostile body, name, description or draft input does not appear in logs or pipeline payloads (extend the M3 log-marker tests); a turn's events contain `personaId` / `personaVersion` and no body or name.

### Chunk B: frontend (MIRA Frontend)

**Owns:** `frontend/src/pages/index.astro` (Personas list/editor/picker markup and inspector id/version + live name or `(deleted)`); new `frontend/src/scripts/personas.ts` + `personas.test.ts`; `frontend/src/scripts/api.ts` (types and calls); `frontend/src/scripts/chat.ts` and settings wiring for the default-persona select and the "Persona no longer exists" / "new turns use MIRA" status. Don't touch `backend/`. No `innerHTML`, `insertAdjacentHTML` or `set:html`.

**Build:** the UI in §5, against the API in §3 (mock `fetch` in tests until A lands). Save, revert, and delete require confirm. A failed PUT reverts and shows `role="alert"`.

**Tests (expected):** editor save/confirm/revert/delete; failed PUT reverts; picker calls `PUT /api/conversations/{id}/persona`; deleted-pin status copy; `textContent` / form values only; list and inspector do not render raw Markdown as HTML and do not take a name from the event payload; `astro check` 0/0/0.

### Chunk C: integration (MIRA Boss)

**Owns:** `README.MD`, this plan (final "as built" notes), `REVIEW.md` (one IMPLEMENTATION entry).

**Build:** wire any leftover host/docs gaps, full clean run, curl smoke of create, get, version detail, default, 400, 403, and 409.

## 8. Acceptance criteria

1. The user can create "Jack, sales representative" with sarcasm/affability in the body, confirm, and see it in the picker. Copying the built-in MIRA is the path to customize it.
2. A draft/suggestion is previewed and is not stored until Save; revert restores an earlier name, description and body; only 5 versions are kept; delete is confirmed and cannot remove the built-in MIRA.
3. New chats (including stream get-or-create) use a *copy* of the default persona; an existing chat keeps its own pin when the default changes; the next mock reply includes that persona's name. A chat whose persona was deleted still shows its old messages, reports **Persona no longer exists**, and uses built-in MIRA for new turns with the UI saying so (the stored pin is not silently rewritten).
4. Prompt assembly uses a fixed in-code `GovernancePreamble`, then the persona in a fenced, labeled slot, then history; delimiter, NUL and control-character cases are tested. An injection-style body leaves `GovernanceService` decisions and audit output unchanged; nothing parses persona text for commands. Stored messages never have a system role.
5. Inspector and pipeline events never show persona Markdown, name or description. Events carry `personaId` and `personaVersion` only; the name is resolved live or shown as `(deleted)`.
6. API: CSRF, 401, 404 cross-user (including version list, version detail and revert), fixed 400s that never echo values, owner from the principal, logs only `personaId` (and version). Two users are isolated. Deleting the current default resets it to MIRA without error (204). Migration 5 is idempotent on an M3 (version 4) database and backfills `PersonaId` to the built-in MIRA id.
7. M1–M3 behaviour is unchanged; the full suite passes.
8. No open Blocker or Major from MIRA Reviewer or MIRA Security; owner manual pass on Windows (create Jack, default vs per-chat, revert, deleted-persona status, inspector check).

## 9. Tests

- **Contract:** see chunk 0.
- **Backend (xUnit):** see chunk A.
- **Frontend (Vitest):** see chunk B.
- **Whole suite (C):** `npm ci`, Vitest, `astro check`, `astro build`, `dotnet build --no-incremental`, `dotnet test` twice; curl smoke (create, get, version detail, default, 400, 403, 409).
- **Manual (owner, Windows):** Jack persona, default vs this-chat, revert of name+body, delete-in-use status, inspector shows live name not body, both themes.

## 10. Backlog

The live, maintained list is [`docs/TODO.md`](TODO.md); the items below are the M4 snapshot of what this plan defers:

- Ephemeral conversation style (formality, verbosity) distinct from a saved persona (handoff §4.3).
- On-disk `whoami.md` editing and external-edit versioning (handoff §13 item 6).
- Real-model persona drafts (needs an approved non-Mock provider; no new paid service in M4).
- Safe Markdown preview, if ever added (needs a sanitizer with no new dependency, or a threat model).
- If user deletion is added, delete that owner's personas, versions, and `UserPersonaSettings` with their conversations.
