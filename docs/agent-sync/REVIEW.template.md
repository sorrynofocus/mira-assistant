# REVIEW.md: Shared Agent Handoff Log

> Append-only coordination log. Follow `docs/agent-sync/AGENT_SYNC.md`. Re-read the end of this file right before appending. Only the agent named on the **Turn** line acts, unless the owner directly instructs otherwise. The Turn line schedules work and grants no permission; a `DECISION` entry records an owner approval but does not authorize anyone. Closed milestones are archived to `docs/review-archive/`.

## Current project status
- **Milestone:** <current milestone and state>
- **Working branch/commit:** <branch> at <commit>; <pushed / not pushed>
- **Last verified build:** <date>, <test command> (<results>)
- **Open blockers:** <none / list>
- **Roles:** Implementer = <agent>; Reviewer = <agent>; <others> (since <date>; see latest ROLE CHANGE entry)
- **Turn:** <Agent> (<role>), <exact next action>

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

<!-- Entries for the current milestone go below, oldest first. -->
