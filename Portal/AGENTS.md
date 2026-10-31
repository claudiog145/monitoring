# AGENTS.md

This file is written for future coding agents (and humans navigating quickly) working
in `reliable-portal-dotnet`. Read this before making changes.

## What this repository is

A .NET 10 / Azure port of `reliable-portal` (a Node.js app), built for **behavioral
parity** with the original, not a redesign. If you are asked to change behavior, first
check whether the original JS app (`../reliable-portal`) already behaves that way -
if so, this is a parity bug, not a feature request, and the fix should make .NET match
JS, not the other way around. See `MIGRATION.md` for the full component map and
`ARCHITECTURE.md` for how the pieces fit together.

## Solution structure

```
JourneyMonitor.slnx
src/
  JourneyMonitor.Core            <- business logic lives here. No Azure/HTTP/EF/Razor deps.
  JourneyMonitor.Infrastructure   <- implements Core's interfaces (App Insights, SQL, demo fixtures, KQL text)
  JourneyMonitor.Functions        <- thin Azure Functions (isolated worker), one per use case
  JourneyMonitor.Web              <- Razor Pages host: serves static UI + Entra ID + typed client proxy
tests/
  JourneyMonitor.Tests            <- xUnit characterization tests, ported from ../reliable-portal/test/*.test.js
```

## Where business logic lives (and where it must NOT live)

- **Business logic -> `JourneyMonitor.Core`, always.** Journey reconstruction,
  correlation, classification, club/host resolution, KQL text mutation, domain health,
  domain comparison: all of it is in `Core/Mapping`, `Core/Analytics`,
  `Core/Filtering`, `Core/Configuration`, `Core/UseCases`.
- **Never** put business logic inside a `[Function("...")]` method body
  (`JourneyMonitor.Functions/Functions/*.cs`). A Function should: parse/validate the
  HTTP request -> call a `Core.UseCases` interface -> map the result to an
  `HttpResponseData`. Nothing else.
- **Never** put business logic inside a Razor `PageModel.OnGetAsync`/`OnPostAsync`
  (there currently are no interactive PageModels beyond static file serving + the
  `/api/journeys/db`, `/api/journeys/refresh`, and `/api/clubs` proxy endpoints in
  `Program.cs` - keep it that way). A
  page/endpoint should: call `IJourneyMonitorApiClient` -> shape a view model -> render.
  It should never run KQL, correlate telemetry, or classify a journey step itself.
- **`JourneyMonitor.Infrastructure`** implements `Core`'s interfaces
  (`ITelemetrySource`, `ITelemetryQueryCache`, `IDemoJourneyFixtureProvider`,
  `IKqlQueryTextProvider`). It may depend on Azure SDKs, SQL, HTTP - `Core` must
  never depend on `Infrastructure`.

## The one thing you must not "fix" without reading ARCHITECTURE.md first

`GetJourneysService`/`GetJourneysFunction` return **raw KQL rows**
(`TelemetryJourneyRow`), not mapped `CustomerJourney` DTOs, even though
`HotelJourneyMapper`/`AirJourneyMapper` exist and are fully tested. This is
intentional - the served static JS (`wwwroot/map-v6.js`, `wwwroot/map-air.js`) maps
rows into journeys **client-side**, exactly like the original app. If you wire the
mappers into the live API response without also removing/rewriting the client-side
mapping JS (a deliberate, separate, larger change), you will double-map data and
silently break the UI. Read "Why `/api/journeys` and `/api/clubs` return raw rows, not
mapped journeys" in `ARCHITECTURE.md` before touching this.

## Adding a new Azure Function

1. Add/extend a use case in `JourneyMonitor.Core/UseCases/` - a plain class,
   constructor-injected with interfaces only, no Azure/HTTP types in its API.
2. Add a `[Function("...")]` class under `JourneyMonitor.Functions/Functions/` that
   only parses input, calls the use case, and maps the response.
3. Register any new services in `JourneyMonitor.Functions/Program.cs` or
   `JourneyMonitor.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`.
4. If the UI needs it, add a method on `IJourneyMonitorApiClient`
   (`JourneyMonitor.Web/Services/JourneyMonitorApiClient.cs`) and a proxy endpoint in
   `JourneyMonitor.Web/Program.cs`.
5. Write a characterization or unit test in `tests/JourneyMonitor.Tests` before wiring
   it into anything user-facing.

## Adding a new journey analyzer / mapper / calculator

Put it in `JourneyMonitor.Core` (`Mapping/`, `Analytics/`, or a new folder if it's a
genuinely new kind of responsibility - don't force it into an existing class). Depend
only on `Core.Domain` types. Write the test first, porting fixture values from the
matching file in `../reliable-portal/test/*.test.js` if a JS equivalent exists (see
`tests/JourneyMonitor.Tests/Mapping/HotelJourneyMapperTests.cs` for the pattern of
building a `TelemetryJourneyRow` from an inline JSON literal via a `RowFromJson`
helper). If there's no JS equivalent, it's genuinely new functionality - confirm that's
actually intended before adding it, since the migration's explicit goal is parity, not
scope expansion.

## Replacing Application Insights with Microsoft Fabric (when that day comes)

Implement `ITelemetrySource` as a new class in `JourneyMonitor.Infrastructure` (or a
new `JourneyMonitor.Infrastructure.Fabric` project). Swap the DI registration in
`ServiceCollectionExtensions`/`Program.cs`. Do **not** touch `GetJourneysService`,
`ClubHostResolver`, any mapper, or any calculator - if you find yourself needing to,
the abstraction has a leak that should be fixed in `Core`'s interface first.

## Running things

- `dotnet build` at the repo root builds all 5 projects.
- `dotnet test tests/JourneyMonitor.Tests/JourneyMonitor.Tests.csproj` runs all
  characterization/unit tests - no Azure credentials, network access, or database
  required (everything is faked or uses committed fixture files).
- `func start` in `src/JourneyMonitor.Functions` runs the Functions host locally
  (requires Azure Functions Core Tools v4 - not available in every dev sandbox; if
  `func` is missing, you can still build/test the Functions project with `dotnet
  build`/`dotnet test`, you just can't invoke the HTTP triggers end-to-end).
- `dotnet run` in `src/JourneyMonitor.Web` runs the Razor host; static tabs work with
  zero backend, Customer Journeys needs the Functions host running too.

## Before considering a change "done"

Per the project's own standard (see the top-level task instructions this repo was
built from): a change is not done because it compiles or returns HTTP 200. For any
change touching journey reconstruction, correlation, mapping, or classification logic,
it must produce output matching the original JS app's behavior for the same input -
run/extend the characterization tests, don't just eyeball the UI. If you change a
`Core` calculation, re-run `dotnet test` and make sure existing characterization tests
still pass; if a characterization test's expected value needs to change, that's a red
flag - stop and confirm it's not a parity regression before updating the assertion.

## Documentation map

- `README.md` - how to run this locally, prerequisites, configuration.
- `ARCHITECTURE.md` - component diagram, dependency direction, request flow, the
  raw-rows-vs-mapped-journeys decision, source/store split, how to extend each layer.
- `MIGRATION.md` - JS -> .NET component-by-component map, test coverage, known gaps.
- `AGENTS.md` - this file.
