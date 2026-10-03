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

### Events

**Event**:
A named, dated gaming event made of one to five Selections, with at most one
Winner.

**Selection**:
One Game and one of that Game's Activities chosen for an Event, stored as a
snapshot of the Game and Activity when the Event is saved. An Activity can
appear in an Event only once.

**Unique games only**:
An Event setting, on by default. When it is on, every Selection in the Event
uses a different Game.

**Winner**:
The Selection that won the Event, identified by its Activity because a Game can
appear in more than one Selection. An Event with exactly one Selection has that
Selection as its Winner.
_Avoid_: winning game

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
