# REVIEW.md: STT 3.0 Codex / Claude Shared Handoff Log

> Append entries chronologically. Do not erase earlier findings or change their status without a new resolution entry. Consult `AGENTS.md` and `docs/STT3_HANDOFF.md` before work.

## Current project status
- **Milestone:** M0 implemented and approved by Claude; M1 not started
- **Working branch/commit:** `main`; local M0 commit (see `git log -1`)
- **Last verified build:** 2026-10-16 23:20 MST, `scripts\test.cmd` passed (12/12 .NET tests, Astro check and build)
- **Open blockers:** None for M0; expand the governance contract before its first caller in M1
- **Next concrete action:** Record M1 intent before M1 code; keep GitHub publication on hold until the owner authorizes it.

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

---

## 2026-10-16 22:46 MST | Codex | IMPLEMENTATION HANDOFF

**Objective:** Complete the M0 local scaffold and hand it to Claude for independent review. Do not begin M1.

**Branch and commit:** `main`; local planning baseline `ea200bb` (`docs: establish MIRA planning baseline`). M0 changes remain uncommitted. No GitHub push was completed; the owner asked to keep the work local.

**Files examined or changed:** `.gitignore`, `README.MD`, `CLAUDE.md`, `docs/M0_ARCHITECTURE.md`, `backend/MIRA.sln` and its four projects, `frontend/`, `scripts/dev.cmd`, `scripts/test.cmd`, and this append-only log. The documentation sub-agent owned only `docs/M0_ARCHITECTURE.md`; Codex reviewed it.

**Changes / findings:**
- Added a loopback-bound .NET 10 Minimal API with `/health/live`, validated mock-only configuration, independent chat/transcription/speech provider contracts, deterministic mocks, and a default-deny `IGovernanceService` implementation. No model or action endpoint is exposed.
- Added a static Astro/vanilla TypeScript page with an accessible API status indicator. The development server proxies `/health` to the local API. Added CMD development/test scripts and local setup instructions.
- Renamed `CLAUDE.MD` on disk to `CLAUDE.md` and aligned its include directive casing. Reconciled the README acronym with the approved MIRA expansion.

**Commands run and results:**
```text
dotnet --version                                      PASS: 10.0.402
node --version                                        PASS: v22.23.0
dotnet build backend/MIRA.sln --no-restore -m:1       PASS: 0 warnings, 0 errors
dotnet test backend/MIRA.sln --no-restore -m:1        PASS: 4/4
cmd /c scripts\test.cmd                              PASS: 4/4 tests; Astro check 0 diagnostics; static build 1 page
GET http://127.0.0.1:4321/                       PASS: HTTP 200
GET http://127.0.0.1:4321/health/live            PASS: {"status":"ok"} via dev proxy
npm audit --json                                      PASS: 0 reported vulnerabilities after install
git check-ignore (private config, DB, build output)  PASS: all checked paths ignored
```
The first sandboxed .NET restore stalled and the sandboxed Astro build hit an entrypoint resolution restriction. Both checks passed with the required access. Orphaned MSBuild workers from the stalled run were stopped. Local development servers were stopped after smoke checks.

**Security/privacy concerns:** API listens on `127.0.0.1:5080` only. The health payload contains no configuration or user data. Mock providers need no credentials; governance denies every capability. No private data, API keys, or real conversation data were added. `node_modules`, build output, SQLite files, local configuration, and `data/` are ignored.

**Open questions / decisions needed:** Claude should verify M0 naming and security boundaries, particularly the governance contract and the case-sensitive `CLAUDE.md` path in the Git diff. The current local commit still records the old filename casing until the M0 changes are committed later.

**Handoff / next exact action:** Claude reviews the local M0 working tree against `ea200bb`, reruns builds/tests from CMD, and appends severity-ranked findings with file and line references. Codex addresses findings and records evidence before any M1 work. Keep all changes local until the owner authorizes publication.

---

## 2026-10-16 22:55 MST | Claude | REVIEW

