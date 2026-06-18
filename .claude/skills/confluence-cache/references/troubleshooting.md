# Confluence Cache — Troubleshooting

Common failure modes, what they mean, and how to recover.

## Atlassian MCP returns 401

**Symptom:** Any `mcp__…__*` Atlassian tool returns `401 Unauthorized`.

**Cause:** OAuth token expired or never completed.

**Fix:** Trigger a fresh handshake by asking Claude any Atlassian question — a browser window opens for sign-in. Once approved, retry the skill operation. The token is cached for the session and refreshed automatically thereafter.

If the browser doesn't open or Atlassian says the app isn't authorized, check that the user has logged into the Atlassian MCP at least once on this machine. The plugin's `setup.md` covers the OAuth scopes the app requests.

## Atlassian MCP returns 403

**Symptom:** A specific page or space returns `403 Forbidden` while others succeed.

**Cause:** ACL denial. The OAuth user doesn't have read access to that page/space.

**Fix:** This is not a bug — it's the live ACL surface working as intended. The skill reports `denied` for the page and continues. Recovery options:

- Ask a Confluence space admin to grant the OAuth user read access.
- Skip caching that page (drop it from the manifest with Branch E).
- If the page was previously cached (older sync) and the user lost access, the file is still on disk. Don't auto-delete on 403 — the user may regain access. Flag it as stale and let them decide.

**Important:** Cache contents reflect what the OAuth-user could read at last sync. If the cache is committed to a shared repo and other team members have narrower Confluence permissions, they'll see content via the cache that they couldn't see via Confluence directly. Spot-check before committing the first batch.

## Atlassian MCP returns 404

**Symptom:** `getConfluencePage` returns `404 Not Found` for an ID that's in the manifest.

**Cause:** Page was moved, archived, or deleted upstream.

**Fix:** Skill reports `not-found` and asks once, at the end of the run, whether to remove the entry (Branch E). Don't auto-remove — the user might want to investigate (was it a typo? was it actually deleted? was it moved to another space?).

If the user wants to recover the page at its new location:

1. Search for it: `"Search Confluence for [title]."` — uses `searchConfluenceUsingCql`.
2. If found, run Branch A on the new URL/ID.
3. Run Branch E on the old entry to clean up.

## A just-cached page doesn't show up in `Grep`/`Read`

**Symptom:** Skill reported success and the manifest is updated, but a `Grep` for a phrase from the page returns nothing.

**Possible causes (in likelihood order):**

1. **File wasn't actually written.** Confirm `knowledge/confluence/<slug>.md` exists and has a body, not just frontmatter. If it's missing, re-run Branch A — the write step (A.4) didn't complete.

2. **Search phrase is too generic or too specific.** A single common word (`"modeling"`) hits many files; an exact multi-word phrase may differ from the page's wording. Try a distinctive multi-word noun phrase that appears verbatim in the body.

3. **Encoding issue in the body.** If the file looks fine but `Grep` still misses content, `Read` it and look for BOM or NUL bytes. The MCP returns clean UTF-8 markdown by default; stray bytes would indicate a downstream write issue.

If none of the above resolve it, escalate to the user with the file path and the failing `Grep` query.

## Conversion fidelity (rare but real)

**Symptom:** A cached page renders differently from the Confluence-rendered version. Common losses:

- Confluence-specific macros (status pills, info panels, expand blocks, table-of-contents macros) render as plain text or disappear.
- Multi-column layouts collapse into a single column.
- Embedded Jira issue lists become plain links.
- Inline comments are dropped (markdown export doesn't carry them; HTML format would).

**Cause:** `getConfluencePage` with `contentFormat: "markdown"` returns simplified markdown. Macros and complex layouts don't have markdown equivalents.

**Fix:** For prose-heavy standards docs, the markdown is fine. For pages where the macros carry meaning (e.g., a status dashboard), three options:

1. **Live-only** — drop the page from the cache; route to the live MCP for those queries.
2. **HTML format** — re-fetch with `contentFormat: "html"`, then convert client-side. The MCP supports `"html"` as a format (round-trip safe, preserves more structure). The skill currently uses `"markdown"` because most Power BI standards pages don't need the extra fidelity. If the user wants to switch, they can ask for a one-off "fetch as HTML" or update the skill's Branch A.3.
3. **ADF format** — `contentFormat: "adf"` returns Atlassian Document Format JSON. Most fidelity, but requires a converter to make it readable. Not currently supported by the skill.

If a particular page is consistently lossy, note it in the manifest entry as a comment and route those queries to live MCP:

```yaml
pages:
  - id: "999"
    slug: "release-status-dashboard"
    # Note: status macros don't survive markdown conversion. Use live MCP for queries about current statuses.
    ...
```

## Manifest is corrupt (YAML parse error)

**Symptom:** Skill pre-flight fails with a YAML error.

**Fix:**

1. Open `knowledge/confluence/_manifest.yaml` in an editor with YAML syntax checking.
2. Common breakage: a page title with unescaped special characters in `title:` (colons, quotes). Wrap the value in single or double quotes.
3. If the file is unrecoverable, restore from git history (or, if the repo isn't gitted, regenerate from the file list):
   - Read each file in `knowledge/confluence/`, parse its frontmatter, append a manifest entry.
   - This is a recovery branch — not normally needed.

## Orphan files

**Symptom:** A `.md` file in `knowledge/confluence/` has no manifest entry (or vice versa).

**Cause:** Hand-edits, partial skill failures, manual file moves.

**Fix:** Run the orphan-check (see `references/manifest-format.md` "Validating the manifest"). The skill identifies mismatches and asks how to resolve each one. Don't auto-fix — the file might represent intentional in-progress work the user did manually.

## "I don't see the confluence-cache skill being invoked"

**Symptom:** User asks "cache this page" but Claude improvises with raw MCP calls instead of routing through the skill.

**Cause:** The skill's `description:` triggers may not match the user's phrasing, or the skill isn't visible to Claude in this session.

**Fix:**

1. Confirm `.claude/skills/confluence-cache/SKILL.md` exists with frontmatter `name: confluence-cache`.
2. Restart Claude Code (skills are picked up at session start in some environments).
3. Try a more explicit trigger: `"Use the confluence-cache skill to grab [page]."`
4. If still not invoked, the skill description likely needs to be expanded to cover the user's phrasing. Edit the `description:` frontmatter in `SKILL.md` and add the missing trigger phrase.

## Resetting the cache

If the cache is in an unrecoverable state:

1. Delete every `.md` file in `knowledge/confluence/` (NOT `_manifest.yaml`).
2. Reset `_manifest.yaml` `pages:` to `[]`.
3. Re-cache pages one at a time (Branch A) or in bulk (hand-seed manifest + Branch B).

This is a destructive operation — confirm with the user before doing it. The reset throws away whatever curation history was in the cache.
