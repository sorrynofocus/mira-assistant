# AGENTS.md: <Project> Agent Operating Rules

## Coordination
This project uses the Agent Sync Protocol: `docs/agent-sync/AGENT_SYNC.md`. It defines roles, the append-only `REVIEW.md` log, the **Turn** baton, entry rules, milestone archiving and review severity. Follow it in every session.

## Required reading (every new session)
1. `docs/agent-sync/AGENT_SYNC.md`
2. `AGENTS.md` (this file)
3. The `REVIEW.md` status header (Roles, Turn) and its latest entries
4. <project design / architecture document>
5. The relevant implementation and tests before editing

## Project rules
<!-- Only project-specific rules belong here; the protocol covers coordination. -->
- **Technology constraints:** <languages, frameworks, things not to introduce without approval>
- **Security and privacy:** <auth, secrets, data handling, what must never be committed>
- **Scripts and tooling:** <shell, build and test commands>
- **Approval required for:** <architecture changes, paid services, pushes, ...> (added to the protocol's Owner authorization list; the owner gives approval directly to the acting agent)

## Completion checklist for each task
- [ ] Scope matches the current milestone.
- [ ] Build passes and the relevant tests ran, or the failure is documented precisely.
- [ ] No credentials, private data or databases committed.
- [ ] Security, privacy and accessibility implications checked.
- [ ] Public interfaces and contracts documented if they changed.
- [ ] `REVIEW.md` entry includes evidence and a **Next turn** line.
