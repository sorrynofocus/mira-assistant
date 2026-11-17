# M2 chunk briefs (Grok team)

Owner: Grok (MIRA Boss, team lead). Working notes for the bots implementing M2. The plan of record is the `2026-11-16 21:55 MST | Grok (MIRA Boss, team lead) | INTENT` entry in `REVIEW.md` (with the owner's 21:52 answers in the 21:50 entry). If this file and the INTENT disagree, the INTENT wins; ask MIRA Boss.

## Every bot, before starting

1. Read `AGENTS.md`, `docs/agent-sync/AGENT_SYNC.md`, the `REVIEW.md` status header and the M2 INTENT, `docs/STT3_HANDOFF.md` §4.1, §6, §7, §10 M2, ADR 0001 and ADR 0002.
2. Code against the chunk 0 contracts: `backend/Mira.Core/Conversations.cs` and `backend/Mira.Core/PipelineEvents.cs`. Don't change them; if a contract is wrong or missing something, ask MIRA Boss, who owns them.
3. **Edit only your chunk's files.** Need a change in another chunk's file? Ask MIRA Boss, who routes it to that file's owner. Never edit `REVIEW.md`, `AGENTS.md`, `docs/agent-sync/` or `scripts/`.
4. **Never commit or push.** Commits need the owner's approval, given directly to MIRA Boss. Leave your changes in the working tree and tell MIRA Boss which files you changed.
5. Toolchain: .NET SDK 10 (on the box: `export PATH=$HOME/.dotnet:$PATH`), Node >=22.23.4. `scripts/*.cmd` are Windows-only; on Linux run their steps directly:
   `dotnet build backend/Mira.sln -m:1 && dotnet test backend/Mira.sln --no-build -m:1 && npm --prefix frontend ci && npm --prefix frontend test && npm --prefix frontend run check && npm --prefix frontend run build`
   Hand back only with a green build (0 warnings) and all tests passing; report the counts.
6. Project rules that bite here: parameterized SQL only (`$name`); owner ID from the principal only (`CurrentUser.GetUserId`), never from JSON; no lookup by bare ID; another owner's resource is a 404, same as a missing one; no message text, titles, passwords, cookies, tokens or keys in logs, pipeline events, tests fixtures that look real, or `REVIEW.md`; no new NuGet or npm dependency, framework (Astro + vanilla TS only), paid service or network call; Windows CMD remains the dev-script shell.

## Contracts (chunk 0, done)

`Conversations.cs`:
- `ConversationMessage(Id, Role, Content, Status, CreatedAt, ModelId, RequestId)`; `MessageStatus` = `Streaming | Completed | Interrupted | Failed`.
- `ConversationSummary(Id, Title, CreatedAt, UpdatedAt)`; `ConversationSnapshot(Id, OwnerUserId, Title, CreatedAt, UpdatedAt, Messages)`.
- `ConversationLimits`: `MaxTitleLength` 200, `DefaultPageSize` 50, `MaxPageSize` 100, `NormalizeTitle` (blank = untitled; too long throws `ArgumentException`).
- `IConversationStore`: `ListAsync(owner, skip, take)`, `CreateAsync(owner, title)`, `GetAsync(owner, id)` (null if missing/not owned), `GetMostRecentOrCreateAsync(owner)` (M1 compatibility), `RenameAsync` / `DeleteAsync` (false if missing/not owned; delete is permanent), `AppendUserMessageAsync(owner, conversationId, content, requestId)`, `BeginAssistantMessageAsync(owner, conversationId, modelId, requestId)`, `FinishAssistantMessageAsync(owner, conversationId, messageId, content, status)`, `GetPromptTurnsAsync(owner, conversationId)`. The comments in the file are the exact semantics.

`PipelineEvents.cs`:
- `PipelineEventTypes` (`request.started`, `response.first_token`, `response.completed`, `response.cancelled`, `response.aborted`, `response.timeout`, `response.failed`) and `PipelineErrorCodes` (`timeout`, `provider_error`, `client_aborted`, `cancelled`).
- `PipelineEventData`: the **whole** payload allowlist (ModelId, Status, InputChars, OutputChars, PromptTurns, DeltaCount, FirstTokenMs, TotalMs, ErrorCode). Lengths and counts, never text.
- `PipelineEvent(Id, OwnerUserId, RequestId, ConversationId?, EventType, ElapsedMs, Data, CreatedAt)`, `PipelineRequestSummary`.
- `PipelineEventRetention.Period` = 30 days (owner decision); `MaxRequestsPerQuery` = 50.
- `IPipelineRedactor.Redact(eventType, data)`, `IPipelineRecorder.RecordAsync(...)` (never throws into the chat turn), `IPipelineEventStore` (`AppendAsync`, `GetByRequestAsync`, `ListRecentRequestsAsync`, `PurgeOlderThanAsync`).

Chunk 0 also made the minimum edits needed to keep M1 green, all M1 behavior unchanged: `InMemoryConversationStore.cs` now implements the full new contract (multi-conversation, in memory); `ChatEndpoints.cs` calls the new signatures with M1's most-recent-conversation behavior and passes the existing `requestId` to messages; two tests in `StoreTests.cs` and one in `ChatStreamTests.cs` call the new signatures. **No `NotImplementedException` stubs exist**: nothing in chunk 0 implements the pipeline interfaces or SQLite storage; those are chunks A and B. Owners of those files now take them over.

## Chunk A: SQLite storage (Storage bot)

**Owns:** `backend/Mira.Infrastructure/Sqlite/MiraDatabase.cs`, new `Sqlite/SqliteConversationStore.cs`, new `Sqlite/SqlitePipelineEventStore.cs`, `backend/Mira.Infrastructure/InMemoryConversationStore.cs`, `backend/Mira.Tests/StoreTests.cs`, new `backend/Mira.Tests/ConversationStoreTests.cs`.

**Build:**
- Migration 2: `Conversations` and `Messages`; migration 3: `PipelineEvents`. Exact columns and indexes are in the INTENT ("Data model"). Same GUID text and ISO-8601 UTC formats as migration 1. Never edit migration 1.
- Startup sweep: `MiraDatabase.InterruptStreamingMessagesAsync()` turns any `Messages.Status = 'Streaming'` into `Interrupted`. It is separate from `MigrateAsync` and is called only at API startup (`MiraHost.InitializeMiraDatabaseAsync`), never by create-user (fix by MIRA Boss, 2026-11-16).
- `SqliteConversationStore : IConversationStore`: every SQL statement filters on `OwnerUserId` (join through `Conversations` for messages). `DeleteAsync` deletes the conversation's `PipelineEvents` rows, its `Messages` and the row, in **one transaction**.
- `SqlitePipelineEventStore : IPipelineEventStore`: `Data` stored as JSON in `RedactedPayload` (serialize `PipelineEventData` only). Owner-scoped reads. `PurgeOlderThanAsync` deletes by `CreatedAt`.

**Tests (expected):** fresh DB reaches version 3; an M1-shaped DB (version 1 with a user and a session) upgrades to 3 and the user can still log in and the session still resolves; migrations idempotent; Streaming rows swept to Interrupted; message order stable; list order and paging bounds; rename/title rules; delete removes conversation, messages and its pipeline events, and leaves other conversations alone; purge removes only events older than the cutoff; every method returns null/false/empty or throws `KeyNotFoundException` for another owner, as the contract says. Run the same contract tests against `InMemoryConversationStore` and `SqliteConversationStore` (a shared theory or base class).

## Chunk B: recording and redaction (Pipeline bot)

**Owns:** new `backend/Mira.Infrastructure/Pipeline/PipelineRedactor.cs`, new `Pipeline/PipelineRecorder.cs`, new `Pipeline/InMemoryPipelineEventStore.cs` (test double; refinement of the INTENT), new `backend/Mira.Tests/PipelineRedactionTests.cs`.

**Build:**
- `PipelineRedactor : IPipelineRedactor` (ctor takes the configured model IDs; the host passes the registered `IChatProvider.ModelId`): unknown event type throws `ArgumentException`; `ModelId` kept only if it is a configured model ID, otherwise stored as `"unlisted"` (fix by MIRA Boss, 2026-11-16); `ErrorCode` kept only if in `PipelineErrorCodes.All`; negative numbers become null.
- `PipelineRecorder : IPipelineRecorder` (ctor: `IPipelineEventStore`, `IPipelineRedactor`, `TimeProvider`, `ILogger<PipelineRecorder>`): redact, stamp `Guid.NewGuid()` and `time.GetUtcNow()`, append. Catch every exception, log the exception **type** only, never rethrow; don't use the chat's cancellation token for the write (pass `CancellationToken.None` or a short timeout) so a cancelled turn still records its final event.
- `InMemoryPipelineEventStore : IPipelineEventStore` for API tests, owner-scoped like the real one.

**Tests (expected):** feed message-like text, a 35+ char password, a session-cookie value, a CSRF token and an `sk-...`-style key into every string field that exists (ModelId, ErrorCode) and assert none survives; the record type has no other place to put text (assert by reflection that `PipelineEventData` has only the allowlisted properties, so adding a field fails a test); unknown event type rejected; recorder swallows a throwing store and logs no payload; recorder stamps time from `TimeProvider`.

## Chunk C: API (API bot)

**Owns:** `backend/Mira.Api/Chat/ChatEndpoints.cs`, `Chat/SseWriter.cs` (only if needed), new `backend/Mira.Api/Conversations/ConversationEndpoints.cs`, new `backend/Mira.Api/Diagnostics/DiagnosticsEndpoints.cs`, new `Diagnostics/PipelineRetentionService.cs` (hosted service: purge at startup and every 24 h with `PipelineEventRetention.Period`; refinement of the INTENT), `backend/Mira.Api/MiraHost.cs`, `backend/Mira.Tests/ChatStreamTests.cs`, `backend/Mira.Tests/TestSupport.cs`, new `backend/Mira.Tests/ConversationApiTests.cs`, new `backend/Mira.Tests/DiagnosticsApiTests.cs`.

**Build:**
- Endpoints from the INTENT: `GET/POST /api/conversations`, `GET/PATCH/DELETE /api/conversations/{id}`, `GET /api/diagnostics/{requestId}`, `GET /api/diagnostics?conversationId=`. Keep `GET /api/conversations/current`. All under the existing `/api` group (auth + CSRF on unsafe methods). 404 for missing or not owned; 400 for bad titles or paging.
- `POST /api/chat/stream` accepts optional `conversationId`; absent = `GetMostRecentOrCreateAsync`; not owned = 404 before any streaming.
- Record pipeline events through `IPipelineRecorder`: `request.started` (InputChars, PromptTurns, ModelId), `response.first_token` (FirstTokenMs), then exactly one terminal event per turn: `completed`, `cancelled`, `aborted`, `timeout` or `failed` (with Status, OutputChars, DeltaCount, TotalMs, ErrorCode). The SSE contract stays `v: 1`.
- `MiraHost`: register `SqliteConversationStore`, `SqlitePipelineEventStore`, `PipelineRedactor`, `PipelineRecorder`, `PipelineRetentionService` once A and B land; until then code against the interfaces and use B's in-memory store in tests.

**Tests (expected):** 401 unauthenticated; CSRF required on POST/PATCH/DELETE; user B gets 404 on user A's conversation for GET, PATCH, DELETE, diagnostics by request ID, and chat/stream with A's `conversationId`; paging bounds; a turn persists user + completed assistant messages and a fresh GET returns both; Stop persists partial text as Interrupted; provider failure persists Failed; all events of a turn share the SSE `requestId` and exactly one terminal event exists; `FirstTokenMs` present after the first delta; no message text appears in any stored event (search the stored JSON for the sent message); concurrent send still 409; the retention service purges with the 30-day cutoff (fake `TimeProvider`).

## Chunk D: frontend (Frontend bot)

**Owns:** `frontend/src/pages/index.astro`, `frontend/src/scripts/chat.ts`, `frontend/src/scripts/api.ts`, new `frontend/src/scripts/conversations.ts`, new `frontend/src/scripts/inspector.ts`, new `frontend/src/scripts/conversations.test.ts`, new `frontend/src/scripts/inspector.test.ts`.

**Build:** conversation sidebar (list, New chat, switch, rename, delete with a confirmation prompt; cancelling deletes nothing); chat sends `conversationId`; reload restores the selected conversation and shows interrupted/failed replies as such; inspector panel hidden by default (toggle), listing recent requests and a request's events with timings and first-token latency. Use `api.ts` helpers (CSRF handled there). Render all server text with `textContent`, never `innerHTML`. Keyboard reachable, visible focus, labelled controls, `aria-live` for status. Code against the API shapes in the INTENT; use fakes until chunk C lands.

**Tests (expected):** Vitest for list rendering and escaping (a title with `<img onerror>` stays text), switching, new, rename, delete only after confirmation, inspector rendering of a redacted event list and empty state; existing `sse.test.ts` and `session.test.ts` keep passing; `astro check` 0/0/0.

### Final API contract (chunk C, implemented and tested 2026-11-16)

Code against this. All routes are under `/api` and need the signed-in session cookie (`mira.session`); without it every route returns **401**. JSON is camelCase; enums are camelCase strings (`"user"`, `"assistant"`, `"streaming"`, `"completed"`, `"interrupted"`, `"failed"`); times are ISO-8601 UTC strings; IDs are GUID strings except `requestId`.

- **CSRF:** every POST, PATCH and DELETE must send header `X-CSRF-TOKEN: <token>` (token from `GET /api/auth/csrf` or the login response; `api.ts` already does this). Missing or wrong token: **403** `{"title":"CSRF validation failed.",...}`. Cross-origin writes: **403**.
- **Errors** are `application/problem+json`: `{ "type", "title", "status" }`. Show `title`; it never contains user content.
- **Not found and not yours look the same: 404.** A non-GUID conversation ID in the path also gives 404.

**Shapes**
- `ConversationSummary` = `{ "id", "title": string|null, "createdAt", "updatedAt" }` (`title` null = untitled; render your own label, e.g. "New conversation").
- `Message` = `{ "id", "role", "content", "status", "createdAt", "modelId": string|null, "requestId": string|null }`. `requestId` links a message to `GET /api/diagnostics/{requestId}`.
- `Conversation` = `{ "id", "title", "createdAt", "updatedAt", "messages": Message[] }` (messages oldest first).

**Conversations**

| Method and path | Request | Success | Errors |
|---|---|---|---|
| `GET /api/conversations?skip=0&take=50` | `skip` >= 0 (default 0), `take` 1-100 (default 50) | **200** `{ "items": ConversationSummary[], "skip", "take", "hasMore": bool }`, newest activity first | 400 bad paging |
| `POST /api/conversations` | body optional: `{ "title": string|null }` (an empty body is fine) | **201** `ConversationSummary`, `Location: /api/conversations/{id}` | 400 title > 200 chars, 403 CSRF |
| `GET /api/conversations/{id}` | | **200** `Conversation` | 404 |
| `GET /api/conversations/current` | | **200** `Conversation` (same shape as by id): most recently updated conversation, created if the user has none | |
| `PATCH /api/conversations/{id}` | body required: `{ "title": string|null }` (null, empty or spaces clears the title; trimmed) | **200** `ConversationSummary` | 400 title > 200, 403 CSRF, 404 |
| `DELETE /api/conversations/{id}` | | **204**, permanent: conversation, its messages and its inspector events are gone; then cancels that conversation's active turn (see below) | 403 CSRF, 404 |

Renaming, and every message written, moves the conversation to the top of the list (`updatedAt`).

**Chat**
- `POST /api/chat/stream` body `{ "message": string, "conversationId"?: guid }`. With `conversationId`: that conversation (404 problem JSON, no stream, nothing saved, if missing or not yours). Without it: M1 behaviour (most recently updated conversation, or a new one). Other errors unchanged: 400 empty/too long, 409 already streaming, 403 CSRF.
- The SSE contract is unchanged (`v: 1`): every event envelope has `requestId` and `conversationId`; event types `response.started`, `response.delta`, `response.completed`, `response.cancelled`, `response.error`. `response.error` codes are `provider_error`, `timeout`, and (new in M2) `conversation_deleted`: the conversation was permanently deleted while the turn was running. Status `interrupted`; message `This conversation was deleted, so the reply was discarded.` It replaces the normal final event, and nothing of the request is kept (its diagnostics 404). If the conversation is deleted before anything streams, the stream request gets 404 problem JSON instead. If that conversation has a streaming turn, the delete then cancels it (owner decision 2026-11-16 23:01) and waits up to 5 s for it to end, so the user's streaming slot is free when the 204 arrives (a new send is not 409). The cancelled stream ends with `response.error` `conversation_deleted`, records no terminal pipeline event, and its diagnostics 404. Deleting a different conversation leaves the active turn alone.
- `POST /api/chat/cancel` unchanged.

**Inspector (diagnostics)**
- `GET /api/diagnostics?conversationId=<guid>&take=20` -> **200** `{ "items": [ { "requestId", "conversationId": guid|null, "startedAt", "lastEventType", "firstTokenMs": number|null, "totalMs": number|null } ] }`, newest first. `conversationId` optional (omit = all of the user's requests); `take` 1-50, default 20. 400 bad `take`; 404 if `conversationId` is missing or not yours.
- `GET /api/diagnostics/{requestId}` -> **200** `{ "requestId", "conversationId": guid|null, "events": [ { "eventType", "elapsedMs", "createdAt", "data": { "modelId", "status", "inputChars", "outputChars", "promptTurns", "deltaCount", "firstTokenMs", "totalMs", "errorCode" } } ] }`, events oldest first. 404 if unknown, not yours, deleted with its conversation, older than 30 days, or not a 32-char lowercase hex ID.
- `data` always has all nine keys; unused ones are `null`. `modelId` is the configured model ID or the literal `"unlisted"`. `errorCode` is one of `timeout`, `provider_error`, `client_aborted`, `cancelled`, or null.
- Event types per turn: `request.started`, then `response.first_token` (if any text arrived), then exactly one terminal event: `response.completed`, `response.cancelled` (Stop), `response.aborted` (browser dropped the request), `response.timeout`, or `response.failed`. Exception: a turn whose conversation is deleted mid-stream records no terminal event, and the delete removes the rest.
- No message text is ever in these responses; render everything with `textContent` anyway.

**Deviations from the brief and INTENT the frontend must know:**
- `GET /api/conversations/current` now returns the full `Conversation` shape (adds `title`, `createdAt`, `updatedAt`).
- `GET /api/diagnostics/{requestId}` returns 404, not 400, for a malformed ID.
- `modelId` in inspector data can be `"unlisted"`.
- `PATCH` needs a JSON body; `POST` doesn't.
- Conversations are never auto-titled; new ones have `title: null` until renamed.

## Chunk E: docs (Docs bot)

**Owns:** new `docs/M2_PERSISTENCE_AND_INSPECTOR.md`, `README.MD` (project-status and features sections only).

**Build:** near the end, from the merged code (not from this brief): schema, endpoints and response shapes, pipeline event types and the payload allowlist, redaction rules, 30-day retention and purge schedule, permanent delete, upgrade path from M1 databases, manual browser/keyboard test steps. Mark anything unverified. No real conversation content in examples.

## Integration (MIRA Boss)

Owns `REVIEW.md`, this file and the chunk 0 contracts. Merges chunks, runs the full suite, does the manual browser and keyboard pass (carry-over from M0-M1), appends the team's single IMPLEMENTATION entry with evidence, and sets the Turn to the MIRA Reviewer (a separate Grok bot; called "Grok Reviewer" in the 21:50 and 21:55 entries). Asks the owner for commit approval after review.
