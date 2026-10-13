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
requirements. Every Activity belongs to exactly one Game.

**Theme**:
A named tag for Activities, such as Fantasy or Horror. An Activity can have any
number of Themes.

**Holiday**:
A named tag for Activities, such as Halloween. It is a label, not a date. An
Activity can have any number of Holidays.

**Themed activity**:
An Activity with at least one Theme or Holiday.

**Comment**:
A dated, plain-text note about how an Activity went. An Activity has any number
of Comments, each with its own added date, and each can be edited or deleted. A
Comment written from an Event's details lands on the Activity that won that
Event and records which Event it came from. This replaces the Activity's old
single text field.
_Avoid_: note, Activity comments

### Events

**Event**:
A named, dated gaming event made of one to five Selections, with at most one
Winner.

**Selection**:
One Game and one of that Game's Activities chosen for an Event, stored as a
snapshot of the Game and Activity as they were when the Selection was chosen.
Editing the Event keeps that snapshot, even if the Game or Activity has since
changed or been deleted, unless the user changes that slot. An Activity can
appear in an Event only once. The snapshot does not carry the Activity's
Comments; those are always read live from the Activity.

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

**Activity performance**:
How an Activity has done across every Event it appeared in: appearances, wins,
win rate, and the average Likes and attendees of those Events. It is raw
numbers, never a single score, and it is worked out when read, not stored. It
cannot tell a weak Activity from a weak night, because an Event has several
Selections.

**slot**:
A position for one Selection while an Event is put together in the create or
edit Event dialog. Re-rolling a slot
gives it a different random Activity that matches the Filters and does not
clash with the other slots, which stay unchanged.

**Filters**:
The choices that narrow which Activities can fill a slot: specific Games,
Themes or Holidays, and Themed activities only.

## Relationships

- A **Game** has many **Activities**; each **Activity** belongs to one **Game**.
- An **Activity** has zero or more **Themes** and zero or more **Holidays**.
- An **Event** has one to five **Selections**; each **Selection** is one
  **Game** and one of its **Activities**.
- An **Event** has zero or one **Winner**, which is one of its **Selections**.
- Each **slot** becomes one **Selection** when the **Event** is saved.
- An **Activity** has zero or more **Comments**; a **Comment** may record the
  **Event** it was written from.
- An **Event** has zero or one set of **Event metrics**.
- **Activity performance** is built from the **Event metrics** and **Likes** of
  the **Events** an **Activity** appeared in.

## Storage

Two admins editing the same Game, Activity, Theme, Holiday, Event or Comment cannot silently
overwrite each other: every item carries an `eTag`, and a save or delete that sends it
back as `If-Match` after someone else saved is refused with 409. An edit never brings
back an item that was deleted in the meantime (404). See `docs/api-validation.md`.

An Event keeps its Selections as one JSON value. When that value is too long for a
single Table Storage property it is stored in numbered chunks (`Selections`,
`Selections__1`, ...) and joined again on read; older Events stored in one piece
read back unchanged.
