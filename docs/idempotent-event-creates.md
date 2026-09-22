# Customized event create retries

`POST /api/events` accepts an optional `Idempotency-Key` header: one non-empty GUID in hyphenated D format. The key becomes the event ID; the body ID is ignored. Deploy the API before the web client that relies on this header. An older API ignores the header and cannot prevent duplicate retries.

The customize dialog generates a key for each intentionally new create and keeps it across failed/ambiguous saves, including edits made before retrying. Updates remain `PUT /api/events/{id}` without a create key.

The API validates selections, normalizes the winner, then uses Azure Table Storage's atomic AddEntity operation, not a read-before-write check or upsert. A duplicate key returns the existing event with 200 when its normalized domain details match; the first insert returns 201. Different details return 409 with instructions to reload the list and edit the existing event. Retrying never overwrites a later update. Domain comparison includes name, date, selection snapshots, unique-game setting and winner, excluding storage metadata. Invalid headers return 400. Clients omitting the header retain legacy fresh-ID create behavior and do not gain deduplication.

## Retention and recovery boundaries

- Deduplication is backed by the event row, not a permanent request ledger. Deleting the row ends retention; a later retry can recreate it.
- The key lives in dialog memory. Reloading the browser, navigating away or opening a new create dialog does not resume the old operation. After an ambiguous save, retry in the same dialog or check the event list before intentionally starting another create.
- Changed retry details are not silently discarded or applied as an update. A 409 keeps the dialog and its edits visible; inspect the saved event and use its edit action.
- The generate endpoint and other entities are outside this change.

## Regression coverage

`IdempotentCreateTests` connects page handlers through the actual EventService and Functions handlers to TableStore with a mocked TableClient. It covers commit-then-lost-response, timeout, failure before commit, repeated retries, changed details, subsequent updates, independent creates, invalid keys, legacy clients, metadata and barrier-synchronized concurrent inserts. Public API reads verify persisted outcomes. This is not live Azure or browser-renderer coverage.
