# AGENTS.md: MIRA / STT 3.0 Agent Operating Rules

## Coordination
This project uses the **Agent Sync Protocol** (`docs/agent-sync/AGENT_SYNC.md`). It defines roles, the append-only `REVIEW.md` log, the **Turn** baton, entry rules, milestone archiving and review severity. Follow it in every session. This file holds only MIRA-specific rules.

## Required reading (every new session)
1. `docs/agent-sync/AGENT_SYNC.md` (how agents coordinate).
2. `AGENTS.md` (this file).
3. The `REVIEW.md` status header (Roles, Turn) and its latest entries.
4. `docs/STT3_HANDOFF.md` (architecture and milestones).
5. `config/governance.example.md` and any approved governance decision record before modifying tool or permission code.
6. Relevant implementation and tests before editing.

## Project-specific working rules
- Current agents: Codex, Claude and Grok. Each may run sub-agents for routine documents (architecture notes, README, learning docs) after checking the file isn't being written by someone else.
- Grok may work as a team of Grok bots. The bots coordinate among themselves automatically, so the team skips the protocol's turn mechanics internally (see "Self-coordinating agent teams" in the protocol). Because Codex and Claude share this repository, the team still holds the Turn line while it works, appends one summary entry when it finishes or needs a decision, and has its work reviewed by the Reviewer (currently a separate Grok bot outside the Implementer team; see the Roles line). Every bot follows the rules in this file.
- Open follow-ups and todos live in `docs/TODO.md`. Add an item whenever you defer something (finding, nit, test gap), tick it and move it to Done when finished, and never drop open items when archiving `REVIEW.md` (owner request, 2026-11-17).
- Security reviewer: **MIRA Security**, a Grok bot (owner decision 2026-11-17). It threat-models each milestone plan before implementation starts and security-reviews the implementation before acceptance, alongside the Reviewer, in `SECURITY REVIEW` entries in `REVIEW.md`. Open Blocker or Major security findings block acceptance like the Reviewer's.
- Seek owner approval for material architectural changes, security compromises, persistent persona edits, new paid services, and any push to GitHub. These extend the protocol's Owner authorization list: the approval must reach the acting agent directly from the owner, and a `REVIEW.md` entry only records it.
- Never paste real secrets or private user messages in `REVIEW.md`, issue logs, or commits.
- Credit AI contributors in commit messages with a `Co-Authored-By:` trailer at the bottom, one line per agent that materially wrote or reviewed the change, e.g. `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. Grok team bots use `Co-Authored-By: <bot name> (Grok Bot) <noreply@x.ai>`, e.g. `Co-Authored-By: MIRA Boss (Grok Bot) <noreply@x.ai>`.

## Technology constraints
- Frontend: Astro static + vanilla TypeScript; do not introduce React/Vue/Svelte or Blazor without approval.
- Backend: C#, ASP.NET Core Minimal APIs, .NET 10 subject to SDK availability.
- Storage: SQLite + migrations, no mandatory vector database, BM25 corpus, or Azure AI Search.
- Streaming: SSE-formatted events in `fetch` response for POST.
- Audio roles: replaceable transcribe/chat/TTS providers; initial test uses mock providers.
- Authentication: swappable local username/password component. Support passwords at least 35 characters, securely hash, protect sessions, require resource ownership and CSRF controls. No password-strength checker required.
- Use Windows CMD for development scripts; do not require PowerShell.

## MIRA governance and tool rules
- MIRA = Memory, Intelligence, Reasoning & Autonomy. The character inspiration does not grant authority to change security boundaries.
- `whoami.md` defines persona only. `governance.md` is administrator-controlled; runtime policy is enforced by code, never solely by model instructions.
- A model may suggest rules/actions, but cannot self-approve or apply governance changes, create its own permission grants, or call state-changing tools without independently validated authorization.
- Any new agent capability must declare scope, risk, effects and approval mode; default deny and audit every decision.
- Coordinate material governance changes with the human owner and document approval in `REVIEW.md`.

## Completion checklist for each task
- [ ] Scope aligns with an STT 3.0 milestone.
- [ ] Build passes and relevant tests run (or failure is documented precisely).
- [ ] No credentials, real conversation data, DB, or private persona committed.
- [ ] Security/privacy and accessibility implications checked.
- [ ] Public-facing interfaces and event contracts documented if changed.
- [ ] `REVIEW.md` entry includes evidence and a **Next turn** line.
