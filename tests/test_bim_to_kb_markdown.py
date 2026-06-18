"""Unit tests for scripts/bim_to_kb_markdown.py.

Fixture-driven characterization tests that pin the parser's branches and the
emitted schema-markdown so future edits can't silently change output. These
tests describe what the parser *currently does*, not necessarily what it
ideally should — see ``test_infer_d_underscore_prefix_is_dead_branch`` for a
known quirk that is deliberately pinned.

The module is importable directly: the repo-root conftest puts ``scripts/`` on
``sys.path``.
"""

import json

import pytest

import bim_to_kb_markdown as bkm


# ── Helpers ──────────────────────────────────────────────────────────────────

def write_bim(tmp_path, model_dict, *, bom=False):
    """Dump a dict to <tmp>/model.bim as JSON and return the Path.

    ``bom=True`` writes with a UTF-8 BOM (utf-8-sig) to exercise the parser's
    BOM-tolerant read.
    """
    path = tmp_path / "model.bim"
    encoding = "utf-8-sig" if bom else "utf-8"
    path.write_text(json.dumps(model_dict), encoding=encoding)
    return path


def render(tmp_path, model, *, model_name=None, model_id=None):
    """Wrap a model body in {"model": ...}, write it, return the markdown."""
    path = write_bim(tmp_path, {"model": model})
    return bkm.parse_bim_to_markdown(path, model_id=model_id, model_name=model_name)


def line_with(md, needle):
    """Return the single rendered line containing ``needle`` (or fail)."""
    for ln in md.splitlines():
        if needle in ln:
            return ln
    raise AssertionError(f"no line containing {needle!r}")


# ── Group A — normalize_expression (pure) ────────────────────────────────────

@pytest.mark.parametrize("expr,expected", [
    ("SUM('T'[c])", "SUM('T'[c])"),   # plain string unchanged
    (["a", "b"], "a\nb"),             # list joined with newlines
    (None, ""),                        # None -> empty
    ("", ""),                          # empty -> empty
])
def test_normalize_expression(expr, expected):
    assert bkm.normalize_expression(expr) == expected


# ── Group B — infer_table_type (pure; branches in priority order) ────────────

def test_infer_calculation_group_is_calc():
    tbl = {"calculationGroup": {"calculationItems": [{"name": "YTD"}]}}
    assert bkm.infer_table_type("Time Intelligence", tbl, []) == "calc"


@pytest.mark.parametrize("partition", [
    {"mode": "calculated"},
    {"source": {"type": "calculated"}},
])
def test_infer_all_calculated_partitions_is_calc(partition):
    assert bkm.infer_table_type("Snapshot", {"partitions": [partition]}, []) == "calc"


def test_infer_mixed_partitions_not_calc():
    # Not ALL partitions calculated -> falls through to the heuristic (default dim).
    tbl = {"partitions": [{"mode": "calculated"}, {"mode": "import"}]}
    assert bkm.infer_table_type("Snapshot", tbl, []) == "dim"


@pytest.mark.parametrize("name,expected", [
    ("Fact Sales", "fact"),      # space-insensitive
    ("FactSales", "fact"),
    ("fct_orders", "fact"),      # underscore-insensitive
    ("FCT_Orders", "fact"),      # case-insensitive
    ("Bridge Map", "bridge"),
    ("brg_account", "bridge"),
    ("BRG Link", "bridge"),
])
def test_infer_name_prefix_overrides_default(name, expected):
    # All of these resolve to a non-dim type, so an isolated table (whose
    # heuristic default is "dim") proves the name branch fired.
    assert bkm.infer_table_type(name, {}, []) == expected


def test_infer_dim_prefix_wins_over_relationship_heuristic():
    # "DimDate" is on the many-side here; without the name check the heuristic
    # would call it a fact. The name prefix must win.
    rels = [{"fromTable": "DimDate", "toTable": "Sales"}]
    assert bkm.infer_table_type("DimDate", {}, rels) == "dim"