**Objective:** Independent review of the M0 scaffold staged against local baseline `ea200bb`, before any M1 work.

**Branch and commit:** `main`; baseline `ea200bb`; reviewed the staged index (`git diff --cached ea200bb`, 30 files). No unstaged or untracked changes were present at review time. This entry is the only change Claude made.

**Files examined or changed:** Examined every staged file except the generated `frontend/package-lock.json` and `backend/MIRA.sln`, which I only checked by stat. Changed: `REVIEW.md` (this entry only).

**Changes / findings:**
- [Major] `backend/Mira.Api/Program.cs:13`: loopback binding is a default, not an enforced boundary. Any `Kestrel:Endpoints` configuration silently replaces `UseUrls("http://127.0.0.1:5080")` and logs only a warning. That configuration can come from plain env vars, `MIRA_Kestrel__...`, or the private `appsettings.Local.json` the README tells users to create. Evidence: starting with `Kestrel__Endpoints__Alt__Url=http://127.0.0.2:5081` logged `Overriding address(es) 'http://127.0.0.1:5080'` followed by `Now listening on: http://127.0.0.2:5081`. A `0.0.0.0` value would expose the unauthenticated API on the LAN, against handoff §2 and §5.5, and it contradicts this log's claim that the API "listens on 127.0.0.1:5080 only". Expected: startup refuses any non-loopback address until a reviewed remote-deployment milestone. Suggested fix: after start, read `IServerAddressesFeature` (or validate the `Kestrel:Endpoints` and `urls` configuration up front) and fail fast if any host is not loopback. Add a test for it.
- [Minor] `Program.cs:7-8`, `README.MD` (config paragraph): the `MIRA_` prefix is stripped before keys are mapped, so the working form is `MIRA_Mira__ProviderMode`. `MIRA_ProviderMode` is silently ignored. Also, because `appsettings.Local.json` is added after the default sources, it overrides command-line arguments and unprefixed env vars. Either document the exact variable form and the precedence, or drop the custom prefix, since the defaults already load env vars.
- [Minor] `backend/Mira.Api/Properties/launchSettings.json:8,17`: the profiles advertise `localhost:5299`, `https://localhost:7241`, and `launchBrowser: true`, but the app always binds to 5080. Running from VS, or `dotnet run` without `--no-launch-profile`, opens a dead URL and implies HTTPS. Remove the file or align it to `http://127.0.0.1:5080` with no browser launch.
- [Minor] `backend/Mira.Core/Governance.cs:3-9`: deny-by-default is correct for M0. `DenyAllGovernanceService` is the only registered implementation, nothing calls it yet, and no capability or action endpoint exists. Verified: the only route is `/health/live`, and other paths return 404. The contract is thinner than `config/governance.example.md` §Enforcement contract, though. `CapabilityRequest` has no owner identity or grant/expiry context, `Action` is a free string rather than the handoff's action classes (`read`/`write`/`delete`/`execute`), and decisions aren't audited (AGENTS.md: "audit every decision"). This doesn't block M0, but extend the contract (owner from the authenticated principal, typed action, audit hook) before the first caller exists, at the latest when M1 auth lands.
- [Minor] `backend/Mira.Tests/M0BoundaryTests.cs`: all 4 tests are unit tests. Nothing exercises the host: the `/health/live` response, `AllowedHosts` rejection, startup failure on an invalid `ProviderMode` through the real configuration pipeline, the DI resolving `IGovernanceService` to the deny-all implementation, or the loopback check above. M1's auth/CSRF acceptance will need a `WebApplicationFactory` harness anyway, so add it now with these cases.
- [Minor] `docs/M0_ARCHITECTURE.md:3,28`: the note still says it "does not claim that the application is implemented" and says to document the ports "once implemented". The diagram also omits the governance boundary. Update it to describe what was built, including ports 5080 and 4321, the Vite `/health` proxy, and `IGovernanceService`.
- [Minor] `.gitignore`: there's no `*.log` / `logs/` entry, although handoff §9 says logs with private text stay out of source control. `**/appsettings.Local.json` only matches that exact casing on case-sensitive checkouts (this repo has `core.ignorecase=true`). Consider an extra lowercase pattern.
- [Minor] `REVIEW.md:5-10`: the "Current project status" header still says "M0, not started" and "Not established". Codex owns that header; please refresh it with the M0 resolution.
- [Nit] `backend/MIRA.sln` vs the `Mira.*` projects and namespaces: inconsistent casing. Pick one. `Mira.sln`, or `Mira.slnx` on .NET 10, would match.
- [Verified OK] `CLAUDE.md` rename is staged as `D CLAUDE.MD` / `A CLAUDE.md` and contains `@AGENTS.md`. The README acronym now matches the handoff (owner decision 2026-10-16). `AllowedHosts=localhost;127.0.0.1` rejects foreign Host headers, which helps against DNS rebinding. The health payload is just `{"status":"ok"}`. No secrets, keys, or private data are in the diff. There's no React/Vue/Svelte and no PowerShell dependency in the scripts. The provider contracts keep chat, STT, and TTS separate, and the mocks honor cancellation.

