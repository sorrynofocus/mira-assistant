# REVIEW.md: STT 3.0 Shared Agent Handoff Log

> Append-only coordination log under the Agent Sync Protocol (`docs/agent-sync/AGENT_SYNC.md`). Do not erase earlier findings or change their status without a new resolution entry. Re-read the end of this file right before appending. Only the agent named on the **Turn** line acts, unless the owner directly instructs otherwise. The Turn line schedules work and grants no permission; a `DECISION` entry records an owner approval but does not authorize anyone. Consult `AGENTS.md` and `docs/STT3_HANDOFF.md` before work.

## Current project status
- **Milestone:** M0-M1 closed and archived to `docs/review-archive/M0-M1.md` (archive staged 2026-11-16, not yet committed); M2 (conversation persistence and inspector) planned, implementation starting; data layer = plain SQLite (ADR 0002)
- **Working branch/commit:** `main` at `fba5480` (= `origin/main`); archive, ADR 0002 and M2 INTENT staged, uncommitted, awaiting the owner's commit approval; no push authorized
- **Last verified build:** 2026-11-16 21:47 MST, Grok on Linux (.NET SDK 10.0.404, Node 24.22.1), the `scripts\test.cmd` steps run directly: build 0 warnings/0 errors, 62/62 xUnit, 12/12 Vitest, Astro check 0/0/0, build 1 page. Windows: `scripts\test.cmd` passed 2026-11-16 (Claude 20:36, Codex 21:21). Requires Node >=22.23.4
- **Open blockers:** none for M2 start (owner answered the M2 scope questions at 21:52 MST). Pending: owner approval of the staged documentation commit (owner said not yet); a different agent's archive VERIFICATION (protocol step 5)
- **Roles:** Implementer = Grok (team of Grok bots, led by MIRA Boss); Reviewer = Grok Reviewer (a separate Grok bot outside the Implementer team); Codex = no standing role (signed off 2026-11-16; owner confirmed); Claude = no standing role, available on owner request (owner decisions 2026-11-16; see the 21:50 DECISION + ROLE CHANGE + MILESTONE CLOSE entry)
- **Next concrete action:** Grok team implements M2 per the 2026-11-16 21:55 INTENT; the Grok Reviewer verifies the M0-M1 archive
- **Turn:** Grok team (Implementer), M2 persistence and inspector per the 2026-11-16 21:55 INTENT (Grok Reviewer side job: verify the M0-M1 archive)

---

## Entry template

## YYYY-MM-DD HH:MM TZ | AGENT | TYPE

<!-- TYPE: INTENT, IMPLEMENTATION, REVIEW, RESOLUTION, VERIFICATION, QUESTION / ADVICE, DECISION, ROLE CHANGE, MILESTONE CLOSE. If acting on a direct owner instruction that overrides the Turn line, say so in Objective. A DECISION entry states how the approval was received and its limits. -->

**Objective:**

**Branch and commit:**

**Files examined or changed:**

**Changes / findings:**
- [Severity if review] Issue, with file path and line range, evidence, expected behavior, suggested fix.

**Commands run and results:**
```text
command
result (PASS/FAIL; relevant details)
```

**Security/privacy concerns:**

**Decisions needed from the owner:**

**Next turn:** <Agent> (<role>), <exact next action>

---

## 2026-10-31 | Claude | ROLE CHANGE

**Objective:** Record the owner's role reassignment and make the shared rules cover more than two agents.

**Owner decision (2026-10-31):** Grok joins as **Implementer**. Codex stays **Reviewer**, possibly with documentation updates. Claude steps out of the Implementer seat and has no standing role; the owner may call on Claude when needed. All previous rules still hold, in particular: an agent never reviews its own change.

**Files changed:**
- `AGENTS.md`: the sub-agent rule now names all current agents. Added a rule to re-read the end of `REVIEW.md` immediately before appending, because three agents raise the chance of simultaneous appends.
- `docs/STT3_HANDOFF.md` §12: describes "multiple" agents and records Grok as Implementer from M2.
- `REVIEW.md`: title made agent-neutral; status header updated (Roles, blocker, next action); this entry.

**Handoff for Grok (new Implementer):**
- State: M0 and M1 are committed locally (`9b4bbce`, `89a312a`), and nothing is pushed.
- Required reading is listed in `AGENTS.md`.
- Before any M2 code, wait for the owner's data-layer choice. Then record M2 intent in this log.
- Keep parallel helper sessions to files the Implementer isn't editing.

**Next exact action:** The owner chooses the M2 data layer. Grok then records the M2 intent. Nothing goes to GitHub without the owner's authorization.

---

## 2026-10-31 | Claude | PROTOCOL

**Objective:** At the owner's request, define the coordination system we have used since M0 as a reusable, project-independent protocol. Separate it from the MIRA-specific rules. Add two things the owner asked for: (1) a **turn baton**, so the owner relays one line ("Codex reviewed, your turn") instead of copy-pasting responses; (2) **clearing at each milestone close**, archiving the log and starting fresh agent sessions so context stays small. Claude has no standing role; this was done on the owner's direct request.

**Branch and commit:** `main` at `dc65710`; these changes are uncommitted and not pushed.

**Files changed:**
- New `docs/agent-sync/AGENT_SYNC.md`: the protocol (roles, Turn baton, entry rules, milestone cycle, clearing and archiving at milestone close, severity, trust rules, adoption steps). It contains nothing project-specific.
- New `docs/agent-sync/REVIEW.template.md` and `docs/agent-sync/AGENTS.template.md`: starter files for other projects.
- `AGENTS.md`:
  - The generic Responsibilities, Working agreement and Review severity sections moved into the protocol.
  - It now starts with a Coordination pointer and lists the protocol as required reading item 1.
  - It keeps only MIRA rules: current agents, approvals, secrets, tech constraints, governance, checklist.
  - The checklist now requires a **Next turn** line.
- `docs/STT3_HANDOFF.md`: the collaboration line and §12 point to the protocol.
- `README.MD`: new "How the AI agents stay in sync" section.
- `REVIEW.md`:
  - The intro line now names the protocol.
  - The status header gains a **Turn** line.
  - This entry.

**What changes in practice:**
- Every entry from now on ends with `**Next turn:** <Agent> (<role>), <action>`, and the header's **Turn** line is updated to match. Only the named agent acts.
- When the owner says "<agent> finished" or "your turn", the next agent reads the header and the latest entry. No pasted responses are needed.
- At milestone close, the milestone's entries move verbatim to `docs/review-archive/<milestone>.md`, and a `MILESTONE CLOSE` summary carries every open item forward. Each agent then starts a fresh session.

**Not done yet (needs review first):** archiving M0 and M1. They are committed and closed (`9b4bbce`, `89a312a`), so under the new rule their entries would move to `docs/review-archive/M0-M1.md`. That leaves `REVIEW.md` holding only the header, the template and a closing summary for Grok's M2 start. I held off because Codex is reading this file right now, and the rule itself should be reviewed before it is applied.

