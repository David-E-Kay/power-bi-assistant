---
name: confluence-cache
description: "Use this skill when the user asks to cache, sync, refresh, grab, pull, add, or update a Confluence page (or 'team standards from Confluence', 'Atlassian page to KB', 'add Confluence page to knowledge'); also when asking 'what Confluence pages do we have cached', 'show cached standards', 'is the [page] cache stale', or to remove a cached page. Encapsulates the full grab-and-curate lifecycle: resolve a page (URL/ID/title), fetch via the official Atlassian MCP in markdown format, write to knowledge/confluence/<slug>.md with version frontmatter, update knowledge/confluence/_manifest.yaml, and re-run ctx_index so the page is searchable. Does NOT trigger for ad-hoc one-off Confluence questions — those route to the live Atlassian MCP directly without going through the cache. Does NOT cache Jira tickets, comments, attachments, or whiteboards."
---

# Confluence Cache Workflow

A procedural skill that owns the local cache of Confluence pages used to extend this project's KB with team standards. The cache parallels `artifacts/model-schema/` — generated, committed, and indexed via Context Mode — but lives under `knowledge/` because cached standards are first-class KB content, not raw model dumps.

| Surface | Where it lives |
|---|---|
| Cached pages | `knowledge/confluence/<slug>.md` (one page per file, frontmatter + body) |
| Manifest (source of truth) | `knowledge/confluence/_manifest.yaml` |
| Routing entry | Row in `knowledge/knowledge-index.md` and a bullet in `.claude/skills/powerbi-context-mode/SKILL.md` |
| Live access (uncached pages) | Official Atlassian MCP — used directly, not via this skill |

## When to Use This Skill

Trigger when the user asks to:

