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

The guarded routes are `POST /api/games`, `PUT /api/games/{id}`, `POST /api/themes`, `PUT /api/themes/{id}`, `POST /api/holidays`, `PUT /api/holidays/{id}`, `POST /api/activities`, `PUT /api/activities/{id}`, `POST /api/activities/{id}/comments`, `PUT /api/activities/{id}/comments/{commentId}`, `POST /api/events`, `PUT /api/events/{id}` and `PUT /api/events/{id}/metrics`. An update to an id that does not exist is still a 404, checked before the body is read; for the metrics `PUT` that is an unknown Event. For Comments, a `POST` to an Activity that does not exist and a `PUT` of a Comment that does not exist or belongs to another Activity are also 404, checked before the body is read.

## Field rules (400, the message names the field)

| Entity | Field | Rule |
| --- | --- | --- |
| Game, Theme, Holiday, Activity, Event | `Name` | Trimmed, then required and at most 100 characters. The trimmed value is stored. Messages: `Name is required.` / `Name must be 100 characters or fewer.` |
| Game | `Website` | Optional: `null`, empty or whitespace is valid. Otherwise at most 2048 characters (checked first, `Website must be 2048 characters or fewer.`), then an absolute `http` or `https` URI, in any case (`Website must be an http or https address.`). So `ftp://...`, `javascript:...` (in any case, with or without leading spaces), `data:...` and a bare `example.com` are refused. The value is stored as sent, not trimmed (unlike `Name`). The rule (`FieldRules.HttpAddress`) runs only on create and update: a Website stored before it still reads back unchanged, and every save of that Game that still sends it is refused, so renaming an old Game whose Website is not http(s) is blocked until the Website is fixed. The Games editor checks the same rule and message before it sends anything, and shows a stored Website as a link only when it passes. |
| Game | `ImageUrl`, `IconUrl` | Optional, at most 2048 characters. Length only; there is no URL-scheme check. |
| Activity | `Description`, `Rules`, `SetupRequirements` | At most 2000 characters each. The Activity's old single text field is gone (#133): a `comments` value in the body, of any length, is ignored and not stored. A Web build left open across the deploy keeps sending it, and the API ignores it, so text typed into the old editor in that window is lost. An Activity's Comments are rows of their own (below). |
| Activity | `GameId` | Required (`GameId is required.`) and must name an existing Game (`Game not found.`). The Activities editor checks the required rule first, before it sends anything, with its own wording: `Game is required.` |
| Activity | `ThemeIds`, `HolidayIds` | Lenient: a missing or `null` list is stored as `[]`, and duplicates are removed. Whether the ids exist is not checked. |
| Event | `Selections` and the rest | Unchanged: the existing `ValidateSelections` messages, run after the name rule. |
| Comment | `Text` | Trimmed, then required and at most 1000 characters. The trimmed value is stored. Messages: `Comment text is required.` / `Comment text must be 1000 characters or fewer.` |
| Comment | `EventId` | Optional. On add it must name an existing Event (`Event not found.`), checked after the text rule. An edit never changes it. |
| Event metrics | `Attendees`, `NewAttendees`, `ReturningAttendees` | Optional (`null` allowed), otherwise a whole number from 0 to 1,000,000. Messages: `Attendees must be a whole number from 0 to 1000000.` / `New attendees must be a whole number from 0 to 1000000.` / `Returning attendees must be a whole number from 0 to 1000000.` A fractional or non-numeric count cannot be read into the integer field, so it is the malformed-body 400 (`Request body is empty or malformed.`). |
| Event metrics | `AmountSpent` | Optional (`null` allowed), otherwise 0 to 1,000,000,000 with at most 2 decimal places. Message: `Amount spent must be between 0 and 1000000000 with at most 2 decimal places.` |

Event metrics fields are checked in the order above and the first failing field wins. There is no cross-check between attendees, new and returning. A body with every field `null` is valid and is stored: it clears the values.

The event-create idempotency flow is unchanged: 201 on first insert, 200 on a matching replay, 409 when the details differ, and 400 for a bad `Idempotency-Key` (see `idempotent-event-creates.md`).

## Delete refusal (Games, Themes, Holidays)

A Game, Theme or Holiday that Activities still use cannot be deleted. The route reads every Activity (they all share one partition), counts the ones that use the id, and when that count is above zero answers 409 and deletes nothing. Nothing is cascaded and no id is stripped from an Activity.

