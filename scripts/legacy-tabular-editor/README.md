# Legacy Tabular Editor 3 scripts

These are the **retired** Tabular Editor 3 C# (`.csx`) implementations of the
capture and benchmark workflows:

- `capture-snapshot.csx` — regression snapshot capture (predecessor of `scripts/capture_snapshot.py`).
- `benchmark-measures.csx` — measure timing sweep (predecessor of `scripts/benchmark_measures.py`).

**The TE-free Python runners in `scripts/` are the default and recommended path.**
They need no Tabular Editor install and write the same output schema, so the
`compare-snapshots.py` analysis step is identical either way.

These `.csx` files are kept only as an opt-in alternative for users who prefer to
run or step through the workflow inside the TE3 GUI (press **F5**). Claude emits
them on explicit request — raw or pre-populated with the session's parameters.
They are notification-free; the Python runners carry the desktop-toast on completion.