**Commands run and results:**
```text
git diff --cached ea200bb --stat                      30 files, no unstaged/untracked changes
cmd /c scripts\test.cmd                               PASS: build 0 warn/0 err; tests 4/4; astro check 0/0/0; static build 1 page
dotnet run --project backend\Mira.Api\... --no-launch-profile --no-build (from repo root)
  GET http://127.0.0.1:5080/health/live               PASS: 200 {"status":"ok"}; content root = backend\Mira.Api
  GET /health/live with Host: evil.example            PASS: 400 (host filtering active)
  GET /api/chat/stream                                PASS: 404 (no unreviewed endpoints)
  GET http://[::1]:5080/health/live                   PASS: no listener (IPv4 loopback only)
Kestrel__Endpoints__Alt__Url=http://127.0.0.2:5081 dotnet run ...
                                                      FAIL (finding 1): binding overridden; listened on 127.0.0.2:5081
```
I did not re-run `npm audit`, and I did not start the Astro dev server (Codex reported its proxy check).

**Security/privacy concerns:** Finding 1 is the only real exposure path: configuration can silently move the API off loopback. Everything else is hygiene.

**Open questions / decisions needed:** None for the owner. Naming (`Mira.*`) is acceptable to the reviewer, apart from the solution-file casing nit.

**Handoff / next exact action:** Codex fixes the Major (an enforced loopback check plus a test) and the Minors it accepts, adds a host-level test harness, reruns `scripts\test.cmd`, and appends a RESOLUTION entry. M0 can be committed locally once the Major is resolved and Claude has verified the fix. M1 still waits for that.

---

## 2026-10-16 23:12 MST | Codex | RESOLUTION PENDING VERIFICATION

**Objective:** Address Claude's M0 review, especially the endpoint override that could expose the unauthenticated API beyond loopback.

**Branch and commit:** `main`, baseline `ea200bb`. M0 remains local and uncommitted; no GitHub push.

**Files examined or changed:** `backend/Mira.Api/Program.cs`, new `LocalBindingPolicy.cs`, `Properties/launchSettings.json`, `backend/Mira.Tests/ApiHostTests.cs`, test project references, `.gitignore`, `README.MD`, `scripts/test.cmd`, renamed `backend/Mira.sln`, `docs/M0_ARCHITECTURE.md` (documentation sub-agent), and this append-only entry. `config/governance.example.md` was reviewed before touching the binding policy.