| Request | Uses counted | Status | Body |
| --- | --- | --- | --- |
| `DELETE /api/games/{id}` | Activities whose `GameId` is the id | 409 | `This game is used by N activities. Delete or move them first.` (`1 activity`, `that activity` for one) |
| `DELETE /api/themes/{id}` | Activities whose `ThemeIds` contain the id | 409 | `This theme is used by N activities. Remove it from them first.` (`1 activity`, `that activity` for one) |
| `DELETE /api/holidays/{id}` | Activities whose `HolidayIds` contain the id | 409 | `This holiday is used by N activities. Remove it from them first.` (`1 activity`, `that activity` for one) |
| Any of the three, row not used by any Activity | | 204 | |
| Any of the three, id that does not exist | | 204 | (idempotent, as before) |

The count and the delete are two separate storage calls with no lock, so an Activity created between them can still end up pointing at a deleted row. That race is accepted. A storage failure while counting goes through the failure mapping below like any other route (for example a 503 for an outage), and nothing is deleted. Saved Events are unaffected: they keep their own copies of the Game and Activity they selected.

## Comments

| Request | Status |
| --- | --- |
| `GET /api/activities/{id}/comments` | 200 with the Activity's Comments, oldest first by added date (`createdUtc`) then id; 404 for an unknown Activity |
| `POST /api/activities/{id}/comments` | 201 with `Location: /api/activities/{id}/comments/{commentId}`, the `ETag` header and the Comment. The API sets `id`, `activityId` and `createdUtc` and ignores them in the body. 404 for an unknown Activity; 400 for an unknown Event |
| `PUT /api/activities/{id}/comments/{commentId}` | 200 with the Comment. Only the text changes; the added date, the Activity and the Event are kept. 404 when the Comment is missing or belongs to another Activity |
| `DELETE /api/activities/{id}/comments/{commentId}` | 204, idempotent. A Comment of another Activity is also 204 and nothing is deleted |

A Comment in a response carries the same storage keys every other entity's does (`partitionKey`, `rowKey`, `timestamp`, and `eTag` below) beside its own fields `id`, `activityId`, `text`, `createdUtc` and `eventId`; the conversion's marker row is never in a response.

Each Comment is its own row in the `ActivityComments` table (ADR 0001). Deleting an Activity deletes its Comments, and a retried Activity delete after the Activity row is already gone still deletes them. Deleting an Event leaves Comments written from it in place, with their `eventId`. Duplicating an Activity copies none of its Comment rows: a duplicate starts with no Comments, because Comments are rows of their own and belong to the original.

The Activity's old single text and its `PATCH /api/activities/{id}/comments` route were removed in #133; that `PATCH` is no longer a route. An Event's Selection snapshot no longer carries the Activity's Comments either. An Event stored earlier whose snapshot still has a `comments` value loads without it (the value is skipped on read), and its next save writes the snapshot without it.

