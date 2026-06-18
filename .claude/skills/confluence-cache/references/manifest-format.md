# `_manifest.yaml` — Schema and Slug Rules

The manifest is the source of truth for the Confluence cache. Every cached `.md` file in `knowledge/confluence/` MUST have a matching entry here. Files without entries are orphans.

## Top-level keys

```yaml
site:
  cloud_id: <UUID>           # Atlassian cloud ID; from getAccessibleAtlassianResources
  url: <https://...>         # Base URL of the Confluence site
  primary_space:             # Optional default for "list pages in our space" queries
    key: <space key>
    id: "<space id, quoted>"
    name: <human name>

pages: []                    # List of cached page entries (see below)
```

## Per-page entry

```yaml
pages:
  - id: "123456789"                                       # Confluence page ID, quoted (string, not int — IDs can exceed JS safe-int)
    space_key: "TEAM"
    slug: "power-bi-modeling-standards"                   # Filename without .md extension
    title: "Power BI Modeling Standards"                  # Human title at last sync
    url: "https://<your-org>.atlassian.net/wiki/spaces/TEAM/pages/123456789/Power+BI+Modeling+Standards"
    last_modified: "Apr 10, 2026"                         # lastModified date string from getConfluencePage; drives re-fetch decisions
    last_synced: "2026-05-08T10:32:00Z"                   # ISO-8601 UTC of the cache write, quoted
```

The same fields appear in the `.md` frontmatter (Source-of-truth: the manifest is canonical for *what's tracked*; the frontmatter is canonical for *what was last written to disk*). They should agree after every successful skill run.

### Why `last_modified` and not `version`?

The official Atlassian MCP's `getConfluencePage` response (in `markdown` content format) includes `lastModified` as a human date string (e.g. `"Apr 10, 2026"`) but does NOT include a numeric `version` field. The Confluence v2 REST API does expose `version.number` directly, but the MCP abstracts it away. We use what the MCP gives us. Trade-off: a page edited twice in one day will report `unchanged` on the second sync. For stable standards docs, this is fine — same-day double edits are rare.

If a future MCP release exposes `version`, the skill can be extended to record both fields and prefer `version` when present.

### Required vs optional

| Field | Required | Notes |
|---|---|---|
| `id` | Yes | The only field a hand-edit must include. The skill back-fills the rest. |
| `space_key` | After first sync | Skill writes it once it's known. |
| `slug` | After first sync | Generated from title; see rules below. |
| `title` | After first sync | The Confluence-side title at the time of last sync. May drift if the page was renamed. |
| `url` | After first sync | Web URL — useful for humans clicking through. |
| `last_modified` | After first sync | The Confluence `lastModified` date string. Drives re-fetch decisions. |
| `last_synced` | After first sync | ISO-8601 UTC of when the cache file was written. Drives staleness checks. |

### Hand-editing for bulk seed

The minimal valid `pages:` entry is just `{id: "<id>"}`. The skill's refresh-all branch (B) detects entries without `last_modified` or `last_synced` and treats them as fresh adds — fetches the body, generates the slug, writes the file, fills in the rest of the entry.

```yaml
pages:
  - id: "123456789"
  - id: "234567890"
  - id: "345678901"
```

## Slug generation rules

The slug becomes the filename: `knowledge/confluence/<slug>.md`. Rules in order:

1. **Lowercase the title.**
2. **Replace any run of non-alphanumeric characters with a single `-`.**
   - `"DAX: Naming & Conventions (v2)"` → `dax-naming-conventions-v2`
3. **Strip leading/trailing dashes.**
4. **Truncate to 60 characters at a word boundary.**
   - `"Tabular Editor Scripting Patterns and Conventions for Semantic Models"` (69 chars) → `tabular-editor-scripting-patterns-and-conventions-for` (53 chars, cut at `-for`)
5. **On collision** with an existing slug in the cache: append `-<last-6-of-id>`.
   - `"Modeling Standards"` (id `123456789`) → `modeling-standards`
   - Second `"Modeling Standards"` (id `987654321`) → `modeling-standards-654321`
6. **Special characters always strip.** Em-dashes, smart quotes, emoji, parens, slashes — all become `-` (then collapse).
7. **Empty slug fallback** (e.g., title was all special chars): use `confluence-<id>`.

### Renamed pages

If a page's title changes upstream, the slug stays put — `slug` in the manifest is the authoritative filename. The `title` field in both manifest and frontmatter is updated to the new title on next sync, but the file is not renamed. This avoids breaking any external bookmarks to the file.

If the user explicitly wants to rename the file to match the new title, they can do it manually:

1. Rename `knowledge/confluence/<old>.md` → `<new>.md`.
2. Update `slug:` in both the manifest entry and the file frontmatter.

## Reserved slugs

Don't generate these — the skill must rename to a `-confluence-<id>` variant if the title would collide:

- `_manifest` (the manifest file lives here)
- `index`, `readme`, `_archive` (reserved for KB conventions)

## Validating the manifest

Quick sanity check the user can run any time:

> "Are there any orphans in the Confluence cache?"

The skill compares the file list in `knowledge/confluence/` (excluding `_manifest.yaml`) against the `pages:` list. Mismatches:

- File without manifest entry → flag, ask whether to back-fill the manifest or delete the file.
- Manifest entry without file → flag, ask whether to fetch the page or drop the entry.

This is a maintenance branch, not a default workflow — don't run it on every skill invocation.