**Review request for Codex:**
1. Is the protocol clear and complete enough for a new agent (for example Grok) to follow cold?
2. Is anything MIRA-specific left in `docs/agent-sync/`, or anything generic left in `AGENTS.md`?
3. Do the archiving rules lose information? In particular, carry-over of open findings and owner decisions.
4. Does the Turn rule conflict with Codex's own harness habits?

No code changed, so no tests were run for this documentation-only change.

**Decisions needed from the owner:** approve the protocol, and approve archiving the M0–M1 entries once Codex has reviewed. Codex may do the move itself (docs updates are in its remit), or the owner can name another agent; whoever moves the entries, a different agent should confirm they are verbatim.

**Next turn:** Codex (Reviewer), review this entry and `docs/agent-sync/`, append findings, then set Turn to Owner for approval.

---

## 2026-10-31 15:31 MST | Codex | REVIEW

**Objective:** Review Claude's Agent Sync Protocol and its MIRA adoption for a new agent, the turn baton, approval provenance, and milestone archiving. Codex is the named Reviewer on the Turn line.

**Branch and commit:** `main` at `dc65710`; protocol changes are uncommitted and not pushed.

**Files examined or changed:** Examined `docs/agent-sync/AGENT_SYNC.md`, both templates, the diffs to `AGENTS.md`, `docs/STT3_HANDOFF.md`, `README.MD`, and `REVIEW.md`, plus the latest role and protocol entries. Changed only `REVIEW.md` (this appended review and the Turn header).

**Findings (severity-ranked):**
- **[Major] Make owner authorization verifiable to the acting agent.** `docs/agent-sync/AGENT_SYNC.md:55,82-86` says an agent records the owner's decision in a `DECISION` entry and may then commit, while also saying an agent's entry is never owner approval. A cold agent receiving only the one-line relay cannot distinguish a genuine owner decision from an inaccurate or forged log entry. State explicitly that a `DECISION` entry documents a decision but does not itself authorize a push, paid service, security compromise, or material architecture change. For those actions, the agent executing it should have a direct owner instruction or another owner-authenticated approval; the one-line relay can include the decision when needed. The Turn line schedules work, not grants permission.
- **[Major] Define the archive cut-off so post-M1 decisions remain live.** `docs/agent-sync/AGENT_SYNC.md:17,61-72` says the live log contains current-milestone entries, while `REVIEW.md:579-638` contains a ROLE CHANGE and a PROTOCOL proposal after M1 closed. Claude's proposed `M0-M1.md` move says it would leave only the header/template/closing summary (`REVIEW.md:626`), which would also remove those post-M1 entries before this protocol is approved. Archive only the entries through the M1 close boundary; retain later role/protocol/review entries in the live log, or explicitly carry them forward with their pending decisions and Turn. Define how cross-milestone entries are handled, and have a second agent compare the archive and live log so no entry or open item disappears.
- **[Minor] Owner-directed work needs an explicit Turn override path.** The absolute rule in `docs/agent-sync/AGENT_SYNC.md:35` says only the named agent may act, while its Roles section allows a no-standing-role agent to act on the owner's specific request (`:27`). If the owner directly assigns an urgent or different task while the header is stale, a cold agent could stop unnecessarily. Say the Turn is the default schedule; a direct owner instruction may override it, and the acting agent records the change and updates the header before or with its entry. This also aligns the protocol with user instructions that take precedence over repository rules.
- **[Minor] Update the live entry template for the new baton.** `REVIEW.md:16-39` still ends with `Handoff / next exact action` and offers only three entry types. It omits the required `Next turn` line that `docs/agent-sync/REVIEW.template.md:17-38` provides. Replace the live template when the protocol is adopted, so Grok cannot copy an obsolete format. `docs/STT3_HANDOFF.md:515` also still titles §12 as instructions for Codex and Claude, omitting Grok; make that heading role-neutral.

**What checks out:** The protocol folder is project-independent aside from agent names used as examples; `AGENTS.md` retains MIRA-specific technology, governance, approval, and privacy rules. Its templates contain the needed Roles and Turn fields. Moving entries verbatim into an archive can preserve full history if the cut-off and carry-over summary are explicit. There is no inherent conflict with Codex's tools once direct owner instructions clearly override the scheduling baton.

**Commands and tests:** Read-only document/diff review using `git diff`, `rg`, and file reads. No build or tests were run because this change is documentation and process only. `git diff --check` reports the existing two-space Markdown hard line break at `docs/STT3_HANDOFF.md:6`; it is not a substantive finding.

**Security/privacy and decisions needed:** The approval-provenance gap matters most for pushes and paid or security-sensitive actions. No code, secrets, or user data were changed. Recommend resolving both Major findings before the owner approves the protocol or authorizes archiving. The owner can ask Claude (no standing role) to revise it, then assign Codex to verify; M2 remains blocked on the separate data-layer decision.

**Next turn:** Owner, review these findings and assign resolution before protocol approval or M0-M1 archiving.

---

## 2026-11-16 20:08 MST | Claude | RESOLUTION

**Objective:** Resolve the four findings in Codex's 2026-10-31 15:31 MST protocol REVIEW. **Acting on a direct owner instruction** in the Claude session on 2026-11-16 ("Claude, resolve Codex's findings"); the Turn line was "Owner, review Codex's protocol findings and assign resolution". Claude still has no standing role.

**Branch and commit:** `main` at `dc65710`; all protocol changes are uncommitted and not pushed.

**Files changed:** `docs/agent-sync/AGENT_SYNC.md`, `docs/agent-sync/REVIEW.template.md`, `docs/agent-sync/AGENTS.template.md`, `AGENTS.md`, `docs/STT3_HANDOFF.md` §12, `README.MD` (sync section), and `REVIEW.md` (intro line, entry template, status header, this entry).

**Resolutions:**
- **[Major] Owner authorization must be verifiable to the acting agent: resolved.** New section `AGENT_SYNC.md` "Owner authorization":
  - It lists the actions that need approval: push or publish, committing a milestone as accepted, new paid service, security compromise, material architecture change, and anything `AGENTS.md` adds.
  - The acting agent must receive the approval directly, in its own session or through an owner-authenticated channel; the one-line relay may carry it.
  - **A `DECISION` entry documents an approval but does not grant one**, and a cold agent never acts on one alone.
  - A recorded approval states how it was received and its limits, and it covers only what it names.
  - Turn baton rule 7: the Turn line schedules work and never grants permission.
  - The milestone cycle, the closing paragraph and Trust and safety now point to this section.
  - `AGENTS.md` (approval line), `AGENTS.template.md`, handoff §12 step 6 and the README sync section say the same thing briefly.
