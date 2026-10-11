# Records about Events and Activities live in their own tables

Comments (many per Activity) and Event metrics (one set per Event) are stored as their own rows in their own tables, `ActivityComments` and `EventMetrics`, each row with its own eTag, and are read and written through their own routes. They are not properties of the Event or Activity row. We chose this because an Activity can win many Events, so a list on its row would grow without bound and make every Comment edit conflict with every Activity edit (409), and because an Event save that rewrites its Selections must not be able to overwrite Event metrics. Deleting an Event or Activity deletes its rows in these tables.

Activity performance is computed when read from the Events, Event metrics and Likes, behind one read-only route, and is not stored. Table Storage has no transactions across tables, so a stored copy would drift from the Events it summarises. If reads get slow, a stored copy can be added behind the same route without changing the pages.

## Considered Options

- A list of Comments and the metric fields on the Activity and Event rows, saved with their `PUT`: rejected for the reasons above.
- A stored Activity performance table rewritten on every Event save, delete and metrics edit: rejected for drift; kept as the escape hatch.

## Consequences

- A Comment or metrics write can succeed while a related Event or Activity write fails; each route is its own atomic unit.
- Event snapshots stop copying an Activity's Comments.
