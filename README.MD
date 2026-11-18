# M I R A

**Memory · Intelligence · Reasoning · Autonomy**

_Remember. Converse. Discover. Assist._

The Identity of MIRA:
MIRA is a personal autonomous intelligence designed to remember, reason, converse, research, and assist. She maintains continuity across interactions, adapts to individual preferences, and operates within transparent, user-governed boundaries.

## Why MIRA exists

MIRA is STT 3.0. It grew out of the owner's earlier speech-to-text assistant and is being rebuilt as a local-first personal assistant. This section says where it came from and what it is meant to become, so the reasoning is easy to pick up again after time away.

### Where it came from

- **STT, the original app** ([sorrynofocus/STT](https://github.com/sorrynofocus/STT)). My first working cloud AI deployment: a .NET 9 voice assistant built on Azure. You spoke into a microphone. Azure Speech transcribed it, and Azure OpenAI reasoned over it, with optional document retrieval from Azure AI Search. The app then either spoke an answer back or ran a gated local command. It was cloud-first "with room to hybridize later", kept its keys in a local settings file "simple by design", and its persona was a single line of configuration.
- **STT 3.0 becomes MIRA.** The new project keeps the voice idea but turns it into a personal assistant that runs locally. It has a browser chat with streamed replies, memory you control, and editable personas. Speech-to-text, chat, and text-to-speech are three separate model roles, and each one can be swapped. The document-search (RAG) layer is dropped for the first iteration, and web search becomes an optional add-on.
- **The name.** MIRA (Memory, Intelligence, Reasoning & Autonomy) is inspired by the AI that runs a facility in the owner's novel *Invisible War*. That character could create her own operating rules to protect herself and her human companions. This is creative inspiration only. The real MIRA does **not** get to change her own rules or security boundaries. That authority stays with the owner and is enforced in code (see [AGENTS.md](AGENTS.md)).

### What it is meant to be

- **Local-first and private.** Conversations, memory, personas, and settings live in a local SQLite database and private files on the owner's machine. Provider keys stay on the backend. Requests to an AI provider still send that provider the content it needs.
- **Memory you control.** MIRA can suggest things to remember. You can inspect, edit, export, and delete them, and sensitive items wait for your approval.
- **Voice in and out, with replaceable providers.** Transcription, chat, and speech are configured independently, so any one of them can be replaced.
- **Personas.** A `whoami.md` file sets MIRA's voice and character. You can create or change personas, but the model cannot silently rewrite them.
- **Optional web search** (Tavily), used only when it is enabled.
- **Owner-governed boundaries.** The model may propose plans, memories, or rule changes. Only the owner can approve them, through policy the application enforces.

### Design principles

- **Private and local by default:** the API binds to `127.0.0.1` only, and keys, databases, and private data are never committed.
- **Default-deny governance, enforced in code:** `governance.md` describes the policy, but code makes each decision. No tool runs without a validated grant, the model cannot grant itself permissions, and every decision is audited.
- **Replaceable parts:** model providers, storage, and sign-in each sit behind their own interface.
- **Small, proven steps:** each milestone is proven with tests against mock providers before paid services or a microphone are connected.

### Status

M0 (the local baseline) and M1 (local sign-in plus streamed chat against a mock model, with no paid AI connected) are done. M2 (conversation persistence and a redacted pipeline inspector, on plain SQLite per [ADR 0002](docs/decisions/0002-plain-sqlite-for-m2.md)) is done; details are in [the M2 persistence and inspector note](docs/M2_PERSISTENCE_AND_INSPECTOR.md). M3 (an account area and per-user settings: themes, font size, inspector default) is implemented in this tree and in review; see [the M3 account and settings note](docs/M3_ACCOUNT_SETTINGS.md). Personas, memory, voice, and web search come in later milestones. The full roadmap is in [the STT 3.0 handoff](docs/STT3_HANDOFF.md).


---

## TL;DR — build and run

Run these in **Windows CMD** (Command Prompt).

**Step 1: install prerequisites (once).** Skip any you already have.

```cmd
winget install --id Microsoft.DotNet.SDK.10 -e
winget install --id OpenJS.NodeJS.LTS -e
winget install --id Git.Git -e
```

**Step 2: open a NEW CMD window** (so the updated `PATH` is picked up), then build, create your account, and run:

```cmd
git clone https://github.com/sorrynofocus/mira-assistant.git
cd mira-assistant
npm --prefix frontend ci
scripts\test.cmd
scripts\create-user.cmd <your-name>
scripts\dev.cmd
```

What each line does:

| Command | What it does |
| --- | --- |
| `git clone …` | Downloads the repository into a new `mira-assistant` folder. |
| `cd mira-assistant` | Moves into that folder. Run every later command from here. |
| `npm --prefix frontend ci` | Installs the frontend's packages (Astro, Vitest, TypeScript) into `frontend\node_modules`. `--prefix frontend` runs npm in the `frontend` folder; `ci` is a clean install of the exact versions in `frontend\package-lock.json`. It ends with an install summary. A "vulnerabilities" or "funding" notice there is information, not an error. |
| `scripts\test.cmd` | Builds the C# backend, runs the backend tests (xUnit) and frontend tests (Vitest), type-checks the frontend and builds the site. It stops at the first failure; success ends with `All checks passed.` |
| `scripts\create-user.cmd <your-name>` | Creates your local sign-in account in the SQLite database (`%LOCALAPPDATA%\MIRA\mira.db`). It asks for your password twice; nothing is echoed while you type. There are no default accounts. |
| `scripts\dev.cmd` | Starts MIRA in one window: the API (`http://127.0.0.1:5080`) in the background and the web page (`http://127.0.0.1:4321`) in the foreground. Press Ctrl+C to stop. If the API is still running, the script prints how to end it, or close the window. |

You only need `git clone`, `npm --prefix frontend ci` and `create-user` once. After that, `scripts\dev.cmd` is all it takes to run MIRA. Rerun `npm --prefix frontend ci` when `frontend\package-lock.json` changes.

Then open <http://127.0.0.1:4321/> in your browser and sign in with that account. The header should say **API online**.

## Getting started (full walkthrough)

### What the pieces are

MIRA is currently at milestone **M3** (implemented, in review): local sign-in, a streamed text chat against a mock model, saved conversations (several per user), a redacted pipeline inspector, and per-user settings (theme, font size, inspector default). Persona editing, memory, voice, and web search come in later milestones.

- **Frontend: [Astro](https://astro.build/).** Builds a static web page. The page logic is plain (vanilla) TypeScript, with no React/Vue/Svelte. It runs on Node.js.
- **Backend: ASP.NET Core Minimal API.** A small C# web server on .NET 10. It exposes `GET /health/live` (returns `{"status":"ok"}`), local username/password sign-in, a streaming chat endpoint, conversation endpoints, and read-only diagnostics for the inspector. Everything under `/api` requires a signed-in user except the sign-in and CSRF-token endpoints.
- **Local database: SQLite.** Accounts, sessions, conversations, messages and inspector timing events are stored in a SQLite file outside the repository (`%LOCALAPPDATA%\MIRA\mira.db` by default). Inspector events never contain message text and are deleted after 30 days.
- **Mock model providers.** Chat, transcription, and speech are behind replaceable interfaces. M1 uses only deterministic mocks, so you do not need Azure keys or any paid service.
- **Tests: xUnit and Vitest.** .NET unit and integration tests for configuration, authentication, sessions, CSRF, streaming, storage, governance, and the API host; Vitest tests for the frontend stream parser, sign-out, conversation list, inspector, themes and settings.

| Folder | What it contains |
| --- | --- |
| `frontend/` | Astro static web UI (vanilla TypeScript): sign-in form, chat view, conversation list, inspector panel, account area and Settings dialog, themes (`public/theme-init.js` applies the saved theme before first paint), stream parser |
| `backend/Mira.Api/` | ASP.NET Core Minimal API host: health, auth, chat streaming, conversations, diagnostics, user preferences, inspector-event retention, CSRF/origin checks, loopback binding policy, `create-user` command |
| `backend/Mira.Core/` | Contracts and options: provider, account, session, conversation, pipeline-event, and governance interfaces; `Mira` settings |
| `backend/Mira.Infrastructure/` | Implementations: SQLite accounts/sessions/audit log, conversations and pipeline events; pipeline redactor and recorder; mock providers; deny-all governance |
| `backend/Mira.Tests/` | xUnit tests |
| `scripts/` | Windows CMD scripts: `dev.cmd` (run), `test.cmd` (build and test), `create-user.cmd` (create an account) |
| `config/` | Example administrator governance file (`governance.example.md`) |
| `docs/` | Architecture and milestone documents |

### 1. Install prerequisites

You need:

- **.NET 10 SDK** (`winget install --id Microsoft.DotNet.SDK.10 -e`, or download from <https://dotnet.microsoft.com/download/dotnet/10.0>)
- **Node.js 22.23.4 or newer** with npm 9.6.5 or newer (the minimums required by Astro 7.4.12, the patched release). The current LTS works: `winget install --id OpenJS.NodeJS.LTS -e`, or download from <https://nodejs.org/>.
- **Git** (`winget install --id Git.Git -e`, or download from <https://git-scm.com/download/win>)

Open a **new** CMD window after installing, then verify:

```cmd
dotnet --version
node --version
npm --version
git --version
```

`dotnet --version` should print `10.x.x`. `node --version` should print `v22.23.4` or later.

### 2. Get the source

```cmd
git clone https://github.com/sorrynofocus/mira-assistant.git
cd mira-assistant
```

All later commands run from this repository root folder.

The owner keeps source in BOTH of these local working copies:

```text
%USERPROFILE%\source\github-repo\sorrynofocus\mira-assistant
%USERPROFILE%\source\gitlab-repo\sorrynofocus\mira-assistant
```

### 3. Install frontend dependencies

```cmd
npm --prefix frontend ci
```

This is the same as `cd frontend`, `npm ci`, `cd ..`. `npm ci` installs the exact package versions recorded in `frontend\package-lock.json` into `frontend\node_modules`. That folder is ignored by Git, so it is never committed. Re-run this after pulling changes to `package-lock.json`.

### 4. Build and test

```cmd
scripts\test.cmd
```

The script runs, in order, and stops at the first failure:

1. `dotnet build backend\Mira.sln -m:1` builds all .NET projects.
2. `dotnet test backend\Mira.sln --no-build -m:1` runs the xUnit tests.
3. Runs `npm ci` in `frontend` only if `node_modules` is missing.
4. `npm test` (`vitest run`) runs the frontend stream-parser and sign-out tests.
5. `npm run check` (`astro check`) type-checks the frontend.
6. `npm run build` (`astro build`) builds the static site into `frontend\dist`.

Success ends with this line:

```text
All checks passed.
```

### 5. Create your account

There are no default credentials. Create the first account from the repository root:

```cmd
scripts\create-user.cmd <your-name>
```

It prompts for `Password:` and `Confirm password:`; nothing is echoed. If the two entries differ, no account is created. On success it prints `Created user '<your-name>' in <database path>.`

- **Username:** 1–64 characters; letters, digits, `.`, `_`, `-`, and `@`. Leading and trailing spaces are removed. Usernames are case-insensitive (`Owner` and `owner` are the same account) and must be unique.
- **Password:** 8–1024 characters by default (`Mira:Auth:MinimumPasswordLength` / `Mira:Auth:MaximumPasswordLength`). Any characters are allowed, including spaces, symbols, and Unicode. Passwords are never trimmed or truncated. There is no strength meter.
- **Where it is stored:** `%LOCALAPPDATA%\MIRA\mira.db` by default, outside the repository. Set `Mira:DatabasePath` to use another file (see [Configuration](#configuration)). The API and `create-user` must use the same path.
- **Scripted use:** when standard input is redirected, the command reads one line from it as UTF-8 instead of prompting (`--password-stdin` forces this when calling the API project directly). CMD's `echo` does not send non-ASCII characters as UTF-8 and leaves the password in your command history, so prefer the interactive prompt.

`create-user.cmd` runs `dotnet run` on the API project, which builds it first. Run it from the repository root; if the build reports that a file is in use, stop the running API (Ctrl+C in the `dev.cmd` window, or end the process listening on port 5080) and try again.

### 6. Run

```cmd
scripts\dev.cmd
```

This starts both pieces in the current CMD window and prints the two addresses:

- **MIRA API**: the backend at `http://127.0.0.1:5080` (health check at `http://127.0.0.1:5080/health/live`), started in the background in this window.
- **MIRA Frontend**: the Astro dev server at `http://127.0.0.1:4321/`, running in the foreground. It forwards (proxies) `/api` and `/health` requests to the API, so the page and the API appear to the browser as one site. This is what keeps the strict session cookie and CSRF checks working. The script sets `NPM_CONFIG_UPDATE_NOTIFIER=false` so npm's update notice does not pause the start.

Open <http://127.0.0.1:4321/>. The header shows **API online** when the backend responds, or **API unavailable** if it does not; use **Check again** after the API finishes starting.

The page shows a **Sign in** form. After you sign in it shows the chat:

- **Enter** sends, **Shift+Enter** adds a new line.
- While MIRA is responding, the **Stop** button or **Esc** cancels the response. The partial reply is kept and marked **Stopped**.
- Responses come from a mock model that echoes your message. No paid API is called.
- **Your account** is in the lower-left corner: your initial, your username and a **gear** button that opens **Settings** (a dialog you can use with the keyboard; **Esc** closes it).
- **Settings** (saved per user on the server):
  - **Theme:** **MIRA Dark** (the default), **Sovereign** (black and white, high contrast; the API status dot is green when online) or **Match system** (follows your OS light/dark setting: dark gives MIRA Dark, light gives Sovereign, and it switches live).
  - **Font size:** small, default or large.
  - **Open the inspector by default:** whether the inspector panel opens when you sign in. The **Show inspector** button still toggles it for the current session without changing this setting.
  - The page remembers your theme and font size in the browser, so it opens in the right look without a flash. Nothing else is cached.
- **Sign out** is in Settings. It ends the session on the server. If MIRA can't confirm that (for example, the API has stopped), you stay signed in and see an error. Try again once the API is running.

To check the API directly (`curl` ships with Windows 10 and later):

```cmd
curl http://127.0.0.1:5080/health/live
```

Expected output: `{"status":"ok"}`

To stop, press `Ctrl+C` in this window. That stops the frontend, and may also stop the API because both share the console. If the API is still listening on port 5080, the script tries to stop it when the frontend exits; if you answered Y to "Terminate batch job?" and the API is still running, end it with the `taskkill` command the script printed, or close the window.

### 7. Run the pieces manually (optional)

You can also start the two pieces yourself in two CMD windows, both starting at the repository root. `scripts\dev.cmd` runs the same commands in one window.

API:

```cmd
dotnet run --project backend\Mira.Api\Mira.Api.csproj --no-launch-profile
```

Frontend:

```cmd
cd frontend && npm run dev
```

### Configuration

Settings load in this order, with later sources overriding earlier ones:

1. `backend\Mira.Api\appsettings.json` (committed defaults)
2. `backend\Mira.Api\appsettings.Local.json` (optional, private)
3. Environment variables, including `MIRA_`-prefixed ones
4. Command-line arguments (final precedence)

To create a private local override, copy the example:

```cmd
copy backend\Mira.Api\appsettings.Local.example.json backend\Mira.Api\appsettings.Local.json
```

For a prefixed environment variable, use the form `MIRA_Mira__ProviderMode=Mock`. The leading `MIRA_` prefix is removed and `__` stands for `:`, so this sets `Mira:ProviderMode`. Example in CMD:

```cmd
set MIRA_Mira__ProviderMode=Mock
```

Available settings (out-of-range values stop the API at startup):

| Setting | Default | Notes |
| --- | --- | --- |
| `Mira:ProviderMode` | `Mock` | Only `Mock` is accepted in M1. |
| `Mira:DatabasePath` | `%LOCALAPPDATA%\MIRA\mira.db` | SQLite file for accounts, sessions, and audit records. A relative path is resolved against the current folder. |
| `Mira:AllowedOrigins` | `http://127.0.0.1:4321`, `http://localhost:4321`, `http://127.0.0.1:5080`, `http://localhost:5080` | Origins allowed to send state-changing requests (array). |
| `Mira:Auth:SessionIdleMinutes` | `30` | Session ends after this much inactivity (1–1440). |
| `Mira:Auth:SessionAbsoluteHours` | `12` | Session ends this long after sign-in regardless of activity (1–168). |
| `Mira:Auth:MinimumPasswordLength` | `8` | At least 1; applies when creating accounts. |
| `Mira:Auth:MaximumPasswordLength` | `1024` | 128–4096. |
| `Mira:Auth:MaxFailedLoginsPerUsername` | `5` | Failed sign-ins before that username is locked out (1–100). |
| `Mira:Auth:FailedLoginWindowMinutes` | `15` | Lockout window (1–1440). |
| `Mira:Auth:LoginRequestsPerMinute` | `20` | Sign-in requests per minute per client IP address (1–1000). |
| `Mira:Chat:MaxMessageLength` | `8000` | Maximum characters per message (1–100000). |
| `Mira:Chat:MaxResponseSeconds` | `120` | A response running longer is stopped (1–3600). |
| `Mira:Chat:MockDelayMilliseconds` | `40` | Delay between mock words, so streaming is visible (0–10000). |

Example: `set MIRA_Mira__Auth__SessionIdleMinutes=60`.

Limits:

- The API always binds to `http://127.0.0.1:5080`. By design, endpoint overrides are rejected at startup: `Kestrel:Endpoints`, any other `urls` / `ASPNETCORE_URLS` value, and `HTTP_PORTS` or `HTTPS_PORTS`.
- Private configuration (`appsettings.Local.json`) is ignored by Git, as are SQLite files. Never commit keys, databases, or private data.

### Troubleshooting

| Symptom | Fix |
| --- | --- |
| `'dotnet'`, `'node'`, `'npm'`, or `'git'` is not recognized | The install did not update this window's `PATH`. Open a new CMD window (or sign out and back in) and re-run the version checks. |
| `dotnet --version` shows an older version, or the build reports it cannot find .NET 10 | Install the .NET 10 SDK (see prerequisites), open a new window, and confirm `dotnet --version` prints `10.x.x`. |
| `npm ci` or `astro` reports an unsupported Node.js version (`EBADENGINE`, or `Node.js vX is not supported by Astro!`) | Install Node.js 22.23.4 or newer (for example `winget upgrade --id OpenJS.NodeJS.22 -e`), open a new window, and confirm with `node --version`. |
| `npm ci` ends with `N moderate severity vulnerabilities` | Not an error; the install succeeded. Run `npm --prefix frontend audit` to see the details. Updating dependencies changes `package-lock.json`, so it goes through the normal implement-and-review cycle. |
| `scripts\dev.cmd` prints `Install frontend dependencies with: npm --prefix frontend ci` | Run `npm --prefix frontend ci` from the repository root, then run `scripts\dev.cmd` again. |
| Page says **API unavailable**, or the sign-in form says `MIRA is unreachable. Start the API, then reload this page.` | Check that the API has finished starting in the `dev.cmd` window (no error shown), then press **Check again** or reload. Confirm with `curl http://127.0.0.1:5080/health/live`. |
| Sign-in says `Invalid username or password.` | The username or password is wrong, or the account is disabled. The message is the same in every case by design, so it does not reveal which accounts exist. Check that you created the account (`scripts\create-user.cmd`) against the same database the API uses. |
| Sign-in says `Too many failed attempts. Try again later.` (HTTP 429) | That username had 5 failed sign-ins (default) within 15 minutes and is locked out, even with the right password, until the window passes. The counter is kept in memory, so restarting the API also clears it. `Too many sign-in attempts. Wait a minute and try again.` means the per-IP limit of 20 sign-in requests per minute was hit; wait a minute. |
| `Your session ended. Sign in again.` | Sessions end after 30 minutes without activity or 12 hours after sign-in (defaults), on sign-out, or if the account is disabled. Sign in again. |
| Earlier messages are gone after restarting the API | Since M2, conversations are saved in the SQLite database and survive restarts. If they are gone, check that `Mira:DatabasePath` (or `%LOCALAPPDATA%\MIRA\mira.db`) still points at the same file, and that the conversation wasn't deleted (deleting is permanent). A reply that was streaming when the API stopped shows as interrupted. |
| `A response is already streaming. Wait for it to finish or stop it.` | Only one response per user can stream at a time. Wait for it to finish or press **Stop**. |
| `create-user` prints `That username is already taken.` | Usernames are case-insensitive. Pick another name. |
| API window shows an "address already in use" error, or the frontend picks a different port | Another program is using port 5080 or 4321. Find it with `netstat -ano \| findstr :5080` (or `:4321`); the last column is the process ID. Close that program, or end it with `taskkill /PID <pid>`. |
| Startup exception `M1 supports only the Mock provider mode.` | Set `Mira:ProviderMode` back to `Mock`. Other `Mira:...` startup errors name the setting and its allowed range. |
| Startup exception `The API binding must be http://127.0.0.1:5080.` | An `urls` / `ASPNETCORE_URLS` override is set. Remove it from your environment, command line, or `appsettings.Local.json`. |
| Startup exception `Kestrel endpoint configuration is unavailable in local-only mode.` | Remove the `Kestrel:Endpoints` setting from `appsettings.Local.json` or the environment. |
| Startup exception `HTTP_PORTS and HTTPS_PORTS are unavailable in local-only mode.` | Clear those variables, for example `set HTTP_PORTS=` and `set ASPNETCORE_HTTP_PORTS=`, in the window that starts the API. |

## Project status and docs

**M3 is implemented** in this tree and in review: a lower-left account area, a Settings dialog, three theme choices (MIRA Dark, Sovereign, Match system), font size and an inspector default, saved per user (`GET`/`PUT /api/me/preferences`). Details are in [the M3 account and settings note](docs/M3_ACCOUNT_SETTINGS.md).

**M2 is done** (accepted 2026-11-17): multiple conversations per user are stored in SQLite, a page reload keeps history, and a hidden-by-default inspector shows redacted pipeline events and timings. Details are in [the M2 persistence and inspector note](docs/M2_PERSISTENCE_AND_INSPECTOR.md). M2 uses plain SQLite ([ADR 0002](docs/decisions/0002-plain-sqlite-for-m2.md)), not EF Core.

**M1** (local authentication and mock streamed chat) and **M0** (the local baseline) remain the foundation; see [the M1 authentication and streaming note](docs/M1_AUTH_AND_STREAMING.md) and [the M0 architecture note](docs/M0_ARCHITECTURE.md). Persona editing, memory, voice, and web search are still scheduled in [the STT 3.0 handoff](docs/STT3_HANDOFF.md).

The backend is local-only by design (loopback `127.0.0.1` only) and must not be exposed on a network yet.

Architecture and collaboration rules are in [the M0 architecture note](docs/M0_ARCHITECTURE.md), [the M1 note](docs/M1_AUTH_AND_STREAMING.md), [the M2 note](docs/M2_PERSISTENCE_AND_INSPECTOR.md), [the M3 note](docs/M3_ACCOUNT_SETTINGS.md), and [AGENTS.md](AGENTS.md). Implementation and review evidence is appended to [REVIEW.md](REVIEW.md); open follow-ups are tracked in [docs/TODO.md](docs/TODO.md).

## How the AI agents stay in sync

MIRA is built by several AI coding agents working with one human owner: Codex, Claude and Grok, in roles the owner assigns. Agents from different tools can't message each other (a team of Grok bots talks among itself, see below), so they coordinate through files in this repository using the **[Agent Sync Protocol](docs/agent-sync/AGENT_SYNC.md)**:

- **One shared log.** [`REVIEW.md`](REVIEW.md) is append-only. Every agent records what it did, the evidence (commands and test results) and its findings there.
- **A turn baton.** Each entry ends with **Next turn:**, naming which agent acts next and what it should do, and the status header shows the current **Turn**. The owner only has to tell the next agent one line, such as *"Codex reviewed — your turn"*. The agent reads the log and picks up from there. Nobody copies responses between tools. The baton only schedules work: approvals for commits, pushes and other sensitive steps come from the owner directly, never from the log.
- **Different agents implement and review.** An agent never reviews its own change. The M0 loopback-binding bug and the M1 sign-out bug were both caught this way.
- **Agent teams run themselves.** Grok can work as a team of Grok bots that pass work between themselves automatically, so they skip the turn baton among themselves. The team holds the Turn while it works, writes one summary entry when it's done, and its work still goes to an outside reviewer.
- **A clean slate per milestone.** When a milestone is committed, its log entries move to `docs/review-archive/`, a short summary carries open items forward, and each agent starts a fresh session. The repository, not any agent's chat memory, holds the state.

The protocol is project-independent. Copy [`docs/agent-sync/`](docs/agent-sync/) into another repository and use its `REVIEW.template.md` and `AGENTS.template.md` to start.
