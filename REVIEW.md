# REVIEW.md: STT 3.0 Codex / Claude Shared Handoff Log

> Append entries chronologically. Do not erase earlier findings or change their status without a new resolution entry. Consult `AGENTS.md` and `docs/STT3_HANDOFF.md` before work.

## Current project status
- **Milestone:** M0, not started
- **Working branch/commit:** Not established
- **Last verified build:** Not run
- **Open blockers:** None reported; architecture validation pending
- **Next concrete action:** Codex sets up M0 repo, build, safety baseline, and tests, then records results below.

---

## Entry template

### YYYY-MM-DD HH:MM TZ | AGENT | [IMPLEMENTATION / REVIEW / RESOLUTION]

**Objective:**

**Branch and commit:**

**Files examined or changed:**

**Changes / findings:**
- [Severity if review] Describe issue with file path and line range, evidence, expected behavior, suggested fix.

**Commands run and results:**
```text
command
result (PASS/FAIL; relevant details)
```

**Security/privacy concerns:**

**Open questions / decisions needed:**

**Handoff / next exact action:**

---

## Initial handoff | ARCHITECTURE

- The user approved Astro + vanilla TypeScript, ASP.NET Core/C#, SQLite local-first memory, `whoami.md` personas, three model roles (STT/chat/TTS), optional Tavily, server-side credentials, modular local login, and Codex/Claude file-based collaboration.
- Implement text streaming and authentication first. Speech, persona, memory, and search follow milestones in the handoff.
- Authentication must accept complex passwords of **at least 35 characters**, with standard secure hashing and safe cookies; no password-strength meter or breach check is requested.
- Persona and memory updates must remain user-inspectable and under user control.
- **Codex:** begin with M0, provide tests and reviewable evidence. **Claude:** review diffs, tests, and security posture independently.

---

## 2026-10-16 | ARCHITECTURE UPDATE | MIRA identity and governed autonomy

- Product identity proposed: **MIRA = Memory, Intelligence, Reasoning & Autonomy**; inspired by the user's autonomous AI character in *Invisible War*. Internal project designation STT 3.0 remains usable.
- Added distinct responsibilities for `whoami.md`, administrator-controlled `governance.md`, user memories, and scoped permission grants.
- An LLM may propose persona changes, memories, actions, and policy revisions, but must never self-grant authority or directly apply governance revisions.
- All future agent tools are default-deny and subject to server-side scope checks, user approvals, revocation, and audit. No browser automation or state-changing tool is authorized for the first implementation phase.
- **Codex next:** keep M0 bounded; add a governance template and enforceable service boundary without building autonomous tools. **Claude next:** verify no policy bypass or implicit tool authorization.
- **Status:** design updated only; no source code or runtime checks performed.

---

## 2026-10-16 22:32 MST | Codex | IMPLEMENTATION INTENT

**Objective:** Establish the first `main` commit and implement M0 only: repository safety baseline, local Astro/.NET structure, mock provider boundary, health check, CMD scripts, and tests.

**Branch and commit:** Unborn `main`; `origin` is the GitHub repository. No commit yet.

**Files examined or changed:** `AGENTS.md`, `docs/STT3_HANDOFF.md`, `REVIEW.md`, `README.MD`, `config/governance.example.md`; implementation files pending.

**Known risks:** The repository directory ownership triggers Git's safe-directory check for some commands. Astro dependencies require package installation. The frontend and API are not yet implemented.

**Security/privacy concerns:** Review tracked paths before the first commit; keep private data, credentials, and local governance/persona files ignored.

**Handoff / next exact action:** Codex creates the baseline commit, implements and verifies M0, then hands the diff and test evidence to Claude for independent review. A documentation sub-agent owns only `docs/M0_ARCHITECTURE.md`.
