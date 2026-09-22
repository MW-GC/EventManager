# Customized event create retries

`POST /api/events` accepts an optional `Idempotency-Key` header containing one non-empty GUID in `D` format. The key becomes the event ID; body IDs remain ignored. The UI generates a key for each create dialog and retains it across failed saves, including lost responses and timeouts. Updates still use PUT.

The API uses Azure Table Storage's atomic insert, not upsert or a read-before-write check. An existing row cannot be replaced by a retry. An identical normalized payload returns the existing event (200); a new insert returns 201. If the stored details differ, the API returns 409 with instructions to reload and edit the saved event. Invalid keys return 400. Requests without a key retain legacy server-generated IDs and are not retry-safe.

The event row itself is the deduplication record, so protection lasts while that row exists. This is not a durable request ledger: reopening the dialog or reloading the browser starts a new create, and replay after deletion can recreate the same ID. The generated-event endpoint is unchanged. Deploy the API support before the updated UI; an older API ignores the header.

Tests exercise the actual API functions against a mocked Azure TableClient and the existing page-save handler seam against a deterministic HttpMessageHandler. They simulate lost HTTP responses after commit, failures before and after storage commit, and concurrent inserts using a barrier rather than timing. These are not live Azure or browser end-to-end tests.