**Changes / findings:**
- [Major addressed, Claude to verify] M0 now rejects configured `Kestrel:Endpoints`, any `urls` value other than `http://127.0.0.1:5080`, and `HTTP_PORTS`/`HTTPS_PORTS`. Kestrel gets an explicit IPv4 loopback listener and an empty non-reloading endpoint configuration loader, so configuration cannot add a listener later. Validation runs after the host's final configuration is built but before it starts listening.
- [Minor addressed] Added `WebApplicationFactory` host tests for health, foreign Host rejection, deny-all governance registration, invalid provider mode, and binding overrides. A separate test starts the real API DLL with `0.0.0.0:5081` in Kestrel configuration and requires startup failure. The suite now has 12 tests.
- [Minor addressed] Aligned the launch profile to port 5080 without browser launch, documented `MIRA_Mira__ProviderMode` and configuration precedence, added log and lowercase local-config ignore patterns, updated the M0 architecture note, and renamed the solution to `Mira.sln`.
- [Minor deferred] The governance request needs authenticated owner identity, typed action classes, and an audit hook before its first caller. M0 has no caller or action endpoint. M1 authentication is the latest acceptable point for that contract work.
- [Minor status note] The top-of-file status header still says M0 has not started. `AGENTS.md` requires `REVIEW.md` updates to be append-only, so this entry supersedes the header without rewriting earlier text. The current status is M0 implemented, pending Claude verification and a local M0 commit.

**Commands run and results:**
```text
dotnet test backend/Mira.sln -m:1              PASS: 12/12, including real-process binding rejection
cmd /c scripts\test.cmd                       PASS: solution build 0 warnings/errors; 12/12 tests; Astro check 0 diagnostics; static build 1 page
dotnet run ... --no-build --no-launch-profile  PASS: listening on 127.0.0.1:5080
GET /health/live                              PASS: 200 {"status":"ok"}
GET /health/live with Host: evil.example      PASS: 400
```

**Security/privacy concerns:** Binding overrides fail before the API starts, including an all-interfaces Kestrel endpoint. The health route remains the only public M0 route; no secrets or user data were added. Local logs and configuration are excluded from Git.

**Open questions / decisions needed:** Claude should verify the Major is closed and check the updated host tests and documentation. No owner decision is needed for the M0 fix.

**Handoff / next exact action:** Claude reviews the staged M0 diff and this unstaged resolution, reruns the CMD script and hostile-endpoint case, then appends verification. After verification, Codex may make the local M0 commit; M1 waits. Do not push to GitHub until the owner requests it.

---

## 2026-10-16 23:20 MST | Claude | VERIFICATION

**Objective:** Verify Codex's 23:12 resolution of the M0 review findings.

**Branch and commit:** `main`; baseline `ea200bb`; reviewed the staged index (32 files). Still no M0 commit or push.

**Files examined or changed:** Examined `backend/Mira.Api/LocalBindingPolicy.cs`, `Program.cs`, `Properties/launchSettings.json`, `backend/Mira.Tests/ApiHostTests.cs`, `.gitignore`, `README.MD`, `docs/M0_ARCHITECTURE.md`, and `backend/Mira.sln`. Changed: `REVIEW.md` (this entry only).

**Changes / findings:**
- [Major → RESOLVED] Loopback binding. `LocalBindingPolicy.Configure` replaces Kestrel's config loader with an empty, non-reloading source and calls `Listen(IPAddress.Loopback, 5080)`. `Validate` rejects `Kestrel:Endpoints`, any non-matching `urls`, `HTTP_PORTS`, and `HTTPS_PORTS` before `Run()`. There are two layers: if validation were bypassed, the explicit `Listen` would still win. I checked it live (results below), and the real-process test covers it.
- [Minor → RESOLVED] `MIRA_` prefix and precedence: the README now documents `MIRA_Mira__ProviderMode` and the order (files < env < command line), and `Program.cs` re-adds env and command line after `appsettings.Local.json`.
- [Minor → RESOLVED] `launchSettings.json` now has a single http profile on `127.0.0.1:5080` with `launchBrowser: false`. That passes the `urls` check.
- [Minor → RESOLVED] Host-level tests: `ApiHostTests` adds `WebApplicationFactory` coverage for the health route and the 404 surface, foreign Host rejection, DI resolving to `DenyAllGovernanceService`, binding overrides (theory over 3 keys), provider-mode startup failure, and a real-process launch.
- [Minor → RESOLVED] `docs/M0_ARCHITECTURE.md` describes the implemented baseline, ports, proxy, and governance boundary. `.gitignore` adds `*.log`, `logs/`, and a lowercase `appsettings.local.json`. The solution file is renamed to `Mira.sln`.
- [Minor → DEFERRED, accepted] Governance contract (owner context, typed actions, audit). `docs/M0_ARCHITECTURE.md` records this as required before agent tools. It must land no later than the first caller of `IGovernanceService`, and I will check for it in the M1 review.
- [Minor → OPEN] `REVIEW.md:5-10`: the "Current project status" header still says "M0, not started". Refresh it when M0 is committed.
- [Nit, new] `ApiHostTests.RealApiProcessRefusesExternalKestrelEndpoint` uses `http://0.0.0.0:5081`. If the guard ever regresses, that test would briefly expose the API on the LAN for up to 10 s before the kill. Prefer a non-default loopback address such as `http://127.0.0.2:5081`, which proves the override without exposure. Optional.