def test_infer_d_underscore_prefix_is_dead_branch():
    # KNOWN QUIRK: name normalization does .replace("_", "") *before* the
    # startswith("d_") test, so 'd_customer' -> 'dcustomer' and the d_ prefix
    # can never match. Classification falls through to the relationship
    # heuristic instead — here, many-side-only yields "fact", not "dim".
    # Pinned intentionally so a future fix to the prefix logic trips this test.
    many_only = [{"fromTable": "d_customer", "toTable": "Sales"}]
    assert bkm.infer_table_type("d_customer", {}, many_only) == "fact"


def test_infer_heuristic_many_side_only_is_fact():
    rels = [{"fromTable": "Orders", "toTable": "Calendar"}]
    assert bkm.infer_table_type("Orders", {}, rels) == "fact"


def test_infer_heuristic_one_side_only_is_dim():
    rels = [{"fromTable": "Orders", "toTable": "Calendar"}]
    assert bkm.infer_table_type("Calendar", {}, rels) == "dim"


def test_infer_heuristic_many_and_one_is_bridge():
    rels = [
        {"fromTable": "Map", "toTable": "A"},
        {"fromTable": "Map", "toTable": "B"},
        {"fromTable": "C", "toTable": "Map"},
        {"fromTable": "D", "toTable": "Map"},
    ]
    assert bkm.infer_table_type("Map", {}, rels) == "bridge"


def test_infer_isolated_table_defaults_to_dim():
    assert bkm.infer_table_type("Orphan", {}, []) == "dim"


def test_infer_ambiguous_single_each_side_defaults_to_dim():
    # one many-side and one one-side, neither > 1 -> no branch fires -> dim.
    rels = [
        {"fromTable": "Mid", "toTable": "Top"},
        {"fromTable": "Leaf", "toTable": "Mid"},
    ]
    assert bkm.infer_table_type("Mid", {}, rels) == "dim"


# ── Group C — is_internal_table (pure) ───────────────────────────────────────

@pytest.mark.parametrize("name", [
    "LocalDateTable_8f3a2b",
    "DateTableTemplate_abc",
    "RowNumber-2662979B",
    "$Hidden",
])
def test_is_internal_true_for_known_prefixes(name):
    assert bkm.is_internal_table(name) is True


@pytest.mark.parametrize("name", [
    "Sales",
    "DimDate",
    "MyLocalDateTable_x",   # prefix appears mid-string, not at start
    "Account$Detail",       # '$' mid-string
])
def test_is_internal_false_for_normal_and_midstring(name):
    assert bkm.is_internal_table(name) is False


# ── Group D — enum maps ──────────────────────────────────────────────────────

@pytest.mark.parametrize("key,expected", [
    ("manyToOne", "M:1"),
    ("oneToMany", "1:M"),
    ("oneToOne", "1:1"),
    ("manyToMany", "M:M"),
    (1, "M:1"),
    (2, "1:M"),
    (3, "1:1"),
    (4, "M:M"),
])
def test_cardinality_map(key, expected):
    assert bkm.CARDINALITY_MAP[key] == expected


def test_cardinality_unknown_keys_absent():
    # Unknown values are absent so parse falls back to str(value) at ~line 274.
    assert "manyToManyMany" not in bkm.CARDINALITY_MAP
    assert 99 not in bkm.CARDINALITY_MAP


@pytest.mark.parametrize("key,expected", [
    ("oneDirection", "single"),
    ("bothDirections", "both"),
    ("automatic", "automatic"),
    ("none", "none"),
    (1, "single"),
    (2, "both"),
    (3, "automatic"),
])
def test_crossfilter_map(key, expected):
    assert bkm.CROSSFILTER_MAP[key] == expected


def test_crossfilter_unknown_keys_absent():
    assert "weird" not in bkm.CROSSFILTER_MAP
    assert 99 not in bkm.CROSSFILTER_MAP


# ── Group E — parse_bim_to_markdown (integration) ────────────────────────────

