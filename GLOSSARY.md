# EventManager

EventManager plans gaming events for admins. It keeps a library of games and
their activities, puts activities together into an event, and records which
one won.

## Language

### Library

**Library**:
Everything an Event can draw from: the Games, their Activities, and the Themes
and Holidays used to tag those Activities.

**Game**:
A game in the Library, with a name and optional image, icon and website links.

**Activity**:
Something to play in one Game, with a description, rules and setup
requirements. Every Activity belongs to exactly one Game. Planned for 0.6.0
(#203), not built yet: optional minimum players, maximum players and duration
in minutes, each a whole number of at least 1, the minimum at most the maximum.

**Theme**:
A named tag for Activities, such as Fantasy or Horror. An Activity can have any
number of Themes.

**Holiday**:
A named tag for Activities, such as Halloween. It is a label, not a date. An
Activity can have any number of Holidays. Planned for 0.6.0 (#204): a Series'
holiday preset (such as Thanksgiving) is a built-in date rule, not a Holiday.

**Themed activity**:
An Activity with at least one Theme or Holiday.

**Comment**:
A dated, plain-text note about how an Activity went. An Activity has any number
of Comments, each with its own added date, and each can be edited or deleted. A
Comment written from an Event's details lands on the Activity that won that
Event and records which Event it came from. This replaces the Activity's old
single text field.
_Avoid_: note, Activity comments (`ActivityNoteConversion` and its marker
`activity-notes-v1` keep the word because they name the retired column)

### Events

**Event**:
A named, dated gaming event made of one to five Selections, with at most one
Winner. Planned for 0.6.0 (#204): an Event that is an Occurrence may have zero
Selections until they are rolled.

**Selection**:
One Game and one of that Game's Activities chosen for an Event, stored as a
snapshot of the Game and Activity as they were when the Selection was chosen.
Editing the Event keeps that snapshot, even if the Game or Activity has since
changed or been deleted, unless the user changes that slot. An Event may not
have the same Activity in two Selections: a save that does is refused. The
snapshot carries no Comments; Comments are records of their own, read live.

**Unique games only**:
An Event setting, on by default. When it is on, every Selection in the Event
uses a different Game.

**Winner**:
The Selection that won the Event, identified by its Activity because a Game can
appear in more than one Selection. An Event with exactly one Selection has that
Selection as its Winner.
_Avoid_: winning game

**Event metrics**:
The optional numbers recorded about an Event after it was played: attendees,
new attendees, returning attendees and amount spent. They belong to one Event
and are kept apart from it, so editing the Event's Selections never changes
them.
_Avoid_: figures, attendance

**Like**:
One visitor's thumbs-up for an Event, given through the Event's feedback link.
A visitor address counts once per Event.

**Offered**:
The number of Events whose Selections include an Activity, each Event counted
once; only Events dated up to now count. The Stats page counts it for Games the
same way. Beside it, "wins" stays a plain lowercase word, not a term: the
number of those Events whose Winner is that Activity (or has that Game).
_Avoid_: appearances, appeared in

**Activity performance**:
How an Activity has done across every Event that offered it: Offered, wins, win
rate, average Likes and average attendees of those Events. It is raw numbers,
never a single score, and it is worked out when read, not stored. It cannot
tell a weak Activity from a weak night, because an Event has several
Selections.

**slot**:
A position for one Selection while an Event is put together in the create or
edit Event dialog. Re-rolling a slot
gives it a different random Activity that matches the Filters and does not
clash with the other slots, which stay unchanged. Planned for 0.6.0 (#204): a
Series' slot template holds how many slots, and which Filters, its
Occurrences start with when rolled.

**Filters**:
The choices that narrow which Activities can fill a slot: specific Games,
Themes or Holidays, and Themed activities only. Planned for 0.6.0 (#203), three
more, each off by default:
- avoid recent: skips Activities offered in the last N Events (1 to 10, 3 when
  turned on) dated before the Event being planned, not counting the Event being
  edited. It skips Activities, never Games, never relaxes itself, and applies to
  Randomize All, re-roll and Add Activity Slot, not to a slot changed by hand.
- players tonight: keeps an Activity unless its minimum or maximum players
  excludes that number; a missing bound is open.
- longest duration: keeps an Activity with no duration or one within the limit.

**Announcement**:
Planned for 0.6.0 (#203): ready-to-paste Discord text for an Event, copied from
its details. A fixed template: the Event name as a bold title, the date as a
Discord timestamp, then one line per Selection with its Game and Activity (and
players and duration when set), the Winner's line marked. An Event with no
Selections yet says the picks are still to come.

**Series**:
Planned for 0.6.0 (#204): a recurrence rule with a start, a slot template (slot
count, Filters and Unique games only) and an optional end, from which the app
generates Occurrences ahead of time. Its rule is weekly, every other week,
monthly on the nth weekday or on a date, yearly on a date, or yearly on a
holiday preset (such as Thanksgiving or Easter). It is stored in its own table,
apart from its Events; the Series ticket records why in a new ADR 0002.

**Occurrence**:
Planned for 0.6.0 (#204): an Event that belongs to a Series, generated for one
instant of its rule (its occurrence key). It is an ordinary Event with its own
Selections, Winner, Comments and Event metrics, and may have no Selections
until they are rolled. Editing one by hand marks it Modified; a Modified or
played Occurrence (one with a Winner, Comments or Event metrics) is never
rewritten by an edit to its Series. An edit or cancel asks for its scope: this
occurrence, this and following, or all.

## Relationships

- A **Game** has many **Activities**; each **Activity** belongs to one **Game**.
- An **Activity** has zero or more **Themes** and zero or more **Holidays**.
- An **Event** has one to five **Selections**; each **Selection** is one
  **Game** and one of its **Activities**. (Planned for 0.6.0: an
  **Occurrence** may have zero.)
- An **Event** has zero or one **Winner**, which is one of its **Selections**.
- Each **slot** becomes one **Selection** when the **Event** is saved.
- An **Activity** has zero or more **Comments**; a **Comment** may record the
  **Event** it was written from.
- An **Event** has zero or one set of **Event metrics**.
- **Activity performance** is built from the **Event metrics** and **Likes** of
  the **Events** that offered an **Activity**.
- Planned for 0.6.0: a **Series** has many **Occurrences**; an **Occurrence**
  is an **Event**.

## Storage

Two admins editing the same Game, Activity, Theme, Holiday, Event or Comment cannot silently
overwrite each other: every item carries an `eTag`, and a save or delete that sends it
back as `If-Match` after someone else saved is refused with 409. An edit never brings
back an item that was deleted in the meantime (404). See `docs/api-validation.md`.

An Event keeps its Selections as one JSON value. When that value is too long for a
single Table Storage property it is stored in numbered chunks (`Selections`,
`Selections__1`, ...) and joined again on read; older Events stored in one piece
read back unchanged.
