# M2 conversation persistence and inspector

> **Status:** Implemented in this tree (plain SQLite, [ADR 0002](decisions/0002-plain-sqlite-for-m2.md)). M0 boundaries in [M0_ARCHITECTURE.md](M0_ARCHITECTURE.md) and M1 auth/streaming in [M1_AUTH_AND_STREAMING.md](M1_AUTH_AND_STREAMING.md) still apply. The later milestones in [STT3_HANDOFF.md](STT3_HANDOFF.md) are unchanged.
>
> This note is taken from the merged code, not from the planning brief. A live browser and keyboard pass of the steps in section 9 has **not** been run in the session that wrote this file; those steps are unverified.

## 1. Overview

M2 persists many conversations per signed-in user, keeps their messages across API restarts, and adds a hidden-by-default inspector of redacted pipeline events (timings and allowlisted metadata only). The mock chat provider is unchanged. The API still binds only to `http://127.0.0.1:5080`.

The browser talks to the same `/api` group as M1 (session cookie `mira.session`, CSRF header `X-CSRF-TOKEN` on POST/PATCH/DELETE). Owner IDs always come from the authenticated principal. A conversation, message, or request that is missing and one that belongs to someone else both look the same: **404**.

## 2. Database schema (migrations 2 and 3)

`MiraDatabase` (`backend/Mira.Infrastructure/Sqlite/MiraDatabase.cs`) still opens the SQLite file at `Mira:DatabasePath` (default `%LOCALAPPDATA%\MIRA\mira.db`, outside the repository), with foreign keys on and WAL journaling. Migrations are append-only; an applied version is never edited. The current latest version is **3**.

| Version | When | What it adds |
| --- | --- | --- |
| 1 | M1 | `Users`, `Sessions`, `ActionAudit` (unchanged) |
| 2 | M2 | `Conversations`, `Messages` |
| 3 | M2 | `PipelineEvents` |

**Migration 2 — `Conversations` and `Messages`**

```sql
CREATE TABLE Conversations (
    Id TEXT NOT NULL PRIMARY KEY,
    OwnerUserId TEXT NOT NULL REFERENCES Users(Id),
    Title TEXT NULL,
    CreatedAt TEXT NOT NULL,
    UpdatedAt TEXT NOT NULL
);
CREATE INDEX IX_Conversations_Owner_UpdatedAt ON Conversations(OwnerUserId, UpdatedAt DESC);

CREATE TABLE Messages (
    Id TEXT NOT NULL PRIMARY KEY,
    ConversationId TEXT NOT NULL REFERENCES Conversations(Id) ON DELETE CASCADE,
    Role TEXT NOT NULL,
    Content TEXT NOT NULL,
    Status TEXT NOT NULL,
    CreatedAt TEXT NOT NULL,
    ModelId TEXT NULL,
    RequestId TEXT NULL
);
CREATE INDEX IX_Messages_Conversation_CreatedAt ON Messages(ConversationId, CreatedAt);
```

- Conversation and message IDs are GUID strings. `Title` is null until the user renames the conversation (the API never auto-titles).
- `Role` and `Status` are stored as the enum name (`User` / `Assistant`; `Streaming`, `Completed`, `Interrupted`, `Failed`). The HTTP API serializes those enums as camelCase strings.
- `CreatedAt` and `UpdatedAt` are UTC timestamps in ISO-8601 round-trip form (`DateTimeOffset.UtcDateTime` format `"O"`).
- `RequestId` on a message is the 32-character lowercase hex id of the chat turn that wrote it (`Guid` format `"N"`), or null for rows that have none.
- There is no `PersonaId` column in M2 (that waits for a later migration).

**Migration 3 — `PipelineEvents`**

```sql
CREATE TABLE PipelineEvents (
    Id TEXT NOT NULL PRIMARY KEY,
    OwnerUserId TEXT NOT NULL REFERENCES Users(Id),
    RequestId TEXT NOT NULL,
    ConversationId TEXT NULL,
    EventType TEXT NOT NULL,
    ElapsedMs INTEGER NOT NULL,
    RedactedPayload TEXT NULL,
    CreatedAt TEXT NOT NULL
);
CREATE INDEX IX_PipelineEvents_Owner_RequestId ON PipelineEvents(OwnerUserId, RequestId);
CREATE INDEX IX_PipelineEvents_CreatedAt ON PipelineEvents(CreatedAt);
```

`RedactedPayload` is JSON for the allowlisted `PipelineEventData` fields. The store writes it with null properties omitted (`WhenWritingNull`). The HTTP inspector response still returns all nine keys, with unused values as `null` (see section 5).

