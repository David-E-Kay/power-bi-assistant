# Session Handoff — M&C Regression Testing

**Date:** 2026-04-22
**Status:** Fix applied, UNVERIFIED. User needs to run the script in TE3 next.

---

## Outstanding action (start here)

The user must run `output/mc-baseline-smoketest.csx` in TE3 against the M&C model with a **reduced `testLines` list** (~5 cases — temporarily comment out the rest at the top of the list around line ~5 of the `testLines` block) to verify the Strategy 1 fix compiles and connects.

Watch for one of three outcomes:

| Outcome | Meaning | Next step |
|---|---|---|
| Compiles, connection opens, 5 cases run | Strategy 1 works | Restore full 945-case `testLines`, kick off the real run |
| Compile error on the cast line (e.g. `cannot convert type` / `'Database' does not contain 'MetadataObject'`) | TOMWrapper doesn't expose `MetadataObject` publicly | Apply **Strategy 2** (env-var `CONNECTION_STRING`) — see `C:\Users\dkay\.claude\plans\update-the-regression-testing-quizzical-coral.md` |
| Compiles but runtime error on `.ConnectionString` | Cast worked but raw TOM Server unreachable in local PBIP mode | Same fallback — apply Strategy 2 |

**Report the exact error message** — the wording tells which strategy to try next.

---

## What was done this session

1. **CLAUDE.md Regression Testing section rewritten** — `CLAUDE.md` lines 202–229. Now explicitly states the template is read-only and documents the copy-to-output + helper-script pattern (`scripts/gen-mc-testlines.py` / `scripts/apply-mc-testlines.py`). Also flags `skills/skill-regression-testing.md` for the same update.

2. **Diagnosed the `useDirectAdomd=true` bug** — template line 231 (`Model.Database.Server.ConnectionString`) fails in TE3. `Model.Database` is a `TabularEditor.TOMWrapper.Database` wrapper that exposes `.Name` and `.CompatibilityLevel` but NOT `.Server`. Zero plugin-cache evidence for any wrapper escape hatch — `MetadataObject` is a best-effort guess, unverified.

3. **Applied Strategy 1 fix** to `output/mc-baseline-smoketest.csx` around line 231:
   ```csharp
   var tomDb = (Microsoft.AnalysisServices.Tabular.Database)Model.Database.MetadataObject;
   var connStr = tomDb.Server.ConnectionString;
   ```
   Template `scripts/capture-snapshot.csx` was NOT modified (it's read-only per CLAUDE.md).

---

## Key files

- **`output/mc-baseline-smoketest.csx`** — session copy with Strategy 1 applied. This is what the user runs in TE3. **Needs verification.**
- **`output/mc-testlines-blocks.txt`** — generated testLines + groupByColumns (945 cases, 105 measures × 9 contexts). Already injected into the .csx.
- **`scripts/capture-snapshot.csx`** — READ-ONLY template. Still has the same bug at line 231. If Strategy 1 verifies, propose a follow-up to propagate the fix to the template. Do not edit in the verification run.
- **`C:\Users\dkay\.claude\plans\update-the-regression-testing-quizzical-coral.md`** — full plan with Strategy 1, Strategy 2 (env-var fallback), and Layer 4/5 safety-net recommendations.
- **`CLAUDE.md`** — Regression Testing section (lines 202–229) — updated this session.

---

## Next steps (in order)

1. **User runs 5-case verification** in TE3 → report outcome.
2. If Strategy 1 works → restore full `testLines`, full 945-case run.
3. If Strategy 1 fails → apply Strategy 2 from the plan file.
4. If both fail → fall back to `useDirectAdomd=false` (EvaluateDax, no timeout) AND implement Layer 4 batching before a full run. That becomes a new plan.
5. **After successful full run**: propagate the working fix (Strategy 1 or 2) to the template `scripts/capture-snapshot.csx` as a dedicated change.
6. **After confirmed solution**: update KB:
   - `knowledge/pbi-model-gotchas.md` — TE3 TOMWrapper gotcha (`Model.Database` doesn't expose `.Server`; use `.MetadataObject` cast [if confirmed] or env-var connection string).
   - `skills/skill-regression-testing.md` — add the same copy-to-output warning the CLAUDE.md update called out.

**Do NOT update the KB** until a strategy is confirmed working end-to-end.

---

## Open questions / risks

- **`.MetadataObject` existence unverified.** It's the conventional TOMWrapper escape-hatch name but I couldn't find a single example in the plugin cache. If Strategy 1 fails, Strategy 2 (env-var) is the guaranteed path.
- **If user falls back to `EvaluateDax()` (no timeout)**: hung queries cannot be cancelled gracefully. Manual kill requires Task Manager → end TE3 → end `msmdsrv.exe` → reopen PBIP. Messy but non-destructive (partial JSON results survive).
- **Memory watchdog (`IsMemoryCritical`) only fires between queries**, not during one. A single runaway query can still consume memory without the watchdog firing until it finishes.
- **16 GB hardcoded denominator** in `IsMemoryCritical` (around template line 285). If user's machine has more/less RAM, `MEMORY_THRESHOLD_PCT` needs proportional adjustment.
