"""TE-free Power BI capture/benchmark engine.

Stable runner package — the per-session artifact is a JSON config file
(see docs/config-schema.md), never an edit to this package. Import order
matters for CLR modules: anything touching ADOMD must call
pbi_capture.clr_boot.ensure_adomd() first.
"""