@pytest.fixture
def kitchen_sink_md(tmp_path):
    """A realistic model exercising most rendering paths at once."""
    model = {
        "name": "KitchenSink",
        "tables": [
            {
                "name": "Fact Sales",
                "columns": [
                    {"name": "Amount", "dataType": "double"},
                    {"name": "OrderDateKey", "dataType": "int64", "isKey": True},
                    {"name": "ShipDateKey", "dataType": "int64"},
                    {"name": "SecretCol", "dataType": "int64", "isHidden": True},
                    {"name": "Margin Pct", "dataType": "double",
                     "expression": "DIVIDE([Margin],[Amount])"},  # calc column
                ],
                "measures": [
                    {"name": "Total Sales", "expression": "SUM('Fact Sales'[Amount])",
                     "displayFolder": "KPIs"},
                    {"name": "Total Margin",
                     "expression": ["VAR m = [Total Sales]", "RETURN m * 0.3"],
                     "displayFolder": "KPIs"},   # array-form expression
                    {"name": "Internal Ratio", "expression": "DIVIDE(1,2)",
                     "isHidden": True},          # hidden, root folder
                ],
            },
            {
                "name": "DimDate",
                "columns": [
                    {"name": "DateKey", "dataType": "int64", "isKey": True},
                    {"name": "Year", "dataType": "int64"},
                ],
            },
            {
                "name": "Bridge AccountMap",
                "columns": [{"name": "AccountKey", "dataType": "int64"}],
            },
            {
                "name": "Config",
                "isHidden": True,
                "columns": [{"name": "Setting", "dataType": "string"}],
            },
            {
                "name": "Time Intelligence",
                "calculationGroup": {
                    "calculationItems": [
                        {"name": "YTD", "ordinal": 1,
                         "expression": "CALCULATE([Total Sales], DATESYTD('DimDate'[DateKey]))"},
                        {"name": "Current", "ordinal": 0,
                         "expression": "SELECTEDMEASURE()"},
                    ],
                },
            },
            {
                "name": "LocalDateTable_abc123",
                "columns": [{"name": "InternalMarkerColumn", "dataType": "dateTime"}],
            },
        ],
        "relationships": [
            {"fromTable": "Fact Sales", "fromColumn": "OrderDateKey",
             "toTable": "DimDate", "toColumn": "DateKey",
             "cardinality": "manyToOne", "isActive": True},
            {"fromTable": "Fact Sales", "fromColumn": "ShipDateKey",
             "toTable": "DimDate", "toColumn": "DateKey",
             "cardinality": "manyToOne", "isActive": False},
        ],
    }
    return render(tmp_path, model, model_name="KitchenSink")


def test_bom_prefixed_bim_parses(tmp_path):
    model = {"name": "BomTest",
             "tables": [{"name": "DimDate", "columns": [{"name": "Year", "dataType": "int64"}]}],
             "relationships": []}
    path = write_bim(tmp_path, {"model": model}, bom=True)
    md = bkm.parse_bim_to_markdown(path)
    assert "# Model Schema: BomTest" in md


def test_header_counts(tmp_path):
    model = {
        "name": "Counts",
        "tables": [
            {"name": "Fact Sales", "columns": [{"name": "Amt", "dataType": "double"}],
             "measures": [{"name": "M1", "expression": "1"},
                          {"name": "M2", "expression": "2"}]},
            {"name": "DimDate", "columns": [{"name": "DateKey", "dataType": "int64"}]},
        ],
        "relationships": [
            {"fromTable": "Fact Sales", "fromColumn": "Amt",
             "toTable": "DimDate", "toColumn": "DateKey", "cardinality": "manyToOne"},
        ],
    }
    md = render(tmp_path, model, model_name="Counts")
    assert ("**Counts:** 2 tables, 2 columns, 2 measures, "
            "1 relationships, 0 calculation groups") in md


def test_section_headings_present_and_ordered(kitchen_sink_md):
    headings = [
        "# Model Schema: KitchenSink",
        "## Table Inventory",
        "## Relationships",
        "## Table Details",
        "## Calculation Groups",
        "## Measures",
    ]
    positions = [kitchen_sink_md.index(h) for h in headings]   # raises if missing
    assert positions == sorted(positions)