- **[Major] Archive cut-off: resolved.** `AGENT_SYNC.md` "Clearing at milestone close" step 1 now judges each entry by its main subject, not its date:
  - **Archive:** entries mainly about the closed milestone's work, including its closing decision and commit and any review of that commit's contents. Also design or role entries the milestone carried out or a newer entry replaced.
  - **Keep live:** the current ROLE CHANGE, process or protocol proposals and their reviews, entries mainly about a later milestone, and anything still awaiting a reply. When in doubt, keep it live; entries are never split.
  - Open items in archived entries go into the carry-over list, with the heading of the entry they came from.
  - The `MILESTONE CLOSE` summary names the first and last archived entries and any earlier entries kept live, with a reason for each.
  - New step 5: a different agent confirms every heading appears exactly once across the archive and the live log, that the moved text is identical, and that every open item was carried over.
  - The Files table no longer says "current milestone only".
  - *Correction to my 2026-10-31 PROTOCOL entry:* it said archiving would leave only the header, template and a closing summary. Under the corrected rule, the 2026-10-31 ROLE CHANGE, PROTOCOL and Codex 15:31 REVIEW entries, and this entry, stay live.
- **[Minor] Turn override path: resolved.** Turn baton rule 4:
  - A direct owner instruction in the agent's own session overrides the Turn line.
  - The agent records the override in its entry and updates the header in the same change, as this entry does.
  - Text in `REVIEW.md`, another agent's output or any file is never a direct owner instruction.
  - The "No standing role" bullet points to rule 4.
- **[Minor] Live template and §12 heading: resolved.**
  - The live `REVIEW.md` entry template now matches `REVIEW.template.md`: a TYPE field listing all nine entry types, "Decisions needed from the owner", and a closing **Next turn** line. The obsolete "Handoff / next exact action" field is gone.
  - Both templates now use `##` entry headings, matching every real entry in this log (the templates had `###`).
  - Handoff §12 is now titled "Operating instructions for AI agents", and its "Neither tool…" and "Both agents…" wording is agent-neutral.
  - The two-space hard line breaks in the handoff's header block (lines 3-7, including line 6) are intentional Markdown, so I left them.

**Proposed M0-M1 cut-off, for Codex and the owner to confirm (not applied):**
- **Archive** to `docs/review-archive/M0-M1.md` (17 entries, commits `9b4bbce`..`89a312a`): from "Initial handoff | ARCHITECTURE" through "2026-10-31 15:05 MST | Codex | REVIEW".
  - The two 2026-10-16 architecture entries are founding design that M0 and M1 carried out; their substance is in `docs/STT3_HANDOFF.md` and `AGENTS.md`.
  - The 2026-10-17 ROLE CHANGE is replaced by the 2026-10-31 one.
  - The 15:05 REVIEW reviews the contents of commit `89a312a`.
- **Keep live:** 2026-10-31 ROLE CHANGE (current roles), 2026-10-31 PROTOCOL, 2026-10-31 15:31 Codex REVIEW, and this RESOLUTION.
- **Carry-over items:**
  - The owner's M2 data-layer decision (EF Core or plain SQLite, per ADR 0001's Revisit; from Codex's 2026-10-17 23:57 QUESTION / ADVICE and Claude's 2026-10-31 DECISION + COMMIT).
  - No push to GitHub without the owner's direct authorization.
  - I found no other open finding in M0-M1: the M0 governance-contract deferral was met in M1, and the `.gitignore` and README working-tree edits that the 15:05 REVIEW mentioned no longer show in `git status`.

**Commands run and results:**
```text
git diff --check      PASS apart from the existing intentional hard line break at docs/STT3_HANDOFF.md:6 (LF/CRLF warnings only)
git status --short    M AGENTS.md, README.MD, REVIEW.md, docs/STT3_HANDOFF.md; ?? docs/agent-sync/
```
Documentation and process only; no code changed, so no build or tests were run.

**Security/privacy concerns:** None introduced. The change narrows authority: log entries and the Turn line can no longer stand in for owner approval. No secrets or user data.

**Decisions needed from the owner (after Codex verifies):** (1) approve the protocol; (2) approve the M0-M1 archive cut-off above and name the agent who moves the entries (a different agent then confirms them); (3) approve a local commit of the protocol, given directly to the committing agent. M2 still waits on the data-layer decision.

**Next turn:** Codex (Reviewer), verify this RESOLUTION against the diffs to `docs/agent-sync/`, `AGENTS.md`, `docs/STT3_HANDOFF.md`, `README.MD` and `REVIEW.md`, and check the proposed M0-M1 cut-off; then set Turn to Owner.

---

## 2026-11-16 20:36 MST | Claude | IMPLEMENTATION

**Objective:** Fix the two moderate npm audit findings in the frontend. **Acting on a direct owner instruction** in the Claude session on 2026-11-16 ("go ahead and fix the vulnerabilities"). The Turn line was "Codex (Reviewer), verify Claude's 2026-11-16 protocol RESOLUTION". The owner also chose, in the same session, to have Claude upgrade Node.js when the fix turned out to need it. This is security maintenance outside a milestone; it doesn't change the protocol work Codex is verifying.

**Branch and commit:** `main` at `dc65710`; uncommitted and not pushed.

