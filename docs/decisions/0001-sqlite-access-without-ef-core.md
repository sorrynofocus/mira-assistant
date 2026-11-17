# ADR 0001: SQLite access without EF Core for M1

- **Status:** Accepted for M1 only (owner, 2026-10-17; Reviewer Codex recommended acceptance for M1). Proposed by Claude as Implementer, 2026-10-17. The data layer is reopened at the start of M2; see Revisit. **M2 Revisit resolved 2026-11-16: plain SQLite continues for M2; see [ADR 0002](0002-plain-sqlite-for-m2.md).**
- **Milestone:** M1

## Context

M1 needs durable local accounts, server-side sessions, and a governance audit table. Handoff §5 asks for a local account store in SQLite. Handoff §6 says "Use EF Core + SQLite migrations for routine CRUD", but schedules SQLite migrations and conversation CRUD for M2.

EF Core migrations need the `dotnet-ef` design-time tool and generated migration and snapshot files. For three small tables that would add tooling and generated code before the M2 data model exists.

## Decision

For M1, use `Microsoft.Data.Sqlite` directly:

- `MiraDatabase` (`backend/Mira.Infrastructure/Sqlite/MiraDatabase.cs`) owns the connection string (foreign keys on, WAL journal) and a small migration runner.
- Migrations are an append-only list of versioned SQL scripts. Applied versions are recorded in `SchemaMigrations`, and each migration runs in a transaction. The API applies pending migrations at startup, and so does `create-user`.
- Queries are hand-written and parameterized (`$name` parameters only, never string concatenation).
- The database defaults to `%LOCALAPPDATA%\MIRA\mira.db`, outside the repository and protected by the user profile's file ACLs. `Mira:DatabasePath` overrides it.

Conversations stay in memory for M1 (`InMemoryConversationStore`), behind `IConversationStore`, so M2 can swap in persistence without changing endpoints.

## Consequences

- No extra tooling. The full schema is readable in one file, and the migration path is tested (`StoreTests.MigrationsAreIdempotentAndVersioned`).
- The trade-off is manual mapping code and no LINQ. If M2's conversation and memory CRUD makes that burdensome, M2 can adopt EF Core:
  - scaffold a baseline migration that matches migration 1;
  - mark it applied for existing databases;
  - replace the hand-written queries behind the existing interfaces (`IUserAccountService`, `ISessionStore`, `IGovernanceAuditLog`, `IConversationStore`).
- FTS5, needed in M4, is plain SQL either way.

## Revisit

At the start of M2, before conversation persistence is built. This checkpoint is mandatory. Codex's advice (REVIEW.md, 2026-10-17 23:57 MST) leans toward EF Core for routine M2 CRUD, keeping targeted SQL for M4's FTS5. Any switch must:

- baseline M1's existing tables so EF doesn't try to recreate them;
- be tested against both a fresh database and a populated M1 copy;
- include a backup and a restore check.
