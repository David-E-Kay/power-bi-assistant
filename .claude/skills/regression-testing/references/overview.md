<!--
  PARTIAL RECOVERY (2026-06-04): The original of this reference doc was lost to a
  OneDrive partial-sync. Its base version predates the surviving Claude Code session
  logs, so only the section below could be reconstructed (1 of 9 logged edits applied).
  The full document is not recoverable from logs. Regenerate from the regression-testing
  SKILL.md and docs/guides/regression-testing-developer-guide.md if the full doc is needed.
-->

A list of DAX boolean filter expressions that are applied to **every** measure evaluation. When populated, each measure reference is wrapped in:

```dax
CALCULATE(
    [Measure],
    KEEPFILTERS(<filter1>),
    KEEPFILTERS(<filter2>)
)
```

This wrapped reference is what `SUMMARIZECOLUMNS` calls per group, so `SUMMARIZECOLUMNS` still iterates over all grouping column values and the filters only affect the measure evaluation per row — identical to how a report-level slicer works. (Earlier template versions wrapped the measure reference in `IGNORE()`; v8+ removed that — `IGNORE()` is invalid inside `ROW()` and adds nothing inside `SUMMARIZECOLUMNS` once the measure is referenced directly.)