**Startup sweep.** `MiraHost.InitializeMiraDatabaseAsync` (API process only, not `create-user`) calls `MigrateAsync`, then `InterruptStreamingMessagesAsync`: any `Messages` row still `Streaming` (left by a crash or a mid-turn shutdown) is set to `Interrupted` so it stays out of the prompt context. `create-user` applies pending migrations but does not run that sweep, because it may run while the API is serving a live turn.

## 3. Upgrade path from an M1 database

An M1-shaped file is a version-1 database: `SchemaMigrations` records only version 1, and the file has `Users`, `Sessions`, and `ActionAudit` but no conversation tables.

On the next API start (or `create-user`) against that same `Mira:DatabasePath`, `MigrateAsync` applies versions 2 and 3 in separate transactions and records them in `SchemaMigrations`. Existing users and sessions stay valid. The new tables start empty: M1 conversations lived in process memory and are not migrated. The interrupt sweep is a no-op until version 2 exists.

Migrations are idempotent for a given target version: already-applied versions are skipped. Upgrade tests in the backend build a version-1 database and migrate it forward to confirm this path; this document does not repeat those fixtures.

## 4. Endpoints and response shapes

All routes below are under `/api` and require the signed-in session cookie. Unauthenticated calls return **401**. JSON is camelCase; enums are camelCase strings (`"user"`, `"assistant"`, `"streaming"`, `"completed"`, `"interrupted"`, `"failed"`); times are ISO-8601 UTC strings; conversation and message IDs are GUID strings; `requestId` is 32 lowercase hex characters. Errors are `application/problem+json` `{ "type", "title", "status" }`. Show `title`; it does not contain user content. A non-GUID conversation id in the path is **404**.

**Shapes**

- `ConversationSummary` = `{ "id", "title": string|null, "createdAt", "updatedAt" }`. `title` is null until renamed; the UI label for that is "New chat".
- `Message` = `{ "id", "role", "content", "status", "createdAt", "modelId": string|null, "requestId": string|null }`. Messages are oldest first. `requestId` links a message to `GET /api/diagnostics/{requestId}`.
- `Conversation` = `{ "id", "title", "createdAt", "updatedAt", "messages": Message[] }`.

**Conversations** (`ConversationEndpoints.cs`)

| Method and path | Request | Success | Errors |
| --- | --- | --- | --- |
| `GET /api/conversations?skip=0&take=50` | `skip` >= 0 (default 0), `take` 1–100 (default 50) | **200** `{ "items": ConversationSummary[], "skip", "take", "hasMore" }`, newest activity first | 400 bad paging |
| `POST /api/conversations` | no body required (an optional `{ "title": string|null }` is also accepted) | **201** `ConversationSummary`, `Location: /api/conversations/{id}`. New rows have `title: null` | 400 title > 200 characters, 403 CSRF |
| `GET /api/conversations/{id}` | | **200** `Conversation` | 404 |
| `GET /api/conversations/current` | | **200** `Conversation` (same shape as by id): the most recently updated conversation, or a new one if the user has none | |
| `PATCH /api/conversations/{id}` | body required: `{ "title": string|null }` (null, empty or spaces clears the title; the value is trimmed) | **200** `ConversationSummary` | 400 title > 200, 403 CSRF, 404 |
| `DELETE /api/conversations/{id}` | | **204**, permanent; cancels that conversation's active turn (section 7) | 403 CSRF, 404 |

Renaming, and every message written, updates `UpdatedAt` and moves the conversation to the top of the list.

**Chat** (`ChatEndpoints.cs`)

- `POST /api/chat/stream` body `{ "message": string, "conversationId"?: guid }`. With `conversationId`, that conversation is used (404 problem JSON, no stream, nothing saved, if it is missing or not yours). Without it: M1 behaviour (most recently updated conversation, or a new one). Other errors unchanged: 400 empty or too long, 409 already streaming, 403 CSRF.
- The SSE contract is unchanged from M1 (`v: 1`). Every envelope has `requestId` and `conversationId`. Event types: `response.started`, `response.delta`, `response.completed`, `response.cancelled`, `response.error`. `response.error` codes are `provider_error`, `timeout`, and (new in M2) `conversation_deleted`: the conversation was permanently deleted while the turn was running. Status `interrupted`; message `This conversation was deleted, so the reply was discarded.` It replaces the normal final event, and nothing of the request is kept (its diagnostics 404). If the conversation is deleted before anything streams, the stream request gets 404 problem JSON instead.
- `POST /api/chat/cancel` is unchanged.

