# A recurring Event is a Series that materialises Occurrences

A recurring Event is a Series: a rule, a start instant (its anchor, from which the first Occurrence is the first rule date at or after it: the start itself for a weekly, every-other-week or same-date rule, and possibly a later date for an nth-weekday or holiday rule), and a slot template. The app materialises Occurrences ahead of time, and each Occurrence is an ordinary Event row with its own Selections, Winner, Comments and Event metrics, so the Winner flow, the details dialog, Stats and the 0.7.0 Discord bot work on real rows. The Series is its own row in its own table with its own eTag and its own routes, following ADR 0001. An Occurrence carries the id of its Series, the instant it was generated for (its occurrence key, which is the instant itself), and a Modified flag (#204).

An Occurrence may have zero Selections until it is rolled, while a standalone Event keeps one to five. The Series stores a slot template instead of Selections: the slot count, the Filters with the avoid-recent count, the players and duration, and Unique games only. Rolling an Occurrence, by hand in the edit Event dialog or by the 0.7.0 bot, uses the Library as it is on that day, so Selections never go stale as the Library grows.

Times are stored as instants. Calendar arithmetic (weekday, nth weekday, date, holiday date) happens at the UTC offset of the Series' first instant, with no time zone database: every Occurrence keeps the same UTC time, and the local clock time drifts by an hour across a daylight saving change in the viewer's zone, as Discord's own scheduled events do. This drift is accepted; a toggle to follow daylight saving is filed for later (#206). The browser shows every instant in the viewer's time zone.

A Series may have an optional end: no Occurrence is ever made after it, an Occurrence exactly at it is, and it must be after the first instant. "This and following" uses the same end to stop the old Series just before the chosen Occurrence.

Occurrences exist for the next 8 weeks, and at least the next one however far out it is (never past the Series' end). One pure generator in the Shared project decides which Occurrences to create from the Series, the current time, the existing occurrence keys and the cancelled ones. A new Series runs it at once; a daily timer function runs it for every active Series to extend the horizon. The timer has no HTTP surface, so the anonymous HTTP triggers and the Static Web App link are unchanged. An Occurrence's id is derived from its Series id and occurrence key and created with the existing atomic add, so a retried or overlapping run never duplicates or overwrites one.

Holiday presets (Easter, Thanksgiving, Halloween and the others) come from an in-house rules table in the Shared project, with no external service and no network call. Adding a preset is one table row. A holiday preset is a date rule and is not the Holiday tag on Activities.

Edits and cancels name a scope, as in Outlook:

- **This Occurrence**: edits that one Event and marks it Modified. A Modified Occurrence is never overwritten by a later Series edit.
- **This and following**: ends the current Series before that Occurrence and starts a new Series with the change, moving the unmodified, unplayed future Occurrences to it. The old Series records where it was split, so no later edit moves its end past that point or makes an Occurrence on or after the day the new Series starts, and repeating the same edit finishes one that stopped part-way. The new Series records the same point, so no later `all` edit moves its start back before it.
- **All**: touches only Occurrences that are in the future, not Modified, and not played (no Winner, Comments or Event metrics).
- **Cancel an Occurrence**: deletes its Event row and records its key as skipped on the Series, so the generator never makes it again.
- **Cancel the Series**: deletes it and only its future, unplayed Occurrences; past and played ones stay as history.

The skipped keys are one list with one meaning, "never make this instant again". It holds the cancelled Occurrences' keys, and also the instants an edit would make on a date an Occurrence that stays (Modified, played or past) already holds, so the edit never puts a second Occurrence beside it. Anything that needs the cancelled dates alone, a view of them or an un-cancel, cannot read them from this list.

The 0.7.0 bot renaming an Occurrence to its winning Activity does not count as a manual edit and leaves Modified alone, so Series edits still reach it.

## Considered Options

- Compute Occurrences when they are read and store only the Series: rejected, because there would be nothing to attach Selections, a Winner, Comments, Event metrics or the bot's vote to.
- Roll every Occurrence's Selections when it is generated: rejected, because Selections made weeks ahead would miss Games and Activities added since.
- Follow a time zone so the local clock time never drifts: deferred to #206; it needs a stored time zone and a time zone database in the generator.
- Fetch holiday dates from a public holiday API: rejected, because a fixed calendar rule would then depend on a network call and on which holidays the service lists for a country.

## Consequences

- Everything that reads Selections (the Events list, the details dialog, the Winner flow, Stats, Activity performance, the announcement text, the idempotent create) must cope with an Event that has none.
- A Series edit and the edits of its Occurrences are separate writes on separate rows; a failure can leave some Occurrences updated and others not. Repeating the same edit finishes it; the daily run only adds missing Occurrences and does not repair it.
- Skipped occurrence keys live on the Series row and grow by one per cancelled Occurrence, and by the held dates an edit records.
- The API gains a timer trigger package for the daily run.
