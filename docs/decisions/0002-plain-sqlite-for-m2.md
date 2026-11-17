# ADR 0002: Plain SQLite (no EF Core) for M2

- **Status:** Accepted for M2 (owner, 2026-11-16). Resolves the mandatory M2 Revisit in [ADR 0001](0001-sqlite-access-without-ef-core.md). Recorded by Grok (MIRA Boss, team lead) as Implementer.
- **Milestone:** M2
- **Supersedes:** nothing. ADR 0001 stays the record of the M1 decision; this ADR extends the same approach to M2.

## Context

ADR 0001 accepted direct `Microsoft.Data.Sqlite` access for M1 only and made a data-layer decision mandatory at the start of M2, before conversation persistence is built. Handoff §6 proposes "EF Core + SQLite migrations for routine CRUD". Codex's advice (2026-10-17 23:57 MST QUESTION / ADVICE, now in `docs/review-archive/M0-M1.md`) leaned toward EF Core for M2's routine CRUD, while noting that switching needs an EF baseline for M1's existing tables, fresh and populated-database tests, and a backup/restore check.

Grok, as the new Implementer, recommended staying with plain SQLite: the migration runner already exists and is tested, M2 adds only a few tables (`Conversations`, `Messages`, `PipelineEvents`), and switching would add baselining and restore work with little payoff at this size.

## Decision

The owner decided, directly to Grok (MIRA Boss) in the Grok Bot chat on 2026-11-16: **stay with plain SQLite for M2.** So, for M2:

- Keep `Microsoft.Data.Sqlite` and the hand-written, append-only migration runner in `backend/Mira.Infrastructure/Sqlite/MiraDatabase.cs`. M2 tables are added as new versioned migrations (2, 3, ...). Migration 1 is never edited.
- Each migration runs in a transaction and is recorded in `SchemaMigrations`, as in M1.
- Queries stay hand-written and parameterized (`$name` parameters only, never string concatenation).
- Every conversation, message and pipeline-event query is scoped by the authenticated owner's ID taken from the principal, never from request input. There is no lookup by bare ID.
- Persistence sits behind the existing `IConversationStore` contract (extended as M2 needs), so endpoints don't depend on the storage technology.
- No new NuGet dependency for data access.

## Consequences

- No EF tooling, generated migrations or snapshot files; no baseline reconciliation of M1's tables; existing databases upgrade through the same runner.
- Mapping code is manual. Owner scoping is enforced by code review and by cross-user tests, not by an ORM query filter (which wouldn't replace those tests anyway).
- Migration tests must cover a fresh database **and** upgrading an M1-shaped database (version 1 with rows) to the M2 version, with existing users and sessions intact.
- FTS5 in M4 is plain SQL either way.

## Revisit

Optional, not mandatory: reconsider at the start of M4 (memory, FTS5) if hand-written mapping becomes a maintenance burden. Any later switch to EF Core keeps the conditions in ADR 0001's Revisit (baseline existing tables, fresh and populated-copy tests, backup and restore check) and needs a new owner decision.