**Inspector** (`DiagnosticsEndpoints.cs`)

- `GET /api/diagnostics?conversationId=<guid>&take=20` → **200** `{ "items": [ { "requestId", "conversationId": guid|null, "startedAt", "lastEventType", "firstTokenMs": number|null, "totalMs": number|null } ] }`, newest first. `conversationId` is optional (omit = all of the user's requests). `take` is 1–50, default **20**. 400 if `take` is out of range; 404 if `conversationId` is set and the conversation is missing or not yours.
- `GET /api/diagnostics/{requestId}` → **200** `{ "requestId", "conversationId": guid|null, "events": [ { "eventType", "elapsedMs", "createdAt", "data": { ...nine allowlist keys } } ] }`, events oldest first. **404** if the id is unknown, not yours, deleted with its conversation, older than 30 days, or not a 32-character lowercase hex string (a malformed id is 404, not 400).

Example (no message text; `data` always has all nine keys):

```json
{
  "requestId": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "conversationId": "00000000-0000-4000-8000-000000000001",
  "events": [
    {
      "eventType": "request.started",
      "elapsedMs": 0,
      "createdAt": "2026-11-16T20:00:00.0000000+00:00",
      "data": {
        "modelId": "mock-chat",
        "status": null,
        "inputChars": 12,
        "outputChars": null,
        "promptTurns": 2,
        "deltaCount": null,
        "firstTokenMs": null,
        "totalMs": null,
        "errorCode": null
      }
    }
  ]
}
```

## 5. Pipeline event types, allowlist, and redaction

Event types for one turn (`PipelineEventTypes`):

1. `request.started` (always).
2. `response.first_token` if any text arrived (records `FirstTokenMs`).
3. Exactly one terminal event: `response.completed`, `response.cancelled` (the user pressed Stop), `response.aborted` (the browser dropped the request), `response.timeout`, or `response.failed`.

The allowlist (`PipelineEventData`) is the complete set of payload fields. Lengths and counts only; never message text, prompts, titles, usernames, passwords, cookies, CSRF tokens, API keys, or provider error messages.

| Key | Meaning |
| --- | --- |
| `modelId` | Configured provider model id (in M2 the mock is `mock-chat`) or the literal `"unlisted"` |
| `status` | Message status on the terminal event, otherwise null |
| `inputChars` | Length of the user message, never its text |
| `outputChars` | Length of the assistant text so far |
| `promptTurns` | Number of turns sent to the model |
| `deltaCount` | Number of SSE deltas written |
| `firstTokenMs` | Milliseconds to the first token |
| `totalMs` | Milliseconds for the whole turn |
| `errorCode` | One of `timeout`, `provider_error`, `client_aborted`, `cancelled`, or null |

`PipelineRedactor` (`backend/Mira.Infrastructure/Pipeline/PipelineRedactor.cs`):

- Throws if `eventType` is not in `PipelineEventTypes.All`.
- `modelId`: null stays null; a configured provider id is kept; anything else becomes `"unlisted"`. A configured id must be a short identifier (`^[A-Za-z0-9._:/-]{1,100}$`) and must not itself be `"unlisted"` (that is a startup error).
- `status` is kept only if it is a defined `MessageStatus`.
- Numeric fields that are negative become null.
- `errorCode` is kept only if it is in `PipelineErrorCodes.All`; otherwise it becomes null.

`PipelineRecorder` redacts, stamps `Id` and `CreatedAt`, and appends. It must not fail the chat turn: write failures are logged by exception type only (no payload, no message text) and swallowed. The write uses a short timeout instead of the chat cancellation token so a cancelled or aborted turn can still record its terminal event.

Chat instrumentation (`ChatEndpoints`): `request.started` carries `ModelId`, `InputChars`, `PromptTurns`; `response.first_token` carries `FirstTokenMs`; each terminal event carries `ModelId`, `Status`, `OutputChars`, `DeltaCount`, `FirstTokenMs`, `TotalMs`, and an `ErrorCode` when the turn did not complete normally (`cancelled`, `client_aborted`, `timeout`, `provider_error`).

The frontend inspector is hidden until the user opens it. It lists recent requests and a selected request's events (type, elapsed ms, local timestamps with the UTC ISO string in the `title` attribute, first-token latency). Null allowlist values are not shown as the word "null". A message that has a `requestId` has an "Inspect request" control that opens this panel on that request. A 404 (unknown, purged, or malformed id) is shown as a generic "no longer available" status.

## 6. Retention and purge schedule

Owner decision (2026-11-16): inspector events are kept for **30 days** (`PipelineEventRetention.Period`). `PipelineRetentionService` is a hosted service that calls `IPipelineEventStore.PurgeOlderThanAsync(utcNow - 30 days)`:

- once when the API starts (the first loop of `ExecuteAsync`),
- then every **24 hours** (`PipelineRetentionService.Interval`).

The purge deletes `PipelineEvents` rows with `CreatedAt` earlier than the cutoff, across all owners. It is maintenance, not an owner-scoped delete. Failures are logged by exception type only and retried on the next cycle.

## 7. Permanent delete

Delete is permanent (owner decision 2026-11-16). `DELETE /api/conversations/{id}` returns **204** and, in one transaction (`SqliteConversationStore.DeleteAsync`):

1. Deletes `PipelineEvents` for that `ConversationId` and owner.
2. Deletes `Messages` for that owned conversation.
3. Deletes the `Conversations` row.

The `Messages` table also has `ON DELETE CASCADE` from `Conversations(Id)`. Pipeline events are not on that cascade (they are keyed by owner and optional conversation id), which is why they are deleted explicitly in the same transaction.

If that conversation has a streaming turn, the delete then cancels it (owner decision 2026-11-16 23:01) and waits up to 5 s for it to end, so the user's streaming slot is free when the 204 arrives (a new send is not 409). The cancelled stream ends with `response.error` `conversation_deleted`, records no terminal pipeline event, and its diagnostics 404. Deleting a different conversation leaves the active turn alone. Delete runs first and the cancel second (`ActiveTurnRegistry.CancelConversationAsync`), so the cancelled turn always finds its conversation gone; a late event is also dropped by `SqlitePipelineEventStore.AppendAsync`.
 The UI asks for confirmation first; cancelling the prompt sends no request.

## 8. Frontend behaviour (chunk D)

- The conversation sidebar lists pages of 50 (`hasMore` drives a "Load more" button; the next page uses `skip` = items already loaded, `take` 50).
- New chat calls `POST /api/conversations` with no body. A null title is labelled **New chat**. Rename uses `PATCH { "title" }`; a blank value clears the title.
- Chat sends `{ "message", "conversationId" }` to `POST /api/chat/stream`. Interrupted and failed assistant messages are labelled "Interrupted" and "Failed".
- The selected conversation id is remembered in `localStorage` under `mira.selectedConversationId`. On reload the UI restores that conversation, or falls back to `GET /api/conversations/current` (full `Conversation` shape, including `title`). A 404 refreshes the list.
- Server-provided text is assigned with `textContent` only, never `innerHTML`.

## 9. Manual browser and keyboard checks

These steps follow the shipped UI. They have **not** been executed against a running app in the session that wrote this note (unverified). No screen-reader pass is planned for M2 (that stays with M8).

Keyboard-only throughout: Tab to each control, use a visible focus ring, Enter/Space to activate, Escape to stop a stream or cancel an in-progress rename.

1. Sign in with a local account. The conversation sidebar and composer appear; the inspector stays hidden.
2. Send a message. The reply streams from the mock model. While it streams, only one turn is allowed (a second Send is refused with 409 until it finishes or you press Stop).
3. Press Stop mid-reply. The assistant message is labelled **Interrupted**. A partial reply remains after reload.
4. Reload the page (still signed in). The selected conversation and its completed / interrupted / failed messages are still there, with the same titles.
5. Use **New chat**, switch between conversations, **Rename** (including a blank title back to "New chat", and a title of 200 characters), and **Load more** if more than 50 conversations exist.
6. **Delete** a conversation: cancelling the confirmation prompt leaves it in place; confirming removes it and its messages and inspector events permanently.
7. Open **Show inspector**. Recent requests for the current conversation appear, with local timestamps (UTC ISO in the tooltip), event types, elapsed milliseconds, and first-token latency. Null metadata is not shown as "null". `modelId` may be `mock-chat` or `unlisted`.
8. From an assistant or user message, use **Inspect request** to open the same request. A stale or malformed request id should fail gracefully (the request is "no longer available"), not break the page.
9. Sign out. The sign-in screen appears only after the server has ended the session (or already reports 401).

## 10. Out of scope for M2

Personas (M3), memories and FTS5 (M4), audio (M5/M6), Tavily (M7), real or paid model providers, EF Core, Markdown rendering, encryption at rest, and a screen-reader audit (M8).