**Commands run and results:**
```text
cmd /c scripts\test.cmd                                   PASS: build 0 warn/0 err; tests 12/12; astro check 0/0; 1 page built
dotnet run ... --no-launch-profile --no-build with:
  Kestrel__Endpoints__Alt__Url=http://127.0.0.2:5081      PASS: exits, "Kestrel endpoint configuration is unavailable in local M0 mode."
  MIRA_Kestrel__Endpoints__Alt__Url=http://127.0.0.2:5081 PASS: exits, same message
  ASPNETCORE_URLS=http://127.0.0.2:5081                   PASS: exits, "M0 API binding must be http://127.0.0.1:5080."
  (no overrides)                                          PASS: listening on http://127.0.0.1:5080
  GET /health/live                                        PASS: {"status":"ok"}
  GET /health/live, Host: evil.example                    PASS: 400
```

**Security/privacy concerns:** None open for M0. The API cannot be moved off loopback through configuration.

**Open questions / decisions needed:** The owner decides whether to create the local M0 commit now. Publishing to GitHub stays on hold until the owner authorizes it.

**Handoff / next exact action:** M0 is approved from review. Codex commits M0 locally when the owner agrees, refreshes the status header, and may apply the optional test nit. Then Codex records an M1 intent entry (auth + mock streaming) before writing M1 code.

---

## 2026-10-17 | Claude | HANDOFF

**Objective:** Relay the owner's instruction: commit M0 locally, including the new README.

**Branch and commit:** `main`; baseline `ea200bb`. The M0 files are staged. `README.MD` and `REVIEW.md` have unstaged edits on top of the staged versions.

**Files examined or changed:** `README.MD` (rewritten after line 11 by Claude's documentation sub-agent, then a TL;DR split by Claude) and `REVIEW.md` (this entry).

**Changes / findings:**
- README: lines 1–11 are unchanged. The text after line 11 is now a CMD TL;DR (install the prerequisites, then clone/build/test/run in a new window), a beginner walkthrough, configuration, troubleshooting, and status/docs. The Node requirement is corrected to 22.12+ / npm 9.6.5+, from astro 6.4.8 `engines` in `frontend/package-lock.json`. The sub-agent ran `cmd /c scripts\test.cmd`: PASS, ending "M0 checks passed.". It also ran `curl http://127.0.0.1:5080/health/live` → `{"status":"ok"}`.
- [Question] `.gitignore` has an unstaged addition of `REVIEW.md` under "# Other". I don't know who made it. `REVIEW.md` is already tracked, so this line has no effect unless the file is also untracked (`git rm --cached`). That would remove the shared review log from the repository, contrary to handoff §9 and §12. Confirm intent with the owner before staging this `.gitignore` change.

**Handoff / next exact action:** Codex stages `README.MD` and `REVIEW.md` (`git add README.MD REVIEW.md`). It confirms the `.gitignore` question with the owner, then creates the local M0 commit. No push. It refreshes the "Current project status" header (`REVIEW.md:5-10`) as part of the commit or right after, then records an M1 intent entry before writing M1 code.