def test_no_calc_group_section_when_absent(tmp_path):
    model = {"name": "NoCG",
             "tables": [{"name": "DimDate", "columns": [{"name": "Year", "dataType": "int64"}]}],
             "relationships": []}
    md = render(tmp_path, model, model_name="NoCG")
    assert "## Calculation Groups" not in md
    assert "## Measures" in md           # always emitted


def test_active_and_inactive_relationship_sections(kitchen_sink_md):
    md = kitchen_sink_md
    assert "### Active Relationships" in md
    assert "### Inactive Relationships" in md
    # The inactive section carries the USERELATIONSHIP / CROSSFILTER note.
    assert "USERELATIONSHIP()" in md
    assert "CROSSFILTER(…, BOTH)" in md


def test_isactive_defaults_to_active_when_omitted(tmp_path):
    model = {
        "name": "Defaults",
        "tables": [
            {"name": "Fact Sales", "columns": [{"name": "K", "dataType": "int64"}]},
            {"name": "DimDate", "columns": [{"name": "K", "dataType": "int64"}]},
        ],
        "relationships": [
            {"fromTable": "Fact Sales", "fromColumn": "K",
             "toTable": "DimDate", "toColumn": "K", "cardinality": "manyToOne"},
        ],
    }
    md = render(tmp_path, model, model_name="Defaults")
    assert "### Active Relationships" in md
    assert "### Inactive Relationships" not in md


def test_from_cardinality_used_when_cardinality_absent(tmp_path):
    model = {
        "name": "Card",
        "tables": [
            {"name": "Fact Sales", "columns": [{"name": "K", "dataType": "int64"}]},
            {"name": "DimDate", "columns": [{"name": "K", "dataType": "int64"}]},
        ],
        "relationships": [
            {"fromTable": "Fact Sales", "fromColumn": "K",
             "toTable": "DimDate", "toColumn": "K", "fromCardinality": "oneToMany"},
        ],
    }
    md = render(tmp_path, model, model_name="Card")
    assert "1:M" in line_with(md, "Fact Sales[K]")


def test_unknown_cardinality_falls_back_to_raw_string(tmp_path):
    model = {
        "name": "Card",
        "tables": [
            {"name": "Fact Sales", "columns": [{"name": "K", "dataType": "int64"}]},
            {"name": "DimDate", "columns": [{"name": "K", "dataType": "int64"}]},
        ],
        "relationships": [
            {"fromTable": "Fact Sales", "fromColumn": "K",
             "toTable": "DimDate", "toColumn": "K", "cardinality": "weirdCard"},
        ],
    }
    md = render(tmp_path, model, model_name="Card")
    assert "weirdCard" in line_with(md, "Fact Sales[K]")


@pytest.mark.parametrize("cf,expected", [
    ("oneDirection", "single"),
    ("bothDirections", "both"),
    ("weirdCf", "weirdCf"),   # unknown non-empty -> raw string
    (None, "single"),         # absent -> default "single"
])
def test_crossfilter_direction_rendering(tmp_path, cf, expected):
    rel = {"fromTable": "Fact Sales", "fromColumn": "K",
           "toTable": "DimDate", "toColumn": "K", "cardinality": "manyToOne"}
    if cf is not None:
        rel["crossFilteringBehavior"] = cf
    model = {
        "name": "CF",
        "tables": [
            {"name": "Fact Sales", "columns": [{"name": "K", "dataType": "int64"}]},
            {"name": "DimDate", "columns": [{"name": "K", "dataType": "int64"}]},
        ],
        "relationships": [rel],
    }
    md = render(tmp_path, model, model_name="CF")
    assert f"| {expected} |" in line_with(md, "Fact Sales[K]")


def test_hidden_table_flagged_in_inventory(kitchen_sink_md):
    row = line_with(kitchen_sink_md, "| Config |")
    assert "| Y |" in row     # the "Hidden" inventory column


def test_hidden_column_flagged(kitchen_sink_md):
    assert "hidden" in line_with(kitchen_sink_md, "| SecretCol |")


