# M I R A

**Memory · Intelligence · Reasoning · Autonomy**

_Remember. Converse. Discover. Assist._

The Identity of MIRA:
MIRA is a personal autonomous intelligence designed to remember, reason, converse, research, and assist. She maintains continuity across interactions, adapts to individual preferences, and operates within transparent, user-governed boundaries.


---

## TL;DR — build and run

Run these in **Windows CMD** (Command Prompt).

**Step 1: install prerequisites (once).** Skip any you already have.

```cmd
winget install --id Microsoft.DotNet.SDK.10 -e
winget install --id OpenJS.NodeJS.LTS -e
winget install --id Git.Git -e
```

**Step 2: open a NEW CMD window** (so the updated `PATH` is picked up), then build and run:

```cmd
git clone https://github.com/sorrynofocus/mira-assistant.git
cd mira-assistant
npm --prefix frontend ci
scripts\test.cmd
scripts\dev.cmd
```

Then open <http://127.0.0.1:4321/> in your browser. The page should say **API online**.

## Getting started (full walkthrough)

### What the pieces are

MIRA is currently at milestone **M0**: a small local shell that proves the frontend, backend, and tests work together. There is no chat, login, database, or voice yet.

- **Frontend: [Astro](https://astro.build/).** Builds a static web page. The page logic is plain (vanilla) TypeScript, with no React/Vue/Svelte. It runs on Node.js.
- **Backend: ASP.NET Core Minimal API.** A small C# web server on .NET 10. In M0 it exposes one endpoint, `GET /health/live`, which returns `{"status":"ok"}`.
- **Mock model providers.** Chat, transcription, and speech are behind replaceable interfaces. M0 uses only deterministic mocks, so you do not need Azure keys or any paid service.
- **Tests: xUnit.** .NET unit and integration tests for configuration, governance, mocks, and the API host.

| Folder | What it contains |
| --- | --- |
| `frontend/` | Astro static web UI (vanilla TypeScript) |
| `backend/Mira.Api/` | ASP.NET Core Minimal API host (`/health/live`, loopback binding policy, configuration) |
| `backend/Mira.Core/` | Contracts and options: provider interfaces, governance interface, `Mira` settings |
| `backend/Mira.Infrastructure/` | Implementations: mock providers and the deny-all governance service |
| `backend/Mira.Tests/` | xUnit tests |
| `scripts/` | Windows CMD scripts: `dev.cmd` (run) and `test.cmd` (build and test) |
| `config/` | Example administrator governance file (`governance.example.md`) |
| `docs/` | Architecture and milestone documents |

### 1. Install prerequisites

You need:

- **.NET 10 SDK** (`winget install --id Microsoft.DotNet.SDK.10 -e`, or download from <https://dotnet.microsoft.com/download/dotnet/10.0>)
- **Node.js 22.12 or newer** with npm 9.6.5 or newer (the minimums required by Astro). The current LTS works: `winget install --id OpenJS.NodeJS.LTS -e`, or download from <https://nodejs.org/>.
- **Git** (`winget install --id Git.Git -e`, or download from <https://git-scm.com/download/win>)

Open a **new** CMD window after installing, then verify:

```cmd
dotnet --version
node --version
npm --version
git --version
```

`dotnet --version` should print `10.x.x`. `node --version` should print `v22.12.0` or later.

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
4. `npm run check` (`astro check`) type-checks the frontend.
5. `npm run build` (`astro build`) builds the static site into `frontend\dist`.

Success ends with this line:

```text
M0 checks passed.
```

### 5. Run

```cmd
scripts\dev.cmd
```

This opens two new CMD windows and prints the two addresses:

- **MIRA API**: the backend at `http://127.0.0.1:5080` (health check at `http://127.0.0.1:5080/health/live`).
- **MIRA Frontend**: the Astro dev server at `http://127.0.0.1:4321/`. It forwards (proxies) `/health` requests to the API, so the page and the API appear to the browser as one site.

Open <http://127.0.0.1:4321/>. The page shows **API online** when the backend responds, or **API unavailable** if it does not. Use the retry button after the API finishes starting.

To check the API directly (`curl` ships with Windows 10 and later):

```cmd
curl http://127.0.0.1:5080/health/live
```

Expected output: `{"status":"ok"}`

To stop, press `Ctrl+C` in each of the two windows, or close them.

### 6. Run the pieces manually (optional)

These are the same commands `scripts\dev.cmd` runs. Use two CMD windows, both starting at the repository root.

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

M0 limits:

- `Mira:ProviderMode` accepts only `Mock`; any other value stops the API at startup.
- The API always binds to `http://127.0.0.1:5080`. By design, endpoint overrides are rejected at startup: `Kestrel:Endpoints`, any other `urls` / `ASPNETCORE_URLS` value, and `HTTP_PORTS` or `HTTPS_PORTS`.
- Private configuration (`appsettings.Local.json`) is ignored by Git. Never commit keys or private data.

### Troubleshooting

| Symptom | Fix |
| --- | --- |
| `'dotnet'`, `'node'`, `'npm'`, or `'git'` is not recognized | The install did not update this window's `PATH`. Open a new CMD window (or sign out and back in) and re-run the version checks. |
| `dotnet --version` shows an older version, or the build reports it cannot find .NET 10 | Install the .NET 10 SDK (see prerequisites), open a new window, and confirm `dotnet --version` prints `10.x.x`. |
| `scripts\dev.cmd` prints `Install frontend dependencies with: npm --prefix frontend ci` | Run `npm --prefix frontend ci` from the repository root, then run `scripts\dev.cmd` again. |
| Page says **API unavailable** | Check that the **MIRA API** window is open and has finished starting (no error shown), then press retry. Confirm with `curl http://127.0.0.1:5080/health/live`. |
| API window shows an "address already in use" error, or the frontend picks a different port | Another program is using port 5080 or 4321. Find it with `netstat -ano \| findstr :5080` (or `:4321`); the last column is the process ID. Close that program, or end it with `taskkill /PID <pid>`. |
| Startup exception `M0 API binding must be http://127.0.0.1:5080.` | An `urls` / `ASPNETCORE_URLS` override is set. Remove it from your environment, command line, or `appsettings.Local.json`. |
| Startup exception `Kestrel endpoint configuration is unavailable in local M0 mode.` | Remove the `Kestrel:Endpoints` setting from `appsettings.Local.json` or the environment. |
| Startup exception `HTTP_PORTS and HTTPS_PORTS are unavailable in local M0 mode.` | Clear those variables, for example `set HTTP_PORTS=` and `set ASPNETCORE_HTTP_PORTS=`, in the window that starts the API. |

## Project status and docs

The current M0 baseline uses an Astro static frontend, a loopback-bound ASP.NET Core API, and deterministic mock providers. Chat, authentication, storage, voice, and web search are scheduled in [the STT 3.0 handoff](docs/STT3_HANDOFF.md).

The backend is local-only by design (loopback `127.0.0.1` only) and must not be exposed on a network yet.

Architecture and collaboration rules are in [the M0 architecture note](docs/M0_ARCHITECTURE.md) and [AGENTS.md](AGENTS.md). Implementation and review evidence is appended to [REVIEW.md](REVIEW.md).