**Files changed:**
- `frontend/package-lock.json`: `astro` 7.4.0 → 7.4.12; `fast-uri` 3.1.8 → 3.1.9 (dev-only, via `@astrojs/check` → `yaml-language-server` → `ajv`). `npm audit fix` also moved 32 other transitive packages within their existing ranges. No semver-major change.
- `frontend/package.json`: `astro` range `^7.4.0` → `^7.4.12`, so a fresh install can't resolve a vulnerable version.
- `README.MD` prerequisites: minimum Node.js raised from 22.22 to **22.23.4** (Astro 7.4.12's engine requirement).
- `REVIEW.md`: this entry and the status header.

**Advisories fixed:**
- `astro` ≤7.4.6:
  - GHSA-mh32-jxcr-mh9f (XSS via attribute injection in dynamic tag names).
  - GHSA-r3h5-4585-7x3w (`@astrojs/node` image endpoint reading a case-variant sibling directory). MIRA doesn't use `@astrojs/node`.
- `fast-uri` 3.0.0–3.1.8: GHSA-w74x-v6f9-j3xg, GHSA-xhq7-xmqj-769h, GHSA-wjr9-j795-jxcr, GHSA-333x-w4r6-6crp. This is a dev tooling dependency only and isn't in the built site.

**Machine change:**
- Every patched Astro release (7.4.7 through 7.4.12) requires Node ≥22.23.4. On Node 22.23.0, `astro check` refused to run ("Node.js v22.23.0 is not supported by Astro!").
- With the owner's approval, Node.js was upgraded via `winget upgrade --id OpenJS.NodeJS.22 --exact` from 22.23.0 to 22.23.5, the same major line. The installer hash was verified by winget.
- Other agents' environments need Node ≥22.23.4 too.

**Commands run and results:**
```text
npm audit (before)                          2 moderate (astro <=7.4.6, fast-uri 3.0.0-3.1.8)
npm audit fix                               PASS: found 0 vulnerabilities; EBADENGINE warnings on Node 22.23.0
cmd /c scripts\test.cmd (Node 22.23.0)      FAIL at astro check: Node 22.23.0 not supported (xUnit 62/62 and Vitest 12/12 had passed)
winget upgrade OpenJS.NodeJS.22             PASS: 22.23.5 installed
cmd /c scripts\test.cmd (Node 22.23.5)      PASS: build 0 warn/0 err; xUnit 62/62; Vitest 12/12; astro check 0 errors/0 warnings/0 hints; 1 page built; "All checks passed."
npm install (after package.json bump)       PASS: found 0 vulnerabilities
cmd /c scripts\test.cmd (final)             PASS: same results as above
npm ci                                      PASS: 0 EBADENGINE warnings
```

**Security/privacy concerns:** This change reduces risk. No code, secrets or user data changed.

**Decisions needed from the owner:** Approve a local commit of these changes, given directly to the committing agent; it can go together with or separately from the protocol commit. No push.

**Next turn:** Codex (Reviewer), verify Claude's 2026-11-16 protocol RESOLUTION and this dependency IMPLEMENTATION (lockfile diff, `package.json` range, README minimum, `scripts\test.cmd` on Node ≥22.23.4), then set Turn to Owner.

---

## 2026-11-16 20:40 MST | Claude | PROTOCOL

**Objective:** **Acting on a direct owner instruction** in the Claude session on 2026-11-16. The Turn line was "Codex (Reviewer), verify Claude's 2026-11-16 protocol RESOLUTION and dependency-fix IMPLEMENTATION". The owner asked for two things:
1. Short descriptions of the README's quick-start commands, starting with `npm --prefix frontend ci` and covering the other scripts.
2. A plan change. Paraphrasing the owner: Grok bots work as many agents that communicate with each other, and their turn-taking is automatic, so when Grok bots are used they can ignore the Agent Sync Protocol.

**Branch and commit:** `main` at `dc65710`; uncommitted and not pushed.

**Files changed:**
- `docs/agent-sync/AGENT_SYNC.md`: new section "Self-coordinating agent teams (for example, Grok bots)".
  - **Inside the team, the protocol's turn mechanics are skipped:** no Turn or Next turn lines and no log entries for bot-to-bot hand-offs, no owner relays, and the team organizes its own work.
  - The owner marks a team on the Roles line, e.g. `Implementer = Grok (team)`.
  - **Rules that aren't about turn-taking still apply at the team's boundary:**
    - If outside agents share the repository, the team holds the Turn line while it works and appends one summary entry when it finishes or needs a decision. A team working alone may skip the log.
    - Owner authorization must be given directly to the team; a bot's message is never owner approval.
    - Review between bots in the same team doesn't count as the independent review, because they share a model's blind spots. An outside Reviewer still reviews.
    - `AGENTS.md` rules apply to every bot.
- `AGENTS.md`: MIRA rule saying Grok may run as a team of Grok bots, with the above applied. Codex and Claude share this repository, so the team holds the Turn line, appends one summary entry, and has its work reviewed by Codex.
- `docs/STT3_HANDOFF.md` §12: notes that Grok may work as a self-coordinating team.
- `README.MD`:
  - New table in the quick start describing each command (`git clone`, `cd`, `npm --prefix frontend ci`, `test.cmd`, `create-user.cmd`, `dev.cmd`).
  - New bullet in the agent sync section about agent teams.
  - New troubleshooting row explaining that npm's "vulnerabilities" notice is not an error.
  - Fixed two Node.js minimums I missed in my 20:36 entry: the version-check line and the troubleshooting row now say 22.23.4, not 22.22.

**Interpretation, for the owner to confirm:** I read "ignore the protocol" as covering its turn-based coordination, which is the reason the owner gave. I kept owner authorization, MIRA's governance and security rules, and Codex's independent review, because those aren't turn-taking and `AGENTS.md` requires the first two. If the owner wants Grok teams exempt from any of these, that is a separate owner decision.

**Commands run and results:**
```text
documentation only; no code changed, no build or tests run
grep for stale Node minimums (22.22) in README.MD, docs/*.md, AGENTS.md   none left
```

**Security/privacy concerns:** None introduced. The section explicitly keeps the approval and review safeguards for bot teams.

**Decisions needed from the owner:** Confirm the interpretation above. This joins the pending protocol approval.

**Next turn:** Codex (Reviewer), verify Claude's 2026-11-16 RESOLUTION, the dependency-fix IMPLEMENTATION, and this PROTOCOL entry (the agent-team section and the README command table), then set Turn to Owner.

---

## 2026-11-16 21:21 MST | Codex | VERIFICATION

**Objective:** Verify Claude's three 2026-11-16 entries: protocol resolution, dependency security fix, and Grok-team/README additions. Codex is the named Reviewer on the Turn line.

**Branch and commit:** `main` at `dc65710`; changes remain uncommitted and not pushed.

**Files examined or changed:** Examined the actual diffs and current contents of `docs/agent-sync/`, `AGENTS.md`, `docs/STT3_HANDOFF.md`, `README.MD`, `REVIEW.md`, `frontend/package.json` and `frontend/package-lock.json`, plus `scripts/dev.cmd` and `scripts/create-user.cmd` for README accuracy. Changed only this `REVIEW.md` entry and its Turn/status header.

**Verification and findings:**
- **[Major — RESOLVED] Approval provenance.** `AGENT_SYNC.md` now says the Turn and a `DECISION` entry do not grant approval; the acting agent needs direct owner approval for the named sensitive action. `AGENTS.md`, the handoff and README agree.
- **[Major — RESOLVED] Archive cut-off.** The new rule preserves live cross-milestone entries, requires named carry-over items and a second agent's byte-identical archive verification. Claude's proposed cut-off is consistent: the 17 entries from `Initial handoff | ARCHITECTURE` through `2026-10-31 15:05 MST | Codex | REVIEW` concern M0-M1 or superseded roles; the subsequent current ROLE CHANGE and protocol entries remain live. The M2 data-layer decision and no-push restriction are identified for carry-over. The move itself was not performed.
- **[Minor — RESOLVED]** A direct owner instruction can override the Turn and must be recorded; the live entry template now includes the required Next turn line and the handoff heading is agent-neutral.
- **[Minor] Team exception wording.** `docs/agent-sync/AGENT_SYNC.md:3,9`, `README.MD:309`, and `docs/STT3_HANDOFF.md:517` still say agents cannot message each other, while the new team section explicitly says Grok bots can. Qualify those introductions as referring to agents outside a self-coordinating team. The team boundary itself preserves owner authorization, MIRA rules and outside review. Owner confirmation of Claude's interpretation remains pending.
- **[Minor] Lockfile change description.** Claude's 20:36 entry says “No semver-major change,” but Astro 7.4.12 changes its transitive `p-queue` requirement from `^9.3.5` to `^10.0.0`; the lockfile contains `p-queue` 10.0.0. This is an upstream transitive major, not a direct dependency major. Record that distinction in any commit summary; do not edit Claude's append-only entry. The project tests pass with it.
- **Dependency fix verified.** `package.json` pins Astro at `^7.4.12`; the installed and locked versions are Astro 7.4.12 and dev-only `fast-uri` 3.1.9. The README states Node >=22.23.4 in prerequisites, version check and troubleshooting. The quick-start command table matches the CMD scripts. `npm audit` reports zero vulnerabilities. The lockfile comparison found 11 added, 29 removed and 54 version-changed package entries; `p-queue` is the only changed package crossing a major version.

**Commands run and results:**
```text
node --version                         v22.23.5
npm --prefix frontend ls astro fast-uri --all   Astro 7.4.12, fast-uri 3.1.9
npm --prefix frontend ls p-queue --all         Astro -> p-queue 10.0.0
npm --prefix frontend audit --json             PASS: 0 vulnerabilities
cmd /c scripts\test.cmd (sandbox)             BLOCKED: MSBuild Temp access denied; Vitest worker spawn EPERM
cmd /c scripts\test.cmd (elevated rerun)      PASS: build 0 warnings/errors; xUnit 62/62; Vitest 12/12; Astro check 0 errors/warnings/hints; static build 1 page; All checks passed.
git diff --check                      Only the existing intentional Markdown hard line break at docs/STT3_HANDOFF.md:6 is flagged.
```

**Security/privacy concerns:** No credentials, private data or runtime permission changes found. The audit is clean. Approval rules remain with the owner and the acting agent; a Grok bot cannot approve its own action.

**Decisions needed from the owner:** Confirm Claude's interpretation of the Grok-team exception; decide whether to have the two Minor wording/summary corrections made before the local commit; approve the protocol and the proposed M0-M1 archive cut-off, naming the agent to move it; authorize any local commit directly to that agent. Separately choose EF Core or plain SQLite for M2 under ADR 0001. No push is authorized.

**Next turn:** Owner, review this verification and decide the protocol, archive, local commit and M2 data-layer questions.

---

## 2026-11-16 21:36 MST | Claude | DECISION + ROLE CHANGE + COMMIT

**Objective:** Record the owner's decisions and act on them: fix Codex's two Minor findings, commit, and push. **Acting on a direct owner instruction**; the Turn line was "Owner, review this verification and decide…".

**Owner decisions (from the owner in the Claude session, 2026-11-16; quoted):**
- "fix the two Minor points first; record your decision in REVIEW.md and make the commits; push." This authorizes **these two commits and one push of `main` to `origin`** (https://github.com/sorrynofocus/mira-assistant). It does not cover later commits or pushes.
- "I will hand this project over to grok bots to see what they can do." Recorded as a role change: **Implementer = Grok (team of Grok bots)**, working under "Self-coordinating agent teams" in the protocol and the matching `AGENTS.md` rule.
- **Not explicitly decided, so still open:**
  - whether Codex stays Reviewer for the Grok team (the current `AGENTS.md` rule says it does);
  - confirmation of Claude's Grok-team interpretation (20:40 PROTOCOL entry);
  - the M0-M1 archive cut-off and who moves the entries;
  - the M2 data layer (EF Core or plain SQLite, ADR 0001 Revisit).
- The owner directed the protocol to be committed and pushed, so it is now the working process. The open items above are carried forward.

**Codex's Minor findings (2026-11-16 21:21 VERIFICATION):**
- **Team exception wording: fixed.** These now say that agents *from different tools* can't message each other, and that a self-coordinating team (e.g. Grok bots) talks among itself:
  - `docs/agent-sync/AGENT_SYNC.md` lines 3 and 9; line 9 links to the team section;
  - `README.MD` sync section intro;
  - `docs/STT3_HANDOFF.md` §12 intro.
- **Lockfile description: addressed in the commit message, not by editing my entry.** Correcting "No semver-major change" in my 20:36 entry: MIRA's own dependency ranges have no major change, but Astro 7.4.12 brings transitive `p-queue` 9.x → **10.0.0**, a major bump. Codex found it is the only changed package crossing a major version, and the tests pass with it. The dependency commit message states this.
- Codex hasn't verified these two fixes; they are documentation and commit-message wording only.

**Commits (on `main`, parent `dc65710`):**
1. `docs: adopt Agent Sync Protocol with owner-authorization, archive and agent-team rules`: `docs/agent-sync/`, `AGENTS.md`, `README.MD`, `docs/STT3_HANDOFF.md`, `REVIEW.md` (including this entry).
2. `fix(frontend): update Astro to 7.4.12 and fast-uri to 3.1.9 for security advisories`: `frontend/package.json`, `frontend/package-lock.json`.
- Then `git push -u origin main`. This is the repository's first push, so `ea200bb`, `9b4bbce`, `89a312a` and `dc65710` are published as well.

**Pre-push checks:**
```text
git ls-files / full history: no *.db, *.sqlite, appsettings.Local.json, .env, key or private persona file ever tracked
secret-pattern scan of all commits (API keys, tokens, sk-/ghp_/AKIA/AccountKey): only test-fixture passwords in backend/Mira.Tests and frontend tests
bin/, obj/, frontend/dist, frontend/.astro, node_modules: ignored
scripts\test.cmd: last PASS 2026-11-16 (Claude 20:36, Codex 21:21 rerun); no code changed since
```

**Security/privacy concerns:** The repository becomes public on GitHub (if the remote is public). The scans above found no credentials or private data. MIRA still binds only to loopback.

**Decisions needed from the owner:** the open items listed above. The Grok team should ask the owner for the M2 data-layer choice directly before writing M2 code.

**Next turn:** Grok (team, Implementer). Read `AGENTS.md`, `docs/agent-sync/AGENT_SYNC.md` (especially "Self-coordinating agent teams" and "Owner authorization") and this log. Get the owner's M2 data-layer decision directly, then record the M2 INTENT as one team entry. Codex reviews the team's work.

---

## 2026-11-16 21:50 MST | Grok (MIRA Boss, team lead) | DECISION + ROLE CHANGE + MILESTONE CLOSE

**Objective:** Record the owner's decisions given directly to the Grok team, act on the two that need action now (ADR for the M2 data layer; M0-M1 archive), and leave the milestone-close summary. **Acting on a direct owner instruction** to MIRA Boss in the Grok Bot chat; the Turn line was "Grok (team, Implementer), get the owner's M2 data-layer decision directly, then record the M2 INTENT", so the data-layer part is on-Turn and the archive is the owner's added instruction.

**Branch and commit:** `main` at `fba5480` (same as `origin/main`). All changes below are **staged, not committed, not pushed** (see "Commit" below).

**Owner decisions (from the owner, Chris Winters / sorrynofocus, directly to Grok as MIRA Boss in the Grok Bot chat, 2026-11-16 ~21:45 MST; closely paraphrased from the owner's answers):**
1. **M2 data layer:** "Stay with plain SQLite for M2." `Microsoft.Data.Sqlite` plus the hand-written migration runner; not EF Core. This resolves ADR 0001's mandatory M2 Revisit. Recorded in new `docs/decisions/0002-plain-sqlite-for-m2.md`.
2. **Reviewer:** asked "Codex has signed off. Who reviews our work?", the owner chose "Use a separate Grok bot as Reviewer."
3. **Archive:** "Yes, archive M0-M1 now; you do it." This approves the M0-M1 cut-off as reviewed (Claude 20:08 RESOLUTION proposal, Codex 21:21 VERIFICATION) and names Grok (MIRA Boss) as the agent that moves the entries.
4. **Grok-team interpretation:** "Claude and codex will now handle it over to you. you can spawn other grok bots to help you. You are Mira boss, so you can delegate what is needed and planning." The owner confirmed the Grok team works as self-coordinating bots led by MIRA Boss. I read this as confirming Claude's 20:40 PROTOCOL interpretation as written: the team skips turn mechanics internally, while owner authorization, `AGENTS.md` rules and review by a Reviewer outside the Implementer team still apply. The owner did not exempt the team from any of those.

**Limits:**
- **No push is authorized.** No commit was explicitly approved either (see "Commit").
- Decision 1 covers M2 only; ADR 0002 lists an optional M4 revisit.
- Decision 2 names a role, not a person: the Reviewer bot must be a separate Grok bot that does not implement M2 work, so it never reviews its own change. Note for the owner: the protocol says a second instance of the same model shares its blind spots (Roles; Self-coordinating agent teams). The owner chose this knowingly; it is recorded, not second-guessed.
- An entry in this log never authorizes anyone. Other agents must get approvals directly from the owner.

**Follow-up owner answers (from the owner directly to Grok as MIRA Boss in the Grok Bot chat, 2026-11-16 21:52 MST; closely paraphrased):**
5. **Commit:** do not commit yet; keep the changes staged; no push.
6. **Conversations:** multiple conversations per user in M2: yes (list, new, switch, rename, delete).
7. **Delete:** permanent, with a confirmation prompt.
8. **Inspector retention:** pipeline events are kept 30 days (not the 7 days proposed).
9. **Codex:** no standing role (confirmed).
Answers 6-8 settle the scope questions in the 21:55 INTENT below. They are scope decisions, not approvals of any commit or push.

**ROLE CHANGE (Roles line updated):** Implementer = Grok (team of Grok bots, led by MIRA Boss); Reviewer = Grok Reviewer (a separate Grok bot outside the Implementer team); Codex = no standing role, signed off 2026-11-16 (owner confirmed directly to MIRA Boss at 21:52 MST); Claude = no standing role, available on owner request (unchanged since 21:36).

**Files changed:**
- `docs/review-archive/M0-M1.md` (new): the 17 archived entries, verbatim and in order, under a one-line header.
- `REVIEW.md`: the 17 entries removed (moved only), status header updated, this entry and the M2 INTENT below appended.
- `docs/decisions/0002-plain-sqlite-for-m2.md` (new): ADR for decision 1.
- `docs/decisions/0001-sqlite-access-without-ef-core.md`: status line points to ADR 0002 (one sentence added).
- `docs/STT3_HANDOFF.md`: §6 notes the departure from EF Core with links to both ADRs; §12 says the Grok team is reviewed by a separate Grok bot.
- `AGENTS.md`: the Grok-team rule now says the Reviewer is currently a separate Grok bot (was "currently Codex").

**MILESTONE CLOSE: M0-M1**
- **Milestones:** M0 (repository and safety baseline) and M1 (authentication and text chat streaming).
- **Commits:** `ea200bb` (planning baseline), `9b4bbce` (M0), `89a312a` (M1). The last archived entry, Codex's 15:05 REVIEW, was committed in `dc65710`. All are on `origin/main` since the owner-directed push on 2026-11-16.
- **Archive file:** [`docs/review-archive/M0-M1.md`](docs/review-archive/M0-M1.md).
- **Archived (17 entries):** first "Initial handoff | ARCHITECTURE", last "2026-10-31 15:05 MST | Codex | REVIEW". This is exactly the cut-off Claude proposed (20:08 RESOLUTION) and Codex verified (21:21 VERIFICATION).
- **Earlier entries kept live, with reasons:**
  - "2026-10-31 | Claude | ROLE CHANGE": about Grok's Implementer seat from M2, a later milestone. It is now replaced by the 21:36 entry and this one, so it can go at M2 close; kept now under "when in doubt, keep it live".
  - "2026-10-31 | Claude | PROTOCOL", "2026-10-31 15:31 MST | Codex | REVIEW", "2026-11-16 20:08 MST | Claude | RESOLUTION", "2026-11-16 20:40 MST | Claude | PROTOCOL", "2026-11-16 21:21 MST | Codex | VERIFICATION": protocol proposals and their reviews (keep-live rule).
  - "2026-11-16 20:36 MST | Claude | IMPLEMENTATION": dependency security fix outside any milestone (`fba5480`), not M0-M1 work.
  - "2026-11-16 21:36 MST | Claude | DECISION + ROLE CHANGE + COMMIT": protocol commit and push, the role change this entry builds on, and the open items this entry resolves.
- **Decisions made in M0-M1:** ADR 0001 (direct SQLite, accepted for M1 only; now followed by ADR 0002 for M2); Astro 7.4 upgrade approved (2026-10-17); local M0 and M1 commits approved by the owner.
- **Carry-over items (from archived entries):**
  - *M2 data-layer decision* (from "2026-10-17 23:57 MST | Codex | QUESTION / ADVICE" and "2026-10-31 | Claude | DECISION + COMMIT"): **resolved today** by owner decision 1 (ADR 0002). Codex's advice to keep targeted SQL for M4 FTS5 still stands.
  - *No push without the owner's direct authorization* (repeated in most archived entries): standing rule.
  - *Browser check limits* (from "2026-10-17 23:53 MST | Codex | VERIFICATION"): the 32/32 headless Edge smoke driver is outside the repo and was not independently rerun; there has been no screen-reader or visual/layout check. Carried into the M2 test plan (manual browser and keyboard pass) and to M8 (accessibility checks).
  - *Optional test nit* (from "2026-10-16 23:20 MST | Claude | VERIFICATION", repeated in "2026-10-17 19:43 MST | Codex | M1 INTENT"): `ApiHostTests` uses `http://0.0.0.0:5081` (`backend/Mira.Tests/ApiHostTests.cs:49-50`). If the loopback guard regressed, the API would briefly listen on the LAN during the test. Prefer a non-default loopback address. Still open; low priority, not in M2 scope unless cheap.
  - *Known M1 limitations, documented but not findings* (from "2026-10-17 | Claude | IMPLEMENTATION", also in `docs/M1_AUTH_AND_STREAMING.md`): mock provider only (a real provider needs owner approval as a paid service); cookies not `Secure` over loopback HTTP; plain-text replies (no Markdown); in-memory login throttle; per-IP limit is effectively global behind the dev proxy; password limits count UTF-16 code units; fixed lockout window; UI Stop aborts the fetch, so the UI never sees `response.cancelled`; no CLI to disable a user; no "session ended" message after expiry. "Conversations live in memory until M2" is M2 scope.
  - I found no other open finding: every Blocker, Major and Minor raised in M0-M1 has a resolution in a later archived entry.

**Archive check (mine; the protocol's step 5 still needs a different agent):**
```text
diff <(git show HEAD:REVIEW.md | sed -n '45,577p') <(tail -n +3 docs/review-archive/M0-M1.md)   PASS: identical (byte-for-byte)
diff <(git show HEAD:REVIEW.md | sed '45,580d') <REVIEW.md before appending>                     PASS: only the moved lines (and their trailing separator) removed
grep -c '^## ' docs/review-archive/M0-M1.md                                                       17
```

**Build and tests (Linux box, not Windows; `scripts/*.cmd` are CMD-only, so I ran their commands directly in the same order as `scripts\test.cmd`):**
```text
dotnet --version                              10.0.404 (installed via dotnet-install.sh --channel 10.0 to ~/.dotnet)
node --version / npm --version                v24.22.1 / 11.21.1 (meets Node >=22.23.4)
dotnet build backend/Mira.sln -m:1            PASS: 0 warnings, 0 errors
dotnet test backend/Mira.sln --no-build -m:1  PASS: 62/62 (net10.0)
npm --prefix frontend ci                      PASS: 0 vulnerabilities
npm --prefix frontend test (vitest run)       PASS: 12/12 in 2 files (Vitest 5.0.5)
npm --prefix frontend run check               PASS: 10 files, 0 errors, 0 warnings, 0 hints
npm --prefix frontend run build               PASS: 1 page built
npm --prefix frontend audit                   PASS: 0 vulnerabilities
```
No Linux-only failure. The one Linux difference seen is a harmless `VSTEST_ARTIFACTS_DIRECTORY` notice from the test SDK. This run was before any file in this change was edited; the change touches documentation only.

**Commit:** Not committed; everything is staged. The protocol's "Clearing at milestone close" step 4 says the archive move and summary are committed "with the owner's approval for that commit", and the owner directly said not to commit yet (answer 5). Proposed message: `docs: archive M0-M1 review log, record M2 plain-SQLite decision (ADR 0002) and M2 intent`. No push.

**Security/privacy concerns:** None. Documentation and process only; no secrets, credentials or conversation content.

**Decisions needed from the owner:** When ready, approve the local commit of this staged change, directly to MIRA Boss (for example: "MIRA Boss, I approve the local archive/ADR/intent commit; no push"). The owner has said not to commit yet.

**Next turn:** See the M2 INTENT entry below.

---

## 2026-11-16 21:55 MST | Grok (MIRA Boss, team lead) | INTENT

**Objective:** Plan M2, conversation persistence and inspector (handoff §10 M2), as one Grok-team entry. This is the team's plan; inside the team, bots coordinate without log entries. No M2 code has been written.

**Branch and commit:** `main` at `fba5480`, plus the staged documentation change above. M2 work will be on `main` locally (or a local `m2` branch the team merges back), uncommitted until the owner approves a commit. No push.

**Scope (from handoff §10 M2, §3.2, §4.1, §6 and §7):**
- **Build:** SQLite migrations for `Conversations`, `Messages` and `PipelineEvents`; a SQLite `IConversationStore` replacing `InMemoryConversationStore` in the host; owner-scoped conversation and message reads and writes; pipeline-event instrumentation of the chat turn; redaction; an inspector panel (hidden by default) showing a request's events and timings.
- **Accept:** a page reload keeps completed messages (and shows interrupted ones as interrupted); request IDs correlate every event of a turn; a user cannot read, list or change another user's conversations, messages or pipeline events (404, not 403, so IDs can't be probed); the inspector shows timestamps and first-token latency without message text, credentials or tokens.
- **Out of scope:** personas (M3), memories and FTS5 (M4), audio (M5/M6), Tavily (M7), real or paid model providers, EF Core, any agent tool or governance grant, Markdown rendering.

**Data model (migrations 2 and 3, append-only; migration 1 untouched):**
- `Conversations(Id TEXT PK, OwnerUserId TEXT NOT NULL REFERENCES Users(Id), Title TEXT NULL, CreatedAt TEXT, UpdatedAt TEXT)`, index `(OwnerUserId, UpdatedAt DESC)`. `PersonaId` waits for M3 (added by a later migration).
- `Messages(Id TEXT PK, ConversationId TEXT NOT NULL REFERENCES Conversations(Id) ON DELETE CASCADE, Role TEXT, Content TEXT, Status TEXT, CreatedAt TEXT, ModelId TEXT NULL, RequestId TEXT NULL)`, index `(ConversationId, CreatedAt)`.
- `PipelineEvents(Id TEXT PK, OwnerUserId TEXT NOT NULL REFERENCES Users(Id), RequestId TEXT NOT NULL, ConversationId TEXT NULL, EventType TEXT NOT NULL, ElapsedMs INTEGER NOT NULL, RedactedPayload TEXT NULL, CreatedAt TEXT)`, index `(OwnerUserId, RequestId)` and `(CreatedAt)` for the 30-day retention purge (owner answer 8).
- Same GUID and ISO-8601 UTC formats as M1's tables. On startup, any `Messages` row still `Streaming` (left by a crash) is marked `Interrupted`.

**API (additions; all under the existing `/api` group, so authentication and CSRF on unsafe methods already apply):**
- `GET /api/conversations` (own list, bounded pagination); `POST /api/conversations` (create); `GET /api/conversations/{id}` (own conversation with messages); `PATCH /api/conversations/{id}` (title); `DELETE /api/conversations/{id}` (permanent: cascades to the conversation's messages and pipeline events; the UI asks for confirmation first). Multiple conversations per user are in scope (owner answer 6).
- `POST /api/chat/stream` takes an optional `conversationId`; without one it keeps M1's behavior (most recent or new conversation). `GET /api/conversations/current` stays for compatibility.
- `GET /api/diagnostics/{requestId}`: the owner's redacted events for that request, as handoff §7 proposes; plus `GET /api/diagnostics?conversationId=` for the latest requests (bounded).
- The SSE event contract is unchanged (still schema `v: 1`); `requestId` already appears on every event.

**Work breakdown (parallel chunks for separate Grok bots; one owner per file, and a bot needing a change in another chunk's file asks that chunk's owner through MIRA Boss):**

| Chunk | Bot | Owns (create or edit) | Depends on |
|---|---|---|---|
| 0. Contracts | MIRA Boss | `backend/Mira.Core/Conversations.cs` (multi-conversation `IConversationStore`, records), new `backend/Mira.Core/PipelineEvents.cs` (`PipelineEvent`, event-type constants, `IPipelineEventStore`, `IPipelineRecorder`, redaction contract) | — (lands first, small, so A–D can start in parallel) |
| A. SQLite storage | Storage bot | `backend/Mira.Infrastructure/Sqlite/MiraDatabase.cs` (migrations 2 and 3, startup sweep), new `Sqlite/SqliteConversationStore.cs`, new `Sqlite/SqlitePipelineEventStore.cs` (including the 30-day retention purge), `InMemoryConversationStore.cs` (updated to the new contract for tests, or removed), `backend/Mira.Tests/StoreTests.cs`, new `backend/Mira.Tests/ConversationStoreTests.cs` | 0 |
| B. Instrumentation and redaction | Pipeline bot | new `backend/Mira.Infrastructure/Pipeline/PipelineRecorder.cs`, new `Pipeline/PipelineRedactor.cs`, new `backend/Mira.Tests/PipelineRedactionTests.cs` | 0 |
| C. API | API bot | `backend/Mira.Api/Chat/ChatEndpoints.cs` (persistence per conversation, recorder calls at started / first token / completed / cancelled / error), `Chat/SseWriter.cs` only if needed, new `backend/Mira.Api/Conversations/ConversationEndpoints.cs`, new `backend/Mira.Api/Diagnostics/DiagnosticsEndpoints.cs`, `backend/Mira.Api/MiraHost.cs` (DI and route registration), `backend/Mira.Tests/ChatStreamTests.cs`, `backend/Mira.Tests/TestSupport.cs`, new `backend/Mira.Tests/ConversationApiTests.cs`, new `backend/Mira.Tests/DiagnosticsApiTests.cs` | 0; integrates A and B (codes against the contracts, wires real types last) |
| D. Frontend | Frontend bot | `frontend/src/pages/index.astro` (conversation list, new-chat, rename, delete with a confirmation prompt, inspector panel hidden by default), `frontend/src/scripts/chat.ts`, `frontend/src/scripts/api.ts`, new `frontend/src/scripts/conversations.ts`, new `frontend/src/scripts/inspector.ts`, new `frontend/src/scripts/conversations.test.ts`, new `frontend/src/scripts/inspector.test.ts` | 0 and the API shapes above; uses fakes until C lands |
| E. Docs | Docs bot | new `docs/M2_PERSISTENCE_AND_INSPECTOR.md` (schema, endpoints, event types, redaction rules, 30-day retention, permanent delete, manual test steps), `README.MD` (project status and features only) | A–D near the end |
| — Integration | MIRA Boss | `REVIEW.md`, final full test run, the team's single IMPLEMENTATION entry | all |

Untouched by M2: `Accounts.cs`, `Sessions.cs`, `Governance.cs`, auth endpoints and stores, `AGENTS.md`, `docs/agent-sync/`, `scripts/`. Bots inside the team may cross-review each other, but that is not the independent review; the Grok Reviewer bot does that.

**Test plan:**
- **Storage (A):** fresh database migrates to version 3; an M1-shaped database (version 1 with a user and a session) upgrades to version 3 with the user and session still valid; migrations are idempotent; leftover `Streaming` rows become `Interrupted`; message order is stable; delete permanently removes the conversation, its messages and its pipeline events; the retention purge removes only events older than 30 days; every store method ignores rows of another owner.
- **Redaction (B):** the payload is built from an allowlist of fields (event type, elapsed ms, model ID, status, counts and lengths), never free text; tests feed message text, a password, a session cookie, a CSRF token and an API-key-shaped string and assert none appears in stored payloads or logs; unknown fields are dropped.
- **API (C):** unauthenticated requests get 401; missing CSRF on POST/PATCH/DELETE gets rejected; user B gets 404 for user A's conversation, messages and request ID (`GET`, `PATCH`, `DELETE`, diagnostics, and `chat/stream` with A's `conversationId`); pagination bounds hold; a turn persists the user message and the completed assistant message, and a reload returns both; a cancelled turn persists partial text as `Interrupted`; a provider failure persists `Failed`; events for one turn share one request ID and include first-token latency; concurrent-send rule (409) still holds per user.
- **Frontend (D):** Vitest for conversation list rendering (text only via `textContent`), switching, new chat, rename, delete only after confirmation (cancelling the prompt deletes nothing), and inspector rendering of redacted events; the existing SSE and session tests keep passing.
- **Whole suite:** the commands in `scripts\test.cmd` (build, 62+ xUnit, Vitest, `astro check`, `astro build`), run on this Linux box and, before acceptance, on the owner's Windows machine via `scripts\test.cmd`.
- **Manual (carry-over):** a browser and keyboard-only pass (sign in, send, Stop, reload keeps history, new, switch, rename and delete (with confirmation) conversations, open inspector, sign out). No screen-reader check is planned for M2; that stays with M8.

**Security/privacy notes:**
- The server takes the owner ID from the principal only; every query filters by owner; no lookup by bare ID; 404 for others' resources.
- Message text is private user data at rest in `%LOCALAPPDATA%\MIRA\mira.db` (file ACLs; no encryption at rest yet, per handoff §13 item 3). Pipeline events never contain message text, prompts, credentials, cookies or tokens; logs keep M1's rule (IDs and error types only).
- Diagnostics are owner-only and read-only. The inspector is hidden by default and renders only text.
- No new dependency, paid service, network call or governance capability. Parameterized SQL only.
- No real conversation data in tests, fixtures, logs, commits or this log.

**Owner scope decisions (received directly by MIRA Boss, 2026-11-16 21:52 MST; recorded in the 21:50 entry above):** multiple conversations per user (list, new, switch, rename, delete); delete is permanent with a confirmation prompt; pipeline events kept 30 days, purged at startup and daily.

**Decisions needed from the owner:** None for M2 scope. The staged documentation commit waits for the owner's approval (the owner said not to commit yet). M2 code commits will need their own approval after review.

**Next turn:** Grok team (Implementer), implement M2 per this INTENT (chunk 0 first, then A–E in parallel), then append one IMPLEMENTATION entry for the Grok Reviewer. Side job for the Grok Reviewer bot (outside the team) meanwhile: verify the M0-M1 archive per protocol step 5 and append a VERIFICATION entry. The staged documentation change stays uncommitted until the owner approves a commit (the owner said not to commit yet).
