# Confluence Cache — One-Time Setup

This file is run-once. After it's done, the `confluence-cache` skill takes over and the user never has to think about plumbing again.

## What you need

- **Confluence Cloud access** under the Atlassian account whose OAuth scopes allow `read:page:confluence`, `read:space:confluence`, and `search:confluence`. (The official Atlassian MCP already requests these on its OAuth handshake.)
- **The Atlassian plugin enabled** in Claude Code (`plugin:atlassian:atlassian` in `enabledPlugins`, NOT in `disabledMcpjsonServers`).

That's the entire dependency list. No API token, no `.env`, no Python packages.

## Step 1 — Verify the Atlassian plugin is enabled

In a session, ask Claude:

> "List the available Atlassian MCP tools."

Expected: a list including `mcp__…__getConfluencePage`, `searchConfluenceUsingCql`, `getAccessibleAtlassianResources`, etc.

If the list is empty:

1. Open `~/.claude/settings.json` and `<project>/.claude/settings.json`.
2. Confirm `plugin:atlassian:atlassian` is in `enabledPlugins` and NOT in `disabledMcpjsonServers` (project file's disabled list takes precedence over global enable).
3. Restart Claude Code.

If the list is present but every call returns 401/403, the OAuth handshake hasn't run. Trigger it by asking any Atlassian question (e.g., "search Confluence for `foo`") — a browser window opens for sign-in. Approve the requested scopes. Subsequent calls run silently.

## Step 2 — Configure the manifest (the opt-in step)

The repo ships with `knowledge/confluence/_manifest.yaml` as an **empty template** — `site.cloud_id: null` and `pages: []`. Confluence caching stays OFF until you fill in the site block; this is the one-time opt-in. Open it and set:

- `site.cloud_id` to your Atlassian cloud UUID (or your `<your-org>.atlassian.net` URL — both work for MCP calls).
- `site.url` to your Confluence base URL.
- `site.primary_space.key` to the team space you most often cache from (optional).
- Leave `pages: []` empty — pages are added when you cache them (Step 3).

If you'd rather not configure the cloud_id by hand, ask Claude:

> "Get the accessible Atlassian resources and update the manifest cloud_id."

The skill will call `getAccessibleAtlassianResources`, pick the matching site, and write the cloud_id into `_manifest.yaml`. (The skill's pre-flight does this automatically on first run too.)

## Step 3 — Cache one page as a smoke test

```
"Cache the [pick a stable team-standards page] page."
```

Expected outcome:

- Skill announces it's running, resolves the page, fetches via MCP.
- A new file lands at `knowledge/confluence/<slug>.md` with frontmatter + body.
- `_manifest.yaml` gains one entry under `pages:`.
- A one-line confirmation in chat (file path + version).

Open the saved file and skim the body: headings, paragraphs, lists, code blocks, and tables should all be intact. If something looks wrong (mojibake, missing macros), check `references/troubleshooting.md` → "Conversion fidelity."

## Step 4 — Confirm routing reaches the cache

Start a fresh conversation. Ask a question whose answer is in the page you just cached but is NOT in any other `knowledge/` file. Confirm Claude's response cites `knowledge/confluence/<slug>.md` (via `Grep`/`Read`) rather than re-fetching via the live MCP.

If Claude re-fetches via MCP instead, the routing row in `knowledge/knowledge-index.md` isn't picking up the cache. Re-check that edit exists.

## Optional — Seed multiple pages at once

If you want to bulk-seed from day one (e.g., 10 known team-standards pages), hand-edit `_manifest.yaml` to add minimal `id`-only entries:

```yaml
pages:
  - id: "123456789"
  - id: "234567890"
  - id: "345678901"
```

Then ask Claude:

> "Refresh team standards from Confluence."

The skill runs Branch B (refresh-all), back-fills every entry with full metadata, and fetches the bodies. Faster than asking for each page individually.

## What you should NOT need to do

- Generate an Atlassian API token. (The MCP uses OAuth; no token to manage.)
- Install Python packages. (No script, no deps.)
- Edit `.env` or `.env.example`. (Neither file exists for this skill.)

If a future maintenance task adds a Python sync script (e.g., for CI), this file gets a Step 5. For now, it doesn't.
