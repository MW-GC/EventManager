# Issue tracker: GitHub

Issues and specs live in MW-GC/EventManager GitHub Issues. Infer the repository from `git remote -v`.
Use `gh issue view <number> --comments`, `gh issue list`, `gh issue create`, `gh issue comment`, and `gh issue edit` for issue operations. If `gh` is unavailable, use the corresponding GitHub REST API endpoints; never log credentials.
Publishing to the issue tracker means creating a GitHub issue; fetching a ticket includes its body, labels, and comments.

## Pull requests as a triage surface

**PRs as a request surface: no.**

Target implementation PRs at `dev`. Verify the remote PR base and head SHA after creation. Do not merge or deploy without authorization.

## Wayfinding

Use a `wayfinder:map` issue with linked child issues (`wayfinder:<type>`). Prefer GitHub native sub-issues and issue dependencies; fall back to parent task lists, `Part of #<map>`, and `Blocked by: #<n>` when unsupported. Claim eligible, unassigned, unblocked tickets before working; publish resolution evidence back to the tracker.
