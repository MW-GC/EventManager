# API request validation and error responses

Every API route that takes a body reads it through one shared guard (`Validation/RequestBody.cs`), and every route runs inside one shared failure mapper (`Validation/RouteFailures.cs`). Each entity has one validator (`Validation/EntityValidators.cs`), and its create and update routes both call it, so the two cannot drift apart. Error bodies are short plain strings. They never contain a stack trace or exception text.

All routes stay `AuthorizationLevel.Anonymous` on purpose: the Static Web App route table enforces the `admin` role in front of this bring-your-own backend.

## Request bodies (every body-reading route, Events included)

| Request | Status | Body |
| --- | --- | --- |
| `Content-Type` present and not `application/json` or a `+json` type (for example `text/plain`) | 415 | `Content-Type must be application/json.` |
| No `Content-Type` header | read as JSON | |
| Empty body, truncated or malformed JSON, wrong JSON shape or value type | 400 | `Request body is empty or malformed.` |
| The JSON literal `null` | 400 | `Request body must not be null.` |

Property names are case-insensitive and camelCase, as before. A `charset` other than UTF-8 is transcoded. The body is read and deserialised once.

The guarded routes are `POST /api/games`, `PUT /api/games/{id}`, `POST /api/themes`, `PUT /api/themes/{id}`, `POST /api/holidays`, `PUT /api/holidays/{id}`, `POST /api/activities`, `PUT /api/activities/{id}`, `PATCH /api/activities/{id}/comments`, `POST /api/events/generate`, `POST /api/events` and `PUT /api/events/{id}`. An update to an id that does not exist is still a 404, checked before the body is read.

## Field rules (400, the message names the field)

| Entity | Field | Rule |
| --- | --- | --- |
| Game, Theme, Holiday, Activity, Event | `Name` | Trimmed, then required and at most 100 characters. The trimmed value is stored. Messages: `Name is required.` / `Name must be 100 characters or fewer.` |
| Game | `Website`, `ImageUrl`, `IconUrl` | Optional, at most 2048 characters. Length only; there is no URL-scheme check. |
| Activity | `Description`, `Rules`, `SetupRequirements`, `Comments` | At most 2000 characters each. The comments `PATCH` route uses the same `Comments` rule. |
| Activity | `GameId` | Required (`GameId is required.`) and must name an existing Game (`Game not found.`). |
| Activity | `ThemeIds`, `HolidayIds` | Lenient: a missing or `null` list is stored as `[]`, and duplicates are removed. Whether the ids exist is not checked. |
| Event | `Selections` and the rest | Unchanged: the existing `ValidateSelections` messages, run after the name rule. |

`POST /api/events/generate` keeps its own rules unchanged: `Count` from 1 to 5, non-null filter lists, `UtcOffsetMinutes` from -840 to 840. The event-create idempotency flow is unchanged too: 201 on first insert, 200 on a matching replay, 409 when the details differ, and 400 for a bad `Idempotency-Key` (see `idempotent-event-creates.md`).

## Failures after validation (every route)

| Failure | Status | Body | Logged as |
| --- | --- | --- | --- |
| Table Storage conflict (409), other than the event-create idempotency path that the route handles itself | 409 | `The request conflicts with the stored data. Reload and try again.` | Warning |
| Entity or property too large for Table Storage (`EntityTooLarge`, `PropertyValueTooLarge`, `RequestBodyTooLarge`, or a 413) | 413 | `The item is too large to store.` | Warning |
| Storage outage or timeout: a `RequestFailedException` with status 500 or above, or status 0 (transport); an `HttpRequestException`; a `TaskCanceledException` that is not the caller's own cancellation; an `AggregateException` made only of these | 503 | `Storage is temporarily unavailable. Try again shortly.` | Error |
| Anything else | 500 | `An unexpected error occurred.` | Error |

When the caller cancels the request, the cancellation is rethrown to the host. It is not turned into a response and it is not logged as a failure.

Every failure is logged through `ILogger<T>` in the Functions class, with the exception and the function name. That output reaches the Functions host log, and Application Insights too when the Function App has it configured. The two unused Application Insights worker packages and the commented-out registration block were removed. Nothing in the worker had used them.

## Test coverage

`RequestBodyGuardTests` calls the guard directly, with no Functions host. `EntityValidatorTests` covers each validator. `ApiValidationRouteTests` drives every body-reading route through the real Functions classes and `TableStore` over in-memory mocked tables, and checks that rejected requests write nothing. `StorageFailureTests` covers the failure mapping and logging with a mocked `TableClient`, because a running instance cannot simulate a storage outage.