- Cache, sync, refresh, grab, pull, add, or update a Confluence page (or the team's standards pages).
- List what's cached, check freshness, or find stale cached pages.
- Remove a cached page from the KB.
- Bulk-refresh everything in the manifest.

Do NOT use for:

- One-off questions about a Confluence page that doesn't need to be cached. Use the Atlassian MCP directly (`mcp__ac3b8583-…__getConfluencePage` / `searchConfluenceUsingCql`) and answer from the live response.
- Jira tickets, Confluence comments, attachments, or whiteboards.
- Anything outside Confluence (SharePoint, Notion, Google Drive — those would each need their own skill).

If you're unsure whether a page should be cached, ask the user: "Want me to cache this page so future sessions can find it via `ctx_search`, or just answer from the live MCP?"

## Core Principles

1. **MCP for fetch, not REST.** Use the official Atlassian MCP with `contentFormat: "markdown"` so the page comes back ready to write — no ADF/HTML conversion. OAuth is the only auth surface; no API token, no `.env`.
2. **Manifest is the source of truth.** `_manifest.yaml` is the contract between the skill, the cache files, and humans editing by hand. Every cached page has exactly one manifest entry. Files without entries are orphans (the skill flags them).
3. **Frontmatter records freshness.** Every cached `.md` carries `confluence_id`, `last_modified` (date string from the MCP), and `last_synced` (UTC timestamp of the cache write). The `last_modified` value drives re-fetch decisions: a re-run skips pages whose remote `lastModified` matches the cached `last_modified`. (The MCP's `getConfluencePage` returns `lastModified` as a human date string like `"Apr 10, 2026"`, not a numeric version — so we compare strings, not integers. If a page is edited twice on the same day, the `last_modified` won't change; the skill will treat it as `unchanged`. That's a known and accepted limitation; same-day double edits are rare for stable standards docs.)
4. **Re-index after every change.** Adding, refreshing, or removing a page MUST be followed by `ctx_index(path: "knowledge/confluence/")`. Skipping the re-index silently breaks `ctx_search`. Treat the index as part of the write.
5. **Concise reports, not body dumps.** Skill output to chat is one line per page (`updated 12 → 47`, `unchanged`, `denied: 403`, `not-found`). Page content goes to disk; the assistant summarizes if needed.

---

## Pre-Flight (run once per skill invocation)

Before any branch executes, verify:

1. `knowledge/confluence/_manifest.yaml` exists. If missing → tell user to run setup (`references/setup.md`) and stop.
2. `_manifest.yaml` has a `site.cloud_id`. If missing → call `getAccessibleAtlassianResources`, write the cloud_id back into the manifest, and continue.
3. The Atlassian MCP tools are loaded. If a probe (`getAccessibleAtlassianResources`) fails with no tools → tell user the Atlassian plugin is disabled, point at `references/setup.md`, stop.

If all three pass, proceed to the appropriate branch.

---

## Branch A — Grab a single page

User says: "cache the [page]", "add [URL] to KB", "pull this page into knowledge", or pastes a Confluence URL/ID.

### A.1 Resolve the page reference

Accept any of:

- **Full URL** (`https://<your-org>.atlassian.net/wiki/spaces/TEAM/pages/123456789/Page+Title`) → extract page id from the `/pages/{id}/` segment.
- **Tiny URL** (`/wiki/x/Fc1bBw`) → pass the encoded segment to `getConfluencePage` as `pageId`; the MCP accepts tiny IDs.
- **Numeric page ID** alone → use as-is.
- **Title only** (e.g., "Power BI Modeling Standards") → resolve via `searchConfluenceUsingCql`:

  ```
  cql: title = "<exact title>" AND type = page
  ```

  If multiple matches, list them (title + space + URL) and ask the user to pick. If zero matches, switch to fuzzy: `title ~ "<title>"` and present the top results.

### A.2 Check the manifest

Read `_manifest.yaml`. If the page id is already present, this is a refresh, not a new add — proceed to A.3 but compare versions before fetching the body (cheap manifest check vs. tokens of body re-pull).

For new pages, generate the slug:

- Lowercase the title, replace non-alphanumeric runs with `-`, strip leading/trailing dashes, truncate to 60 chars.
- If the slug collides with an existing file in `knowledge/confluence/`, append `-<id>`.

See `references/manifest-format.md` for the slug rules and edge cases.

### A.3 Fetch the page

```
getConfluencePage(
  cloudId: <from manifest>,
  pageId: <id>,
  contentFormat: "markdown"
)
```

The MCP returns title, `lastModified` (human date string, e.g. `"Apr 10, 2026"`), space key, body in markdown, author info, and `webUrl`. (No numeric `version` field is returned in the markdown response — that's why the skill uses `last_modified` as the freshness key.) If the call fails:

- 401 → OAuth lapsed. Tell user to re-trigger by asking any Atlassian question; stop the skill.
- 403 → ACL denial. Note that the cache reflects the OAuth-user's permissions. Report and skip.
- 404 → Page moved or deleted. If the page is in the manifest, ask user whether to remove the entry (Branch E).

For refresh requests, compare the returned `lastModified` to the manifest's recorded `last_modified`. If equal → report `unchanged` without writing the body to disk.

### A.4 Write the cache file

Write to `knowledge/confluence/<slug>.md` with this exact shape:

```markdown
---
confluence_id: "<id>"
space_key: "<space-key>"
page_title: "<title>"
page_url: "<webUrl from MCP>"
last_modified: "<lastModified from MCP, e.g. 'Apr 10, 2026'>"
last_synced: "<ISO-8601 UTC timestamp of this write>"
author: "<author.displayName from MCP, optional>"
labels: [<list, optional — usually empty in markdown response>]
---

# <title>

<markdown body returned by getConfluencePage>
```

Use Write (not Edit) on first add; Edit (or Write to overwrite) on refresh.

### A.5 Update the manifest

Add or replace the entry under `pages:` with: `id`, `space_key`, `slug`, `title`, `url`, `last_modified`, `last_synced`. Preserve the order of other entries; sort alphabetically by `slug` for consistency on first add.

### A.6 Re-index

```
ctx_index(path: "knowledge/confluence/")
```

This is non-optional. Without it, `ctx_search` won't find the new page.

### A.7 Smoke test

Run a single `ctx_search` with a distinctive phrase from the page (a heading or a multi-word noun). Confirm the new file appears in results. If the search misses it, the index didn't pick the file up — re-run `ctx_index` and try once more before reporting failure.

### A.8 Report

One line per page, plus the saved file path:

```
✓ cached "Power BI Modeling Standards" (last modified Apr 10, 2026) → knowledge/confluence/power-bi-modeling-standards.md
```

Do NOT paste the page body into chat.

---

## Branch B — Refresh all cached pages

User says: "refresh team standards", "sync Confluence", "update all cached pages".

For each entry in `_manifest.yaml` `pages:`:

1. Call `getConfluencePage` (markdown format).
2. If returned `lastModified` equals manifest `last_modified` → report `unchanged`, skip write.
3. Otherwise → write the file (A.4), update the manifest (A.5).
4. Track the count of `updated`, `unchanged`, `denied`, `not-found`.

After the loop:

5. `ctx_index(path: "knowledge/confluence/")` once (not per-page).
6. Report a tally: `Refresh complete: 3 updated, 7 unchanged, 0 denied, 0 not-found.`

If any page returned `not-found`, ask the user once at the end whether to run Branch E (remove) on those entries — don't auto-remove.

---

## Branch C — List what's cached

User says: "what Confluence pages do we have cached", "show cached standards", "list KB pages from Confluence".

1. Read `_manifest.yaml`.
2. For each page, compute `staleness = today - last_synced`.
3. Print as a table:

   ```
   | Title                          | Space | Version | Synced       |
   |--------------------------------|-------|---------|--------------|
   | Power BI Modeling Standards    | DATA  | v47     | 3 days ago   |
   | DAX Naming Conventions         | DATA  | v12     | 12 days ago  |
   ```

4. No network calls. No re-index.

---

## Branch D — Find stale pages

User says: "any stale Confluence pages", "what's outdated in the cache", "check freshness".

1. Read `_manifest.yaml`.
2. Filter to entries where `last_synced` > 14 days ago.
3. Report the list. Ask whether to refresh (Branch B for all-stale, or Branch A for individual). Do NOT auto-refresh.

If everything is fresh: `All N cached pages are < 14 days old.`

---

## Branch E — Remove a page

User says: "remove [page] from the cache", "drop [title] from KB", "stop tracking [URL]".

1. Resolve the page (same as A.1).
2. Confirm with the user: `About to delete knowledge/confluence/<slug>.md and remove its manifest entry. Proceed?`
3. On approval:
   - Delete the `.md` file.
   - Edit `_manifest.yaml` to drop the `pages:` entry.
   - `ctx_index(path: "knowledge/confluence/")` to update the index.
4. Report: `✗ removed "<title>" from cache.`

Never delete without explicit confirmation, even if the user's phrasing sounded definitive ("just nuke that page from KB"). The cost of a wrong delete is asking them to re-curate.

---

## Slug Generation

Detailed rules in `references/manifest-format.md`. Quick summary:

- `"Power BI Modeling Standards"` → `power-bi-modeling-standards`
- `"DAX: Naming & Conventions (v2)"` → `dax-naming-conventions-v2`
- Truncate at 60 chars at a word boundary if possible.
- On collision in `knowledge/confluence/`, append `-<id>` (last 6 digits is enough).

## Setup Prerequisite (one-time)

`references/setup.md` walks through enabling the Atlassian plugin and bootstrapping `_manifest.yaml`. The skill aborts pre-flight if those aren't done. Run setup once per user, not once per project.

## Troubleshooting

`references/troubleshooting.md` covers the common failure modes: 401/403 from MCP, page-not-found on a tracked entry, `ctx_search` not finding a just-cached page, conversion fidelity issues (rare, since markdown comes from the MCP directly), and how to recover from a corrupt manifest.

---

## What This Skill Does NOT Do

- Edit `knowledge-index.md`, `CLAUDE.md`, or `powerbi-context-mode/SKILL.md`. Those rows exist from the one-time bootstrap; the skill assumes they're in place.
- Bidirectional sync (writing pages back to Confluence). Read-only.
- Caching attachments, images, comments, whiteboards, or Jira tickets.
- Cache pages the OAuth user can't read. ACL denials are reported and skipped, not bypassed.

If a future need arises (e.g., caching attachments alongside pages), extend this skill — don't fork a parallel one. The pattern is "one source folder under `knowledge/`, one manifest, one skill."
