# MW-GC.EventManager

EventManager helps organize gaming events: maintain games, activities, themes and
holidays, generate filtered activity selections, reroll slots, save events and
select winners.

## Architecture

- **Web** — .NET 10 Blazor WebAssembly UI with Fluent UI; calls `/api/*`.
- **API** — .NET 10 Azure Functions v4 isolated worker; handles CRUD for the
  library and Events, backed by Azure Table Storage. The Web builds each
  Event's selections itself and saves them through the API.
- **Shared** — models and storage entities used by both.
- **Tests** — two MSTest projects: `MW-GC.EventManager.Api.Tests` for the API
  logic and `MW-GC.EventManager.Web.Tests` for the Web UI, which renders pages
  with bUnit.

Production deployment uses Azure Static Web Apps and Azure Functions. The
[Web routing configuration](MW-GC.EventManager.Web/staticwebapp.config.json)
restricts application and API access to the `admin` role.

The Function App is a bring-your-own backend linked to the Static Web App. The
`admin` role is enforced by the Static Web App route table, and the linked
backend accepts traffic only through the Static Web App.

## Prerequisites and setup

Install the .NET 10 SDK. For the full local app, also install Azure Functions
Core Tools v4, Azurite with Table Storage support, and the Azure Static Web Apps
CLI (`swa`). The npm-based tools require Node.js and npm.

From the repository root:

```sh
dotnet restore MW-GC.EventManager.slnx
```

Create `MW-GC.EventManager.API/local.settings.json` (already git-ignored):

```json
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "UseDevelopmentStorage=true",
    "FUNCTIONS_WORKER_RUNTIME": "dotnet-isolated"
  }
}
```

Keep credentials and local settings out of commits. Leave
`AzureWebJobsStorage__accountName` unset locally: the API prefers that
identity-based Azure configuration over the connection string when present.

## Run locally

Run each block in a separate terminal, starting from the repository root:

```sh
# 1. Storage emulator; keep its data outside the repository
azurite --location ../eventmanager-azurite
```

```sh
# 2. API (default port 7071)
cd MW-GC.EventManager.API
func start
```

```sh
# 3. Web development server (port 5221)
dotnet run --project MW-GC.EventManager.Web/MW-GC.EventManager.Web.csproj --launch-profile http
```

```sh
# 4. Same-origin Web/API proxy (port 4280)
swa start http://localhost:5221 --api-devserver-url http://localhost:7071 --swa-config-location MW-GC.EventManager.Web
```

Open `http://localhost:4280` after the services are ready. In the local SWA mock
sign-in screen, include the `admin` role; this is emulated authentication, not a
production login. Add games and activities before generating an event.

The Web client defaults to its own origin for API calls, so use the proxy rather
than opening port 5221 directly. To override the backend, create Web
`wwwroot/appsettings.json` with `ApiBaseUrl`; cross-origin use also requires API
CORS configuration. Never put secrets in browser configuration.

## Build and test

Run from the repository root:

```sh
dotnet build MW-GC.EventManager.slnx
dotnet test MW-GC.EventManager.slnx
```

The solution-wide commands are the entry point. To run one test project:

```sh
# API logic tests (MSTest)
dotnet test MW-GC.EventManager.Api.Tests/MW-GC.EventManager.Api.Tests.csproj

# Web UI tests (MSTest + bUnit)
dotnet test MW-GC.EventManager.Web.Tests/MW-GC.EventManager.Web.Tests.csproj
```

Package restore requires access to NuGet or a populated local package cache.

## Contributing

- Branch from `dev` when it exists; otherwise use the default branch. Use a
  focused branch such as `feat/short-description` or `docs/short-description`.
- **Target pull requests at `dev` when it exists**, not `main`; otherwise target
  the repository's default branch.
- Keep changes scoped, add regression tests for behavior changes, and run the
  build and relevant tests above before requesting review.
- Use conventional commits, for example `docs(readme): clarify local setup`.
  Include the purpose, related issue, checks/results and limitations in the PR.
- Do not commit secrets, generated build output or local emulator data.

## Further documentation

- [Idempotent customized event creates](docs/idempotent-event-creates.md) — how
  `POST /api/events` uses the `Idempotency-Key` header so retries do not
  create duplicate events.
- [Customized event create retries](docs/event-create-retries.md) — moved;
  points to the idempotent creates guide.
- Agent guides: [issue tracker](docs/agents/issue-tracker.md),
  [triage labels](docs/agents/triage-labels.md) and
  [domain docs](docs/agents/domain.md).
- [Solution/project layout](MW-GC.EventManager.slnx).
- [API startup and storage configuration](MW-GC.EventManager.API/Program.cs).
- [Web startup and API configuration](MW-GC.EventManager.Web/Program.cs).
- [Web launch profile](MW-GC.EventManager.Web/Properties/launchSettings.json).
- [License](LICENSE.txt).