def test_column_datatype_and_key_flag(kitchen_sink_md):
    row = line_with(kitchen_sink_md, "| OrderDateKey |")
    assert "INTEGER" in row     # int64 -> INTEGER
    assert "KEY" in row         # isKey -> KEY flag


def test_calculated_column_renders_with_dax(kitchen_sink_md):
    md = kitchen_sink_md
    assert "**Calculated Columns:**" in md
    assert "- **Margin Pct** (DECIMAL)" in md    # double -> DECIMAL
    assert "DIVIDE([Margin],[Amount])" in md


def test_calculation_group_section_renders_items(kitchen_sink_md):
    md = kitchen_sink_md
    assert "## Calculation Groups" in md
    assert "### Time Intelligence" in md
    assert "**Current** (ordinal 0)" in md       # items sorted by ordinal
    assert "**YTD** (ordinal 1)" in md
    assert "SELECTEDMEASURE()" in md
    assert "DATESYTD('DimDate'[DateKey])" in md
    # A calculationGroup table is NOT listed in the table inventory.
    assert "| Time Intelligence |" not in md


def test_calculated_table_shows_calc_type_in_inventory(tmp_path):
    model = {
        "name": "CalcTbl",
        "tables": [
            {"name": "DimDate", "columns": [{"name": "DateKey", "dataType": "int64"}]},
            {"name": "Projection", "partitions": [{"mode": "calculated"}],
             "columns": [{"name": "Val", "dataType": "double"}]},
        ],
        "relationships": [],
    }
    md = render(tmp_path, model, model_name="CalcTbl")
    assert line_with(md, "| Projection |").lstrip().startswith("| CALC |")


def test_measures_section_heading_hidden_and_folder(kitchen_sink_md):
    md = kitchen_sink_md
    assert "### Measures on Fact Sales" in md
    assert "#### Total Sales" in md
    assert "#### Internal Ratio *(hidden)*" in md
    assert "**Folder: KPIs**" in md


def test_measure_array_expression_normalized(kitchen_sink_md):
    md = kitchen_sink_md
    assert "#### Total Margin" in md
    assert "VAR m = [Total Sales]\nRETURN m * 0.3" in md


def test_internal_table_excluded(kitchen_sink_md):
    md = kitchen_sink_md
    assert "LocalDateTable_abc123" not in md
    assert "InternalMarkerColumn" not in md


def test_relationship_to_internal_table_skipped(tmp_path):
    model = {
        "name": "IntRel",
        "tables": [
            {"name": "Fact Sales", "columns": [{"name": "K", "dataType": "int64"}]},
            {"name": "DimDate", "columns": [{"name": "K", "dataType": "int64"}]},
        ],
        "relationships": [
            {"fromTable": "Fact Sales", "fromColumn": "OrderDate",
             "toTable": "LocalDateTable_x", "toColumn": "Date", "cardinality": "manyToOne"},
            {"fromTable": "Fact Sales", "fromColumn": "K",
             "toTable": "DimDate", "toColumn": "K", "cardinality": "manyToOne"},
        ],
    }
    md = render(tmp_path, model, model_name="IntRel")
    assert ("**Counts:** 2 tables, 2 columns, 0 measures, "
            "1 relationships, 0 calculation groups") in md
    assert "LocalDateTable_x" not in md


def test_empty_model_renders_zeroed_counts_and_no_spurious_sections(tmp_path):
    md = render(tmp_path, {"name": "Empty", "tables": [], "relationships": []},
                model_name="Empty")
    assert "# Model Schema: Empty" in md
    assert ("**Counts:** 0 tables, 0 columns, 0 measures, "
            "0 relationships, 0 calculation groups") in md
    # Static section headers are always emitted; calc-group / sub-sections are not.
    assert "## Table Inventory" in md
    assert "## Relationships" in md
    assert "## Table Details" in md
    assert "## Measures" in md
    assert "## Calculation Groups" not in md
    assert "### Active Relationships" not in md
    assert "### Inactive Relationships" not in md
