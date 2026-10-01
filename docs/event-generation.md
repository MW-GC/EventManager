# Event generation

`POST /api/events/generate` draws a random set of Selections from the library and **saves the result as a new Event**. It is not a preview: every 201 response is already stored, so `GET /api/events` lists it and `GET /api/events/{id}` returns it. If you do not want the Event, delete it with `DELETE /api/events/{id}`. The customize dialog's own saves go through `POST /api/events` instead (see `idempotent-event-creates.md`).

## Request

```json
{
  "count": 3,
  "uniqueGamesOnly": true,
  "themedOnly": false,
  "selectedGameIds": [],
  "selectedThemeIds": [],
  "selectedHolidayIds": [],
  "utcOffsetMinutes": -300
}
```

- `count` must be between 1 and 5 (`EventEntity.MaximumSelections`), otherwise 400.
- The three id lists must not be null. An empty list means "no filter".
- `utcOffsetMinutes` is optional. See below.

## Response

`201 Created` with the stored Event and `Location: /api/events/{id}`. A request the library cannot satisfy (too few games or activities after filtering) answers 400 and stores nothing. A one-Selection Event gets its only activity as winner; otherwise there is no winner yet.

The Event is written once, with a single upsert to the Events table, after the Selections have been drawn. Nothing is written when validation or the draw fails.

## Name and date

The default name is `Event - ` followed by the current time, formatted `MMM d, h:mm tt` with the invariant culture (for example `Event - Oct 1, 7:30 PM`).

- `utcOffsetMinutes` is the caller's local offset from UTC in minutes. UTC-05:00 is `-300`, UTC+09:00 is `540`, UTC+05:30 is `330`. That is the negation of JavaScript's `Date.prototype.getTimezoneOffset()`.
- The name uses the current time shifted by that offset, so an Event generated at 00:30 UTC on Oct 2 by a caller at UTC-05:00 is named `Event - Oct 1, 7:30 PM`, not Oct 2.
- When the field is missing or null the name uses UTC, as before.
- Valid offsets are -840 to 840 (UTC-14:00 to UTC+14:00). Anything else answers 400 `UtcOffsetMinutes must be between -840 and 840.` and stores nothing.
- The offset only affects the name. The stored `date` is always the UTC instant of generation, and the name and the date come from the same clock read.

## Winner pick

`POST /api/events/{id}/winner` picks one of the Event's Selections uniformly at random, stores its activity id as `winnerActivityId`, and returns the updated Event with 200. An unknown id answers 404 and an Event with no Selections answers 400. Each call picks again, so calling it twice can change the winner.

## Clock and randomness

`EventGenerator` owns both sources of nondeterminism:

- a `TimeProvider` for the current time (name and date). `Program.cs` registers `TimeProvider.System` as a singleton and dependency injection hands it to the generator.
- a `Random` for the draw and for the winner pick (`EventGenerator.PickWinner`). The Functions host uses `Random.Shared`.

Tests construct the generator with a fake `TimeProvider` and a seeded or scripted `Random`, which pins the name, the date, the drawn Selections and the winner. `EventGenerationClockAndRngTests` covers the local-date name, the offset range, the seeded winner pick, and the single upsert.

## Count below 1

The HTTP route rejects `count` outside 1..5 before the generator runs. The generator itself returns null (no result, never an exception) for a negative count, in both unique-games and repeated-games mode. A count of 0 returns an empty list in both modes.