Old texts become Comments (#130). The conversion runs until the marker below is stored, and the marker stays `activity-notes-v1`. Each time the Function App host starts, a background service (`ActivityNoteConversionStartup`) runs `ActivityNoteConversion`, and until the marker is stored every Activity `PUT` runs the same pass first (waiting for a pass already running; two passes never overlap). Since the Activity no longer has the field, the conversion reads the `Activities` table (partition `Activity`) as raw rows and takes the old text from each row's `Comments` column; a row without that column, or whose `Comments` value is not a string, is skipped. Every row whose old text is non-empty after trimming gets one Comment with that trimmed text, `eventId` null, and `createdUtc` set to the Activity row's last write (its Table Storage `Timestamp`). A text over 1000 characters is cut to at most 1000 characters (fewer when the cut would split a character made of two UTF-16 units), then trimmed again, and a warning naming the Activity is logged: the rest of the text is not kept, and it is not split into more Comments. The Comment itself is never dropped. The Activity row is only read, so the old column stays on rows the API has not written since. An Activity `PUT` replaces the row and drops that column, so it never writes before the pass has finished: a `PUT` that arrives while the startup pass runs, or after it failed, runs or waits for the pass, and when the pass fails the `PUT` answers 503 `Storage is temporarily unavailable. Try again shortly.` and writes nothing. Creating or duplicating an Activity writes a new row and deleting one removes its old text with it, so those do not wait. The Comment's id is derived from the Activity id and the row is inserted, never replaced, so a repeated pass adds no duplicate. A finished pass stores one marker row in `ActivityComments` (partition `Migration`, row `activity-notes-v1`), outside the Comment partition, so no Comment list or Activity-delete cascade sees it; a start that finds it does nothing, so a converted Comment that was deleted does not come back. A pass that fails part-way is logged, stores no marker, never stops the host, and is retried at the next host start or the next Activity `PUT` (#187). So a converted Comment deleted while a pass is failing, or while a second instance's pass is still running, can be added again by the pass that finishes; it is never duplicated, because the id is derived and the row only inserted. The conversion can be deleted once every deployment has stored the marker. `ActivityNoteConversionTests` covers this over the in-memory tables; `ActivityNoteConversionAzuriteTests` runs it against Azurite (the host-start pass, the stored `ActivityId` column type the Comment filter needs, and the list route) and skips itself unless `EM_AZURITE_CONNECTION` is set (see the README).

## Conflict-safe writes (ETags and If-Match)

Every Game, Theme, Holiday, Activity, Event and Comment carries its Table Storage ETag as the string property `eTag` in the JSON body (lists included), and every single-entity response (GET one, POST, PUT, the Comment `POST` and `PUT`) also sends it as the HTTP `ETag` header. A client may echo it as `If-Match` on `PUT`, `DELETE` and the Comment `PUT` and `DELETE`; the value is passed to storage as given. If-Match is optional, and only the header counts: an `eTag` in a request body is ignored.

| Request | Status |
| --- | --- |
| `PUT` (other than the Event metrics `PUT`, below) with a current `If-Match`, or with none, or with `If-Match: *` | 200, with the new ETag |
| `PUT` with a stale `If-Match` | 409 `The request conflicts with the stored data. Reload and try again.`; the row is unchanged |
| `PUT` for a row that was deleted, even between the existence check and the write, with or without `If-Match` | 404; the row is not recreated (updates are never upserts) |
| `DELETE` with a stale `If-Match` | 409; nothing is deleted |
| `DELETE` of a row that does not exist | 204 (idempotent, as before) |
| `PUT /api/activities/{id}/comments/{commentId}` with a stale `If-Match` | 409; the text is unchanged |
| `DELETE /api/activities/{id}/comments/{commentId}` with a stale `If-Match` | 409; the Comment is kept |

Each Comment row has its own eTag, so editing one Comment never makes another Comment or the Activity stale.

An Event's metrics row carries an ETag too, in the body and the `ETag` header. `GET /api/events/{id}/metrics` of an Event with no stored row answers 200 with every field `null`, the Event id, and no `ETag` header. `PUT /api/events/{id}/metrics` is the deliberate exception to "updates are never upserts": it creates the row when none exists. Without `If-Match` it only creates, as an insert, so two first saves cannot overwrite each other.

| `PUT /api/events/{id}/metrics` | Status |
| --- | --- |
| No `If-Match`, no stored row | 200, the row is created, with its ETag |
| No `If-Match`, a row is stored (including one another first save just created) | 409 `The request conflicts with the stored data. Reload and try again.`; the row is unchanged |
| Current `If-Match`, or `If-Match: *`, on a stored row | 200, with the new ETag |
| Stale `If-Match` | 409; the row is unchanged |
| Any `If-Match` (including `*`) with no stored row | 409; nothing is created |
| Unknown Event | 404 |

Deleting an Event also deletes its metrics row, after the Event delete answers 204 (including a retried delete of an Event already gone). A 409 delete deletes neither.

## Event metrics for every Event (the Stats page)

`GET /api/event-metrics` (#136) is the one bulk read behind Activity performance, read by the Stats page and by the Activity details dialog. It takes no body, sends no `ETag` and writes nothing. It answers 200 with a JSON array of `{ eventId, attendees, newAttendees, returningAttendees, amountSpent, likes }`: one item for every Event that has a stored metrics row or at least one Like, the metric fields `null` when not recorded and `likes` the Event's Like count. An Event with neither has no item, which the browser reads as no metrics and 0 Likes. The Like counts come from one pass over the `FeedbackLikes` table that reads only the keys. Like rows are written under the Event id in its hyphenated form, so a partition key that is not a Guid in that form (not a Guid at all, or one in braces or without hyphens) is skipped. Whether each Event still exists is not checked: the browser matches items to its Events list by id and ignores the rest. A storage failure goes through the failure mapping below like every other route (for example a 503 for an outage).

## Long values (chunked properties)

Table Storage limits one string property to 64 KiB of UTF-16, which is 32,768 characters. A complex property (an Event's `Selections`, an Activity's `ThemeIds`/`HolidayIds`) is stored as compact JSON, with non-ASCII text kept as the character rather than a `\uXXXX` escape. When that JSON is longer than 30,000 characters it is split over `Selections`, `Selections__1`, `Selections__2` ... and joined again on read. Five Selections with every field at its cap are about 70,000 characters, so three chunks. The API contract does not change, and a row written before chunking (one property) reads back unchanged.

## Failures after validation (every route)

| Failure | Status | Body | Logged as |
| --- | --- | --- | --- |
| Table Storage conflict (409) or failed `If-Match` precondition (412), other than the event-create idempotency path that the route handles itself | 409 | `The request conflicts with the stored data. Reload and try again.` | Warning |
| A complex property (for example an Event's `Selections`) that would push the row past Table Storage's 1 MiB entity limit or 252 custom properties, even split into chunks; refused before anything is written | 413 | `<Field> is too large to store.` (for example `Selections is too large to store.`) | Warning |
| Entity or property too large for Table Storage (`EntityTooLarge`, `PropertyValueTooLarge`, `RequestBodyTooLarge`, or a 413) | 413 | `The item is too large to store.` | Warning |
| Storage outage or timeout: a `RequestFailedException` with status 500 or above, or status 0 (transport); an `HttpRequestException`; a `TaskCanceledException` that is not the caller's own cancellation; an `AggregateException` made only of these | 503 | `Storage is temporarily unavailable. Try again shortly.` | Error |
| Anything else | 500 | `An unexpected error occurred.` | Error |

When the caller cancels the request, the cancellation is rethrown to the host. It is not turned into a response and it is not logged as a failure.

Every failure is logged through `ILogger<T>` in the Functions class, with the exception and the function name. That output reaches the Functions host log, and Application Insights too when the Function App has it configured. The two unused Application Insights worker packages and the commented-out registration block were removed. Nothing in the worker had used them.

## Test coverage

`RequestBodyGuardTests` calls the guard directly, with no Functions host. `EntityValidatorTests` covers the Game, Theme, Holiday, Activity, Event and Event metrics validators, the Game Website rule included (accepted and refused values, and the length check coming first); the Comment text rule is covered through the routes in `CommentApiTests`. `ApiValidationRouteTests` drives every body-reading route but the Event metrics `PUT` (the Comment add and edit included) through the real Functions classes and `TableStore` over in-memory mocked tables, and checks that rejected requests write nothing. It also covers the Game Website rule on create and update (`ftp://x`, `javascript:alert(1)`, `JavaScript:alert(1)`, ` javascript:alert(1)` with a leading space, `data:text/html,x` and `example.com` refused with nothing written; `http://x`, `HTTPS://x`, empty, whitespace and `null` accepted), and a Game stored with `example.com` still returned by `GET` and refused on a save that still sends it; `RetiredCommentsFieldTests` covers a `comments` value over the old 2000-character cap ignored on Activity create and update. The Event metrics `PUT`'s malformed-body 400 is covered in `EventMetricsTests`. `StorageFailureTests` covers the failure mapping and logging with a mocked `TableClient`, because a running instance cannot simulate a storage outage. `DeleteInUseRefusalTests` covers the delete refusal for all three entities, an Event keeping its snapshot after a Game delete, and a failure while counting. `ConditionalWriteTests` covers, for Games, Themes, Holidays, Activities and Events, If-Match on every `PUT` and `DELETE`, the update-that-races-a-delete 404, and the ETag header and body; the Comment routes' If-Match and ETags are covered in `CommentApiTests`. `OldSelectionSnapshotTests` covers an Event stored with `comments` in its Selection snapshot: it loads with every other field, and its next save writes the snapshot without it. `PropertyLimitTests` covers chunking a five-Selection Event at every cap, old-shape rows, the row-limit 413 and the 412 mapping. `CommentApiTests` covers the Comment routes through the real Functions classes over in-memory tables: add, list order, edit, delete, the text rule, an unknown Activity or Event, bad bodies, If-Match on edit and delete, the Activity-delete cascade (including a retried delete, a stale one, and a failed Comment cleanup that is logged and answers 503), an Event delete leaving Comments in place, a duplicate copying no Comment rows, and a list or Activity delete whose query storage answers with only that Activity's Comments. `StorageQueryAzuriteTests` sends those two queries, and the bulk Like count, to Azurite. `TableStoreTests` covers `GetWhereAsync`, its refusal for an entity with a complex property, and that the Comment entity has none. `EventMetricsTests` drives the Event metrics routes through `LibraryHarness`: the field limits, the If-Match and first-save rules, concurrent first saves, and the Event delete cascade; and the bulk `GET /api/event-metrics`: no data, metrics only, Likes only, both for one Event, two Events next to one with neither, a partition key that is not a Guid or is a Guid in another form, one key-only query of the whole Likes table, a storage outage on either table (the mapped 503, logged), and the JSON shape.
