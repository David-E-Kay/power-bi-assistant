#!/usr/bin/env python3
"""
bim_to_kb_markdown.py — Convert a Power BI .bim file into a RAG-optimized
markdown file for uploading to Claude Project Knowledge.

Usage:
    python bim_to_kb_markdown.py <path_to.bim> [--model-id <id>] [--model-name <name>] [--output <path>]

Produces a single markdown file structured with headings that chunk well
for RAG retrieval. Replace the file in Project Knowledge only when the
TOM structure materially changes (new tables, relationship topology,
new/rewritten measures).
"""

import json
import sys
import re
import os
from datetime import datetime, timezone


# ── Data type mapping ────────────────────────────────────────────────────────

TOM_TYPE_MAP = {
    "string": "TEXT",
    "int64": "INTEGER",
    "double": "DECIMAL",
    "decimal": "DECIMAL",
    "boolean": "BOOLEAN",
    "dateTime": "DATETIME",
    "binary": "BINARY",
    "automatic": "AUTO",
    "int32": "INTEGER",
    "currency": "CURRENCY",
    "unknown": "UNKNOWN",
}

CARDINALITY_MAP = {
    "manyToOne": "M:1",
    "oneToMany": "1:M",
    "oneToOne": "1:1",
    "manyToMany": "M:M",
    1: "M:1",
    2: "1:M",
    3: "1:1",
    4: "M:M",
}

CROSSFILTER_MAP = {
    "oneDirection": "single",
    "bothDirections": "both",
    "automatic": "automatic",
    "none": "none",
    1: "single",
    2: "both",
    3: "automatic",
}


# ── Table type inference ─────────────────────────────────────────────────────

def infer_table_type(table_name, table_obj, relationships):
    name_lower = table_name.lower().replace(" ", "")

    if table_obj.get("calculationGroup"):
        return "calc"

    partitions = table_obj.get("partitions", [])
    if partitions and all(
        p.get("mode") == "calculated"
        or p.get("source", {}).get("type") == "calculated"
        for p in partitions
    ):
        return "calc"

    if name_lower.startswith("fact") or name_lower.startswith("fct"):
        return "fact"
    if name_lower.startswith("dim") or name_lower.startswith("d_"):
        return "dim"
    if name_lower.startswith("bridge") or name_lower.startswith("brg"):
        return "bridge"

    many_side_count = 0
    one_side_count = 0
    for rel in relationships:
        if rel.get("fromTable") == table_name:
            many_side_count += 1
        if rel.get("toTable") == table_name:
            one_side_count += 1

    if many_side_count > 0 and one_side_count == 0:
        return "fact"
    if one_side_count > 0 and many_side_count == 0:
        return "dim"
    if many_side_count > 1 and one_side_count > 1:
        return "bridge"

    return "dim"


def is_internal_table(table_name):
    for prefix in ["LocalDateTable_", "DateTableTemplate_", "RowNumber-", "$"]:
        if table_name.startswith(prefix):
            return True
    return False


def normalize_expression(expr):
    """Convert .bim expression to plain string.
    
    .bim files store DAX expressions as either a plain string or a JSON
    array of strings (one per line). This normalizes both to a single string.
    """
    if isinstance(expr, list):
        return "\n".join(expr)
    return expr or ""


# ── Main parser ──────────────────────────────────────────────────────────────

def parse_bim_to_markdown(bim_path, model_id=None, model_name=None):
    with open(bim_path, "r", encoding="utf-8-sig") as f:
        bim = json.load(f)

    model_obj = bim.get("model", bim)

    if not model_name:
        model_name = (
            model_obj.get("name")
            or model_obj.get("description")
            or os.path.splitext(os.path.basename(bim_path))[0]
        )

    if not model_id:
        model_id = re.sub(r"[^a-zA-Z0-9]+", "_", model_name).strip("_").lower()

    now = datetime.now(timezone.utc).strftime("%Y-%m-%d %H:%M UTC")
    tables_raw = model_obj.get("tables", [])
    relationships = model_obj.get("relationships", [])

    # ── Classify tables ──────────────────────────────────────────────────

    tables_by_type = {"fact": [], "dim": [], "bridge": [], "calc": []}
    all_tables = {}  # name → parsed data
    calc_groups = []

    for table in tables_raw:
        table_name = table.get("name", "")
        if is_internal_table(table_name) or not table_name:
            continue

        table_type = infer_table_type(table_name, table, relationships)
        is_hidden = table.get("isHidden", False)
        description = table.get("description", "")
        if isinstance(description, list):
            description = " ".join(description)
        is_calc_group = table.get("calculationGroup") is not None

        # Columns
        columns = []
        calc_columns = []
        for col in table.get("columns", []):
            col_name = col.get("name", "")
            if not col_name or col_name.startswith("RowNumber-"):
                continue

            data_type = col.get("dataType", "")
            mapped_type = TOM_TYPE_MAP.get(
                data_type, data_type.upper() if data_type else "?"
            )
            is_key = col.get("isKey", False)
            col_hidden = col.get("isHidden", False)
            sort_by = col.get("sortByColumn", "")
            expression = normalize_expression(col.get("expression", ""))

            flags = []
            if is_key:
                flags.append("KEY")
            if col_hidden:
                flags.append("hidden")
            if sort_by:
                flags.append(f"sort by {sort_by}")

            col_info = {
                "name": col_name,
                "data_type": mapped_type,
                "flags": ", ".join(flags) if flags else "",
                "expression": expression,
            }

            if expression:
                calc_columns.append(col_info)
            else:
                columns.append(col_info)

        # Measures
        measures = []
        for measure in table.get("measures", []):
            m_name = measure.get("name", "")
            if not m_name:
                continue
            dax = normalize_expression(measure.get("expression", ""))
            m_hidden = measure.get("isHidden", False)
            display_folder = measure.get("displayFolder", "")
            m_format = measure.get("formatString", "")
            m_desc = measure.get("description", "")
            if isinstance(m_desc, list):
                m_desc = " ".join(m_desc)

            measures.append({
                "name": m_name,
                "dax": dax,
                "hidden": m_hidden,
                "folder": display_folder,
                "format": m_format,
                "description": m_desc,
            })

        # Hierarchies
        hierarchies = []
        for hier in table.get("hierarchies", []):
            hier_name = hier.get("name", "")
            levels = [
                lvl.get("column", lvl.get("name", ""))
                for lvl in hier.get("levels", [])
            ]
            if hier_name and levels:
                hierarchies.append({"name": hier_name, "levels": levels})

        # Calc group items
        if is_calc_group:
            cg_obj = table["calculationGroup"]
            items = []
            for item in cg_obj.get("calculationItems", []):
                items.append({
                    "name": item.get("name", ""),
                    "ordinal": item.get("ordinal", 0),
                    "expression": normalize_expression(item.get("expression", "")),
                })
            calc_groups.append({
                "name": table_name,
                "description": description,
                "items": sorted(items, key=lambda x: x["ordinal"]),
            })
            continue  # Don't add to regular tables

        table_data = {
            "name": table_name,
            "type": table_type,
            "hidden": is_hidden,
            "description": description,
            "columns": columns,
            "calc_columns": calc_columns,
            "measures": measures,
            "hierarchies": hierarchies,
        }

        tables_by_type[table_type].append(table_data)
        all_tables[table_name] = table_data

    # ── Parse relationships ──────────────────────────────────────────────

    rels_parsed = []
    for rel in relationships:
        from_table = rel.get("fromTable", "")
        from_column = rel.get("fromColumn", "")
        to_table = rel.get("toTable", "")
        to_column = rel.get("toColumn", "")

        if is_internal_table(from_table) or is_internal_table(to_table):
            continue

        cardinality = rel.get("cardinality", rel.get("fromCardinality", ""))
        mapped_card = CARDINALITY_MAP.get(cardinality, str(cardinality))

        cross_filter = rel.get("crossFilteringBehavior", "")
        mapped_cf = CROSSFILTER_MAP.get(
            cross_filter, str(cross_filter) if cross_filter else "single"
        )

        is_active = rel.get("isActive", True)

        rels_parsed.append({
            "from_table": from_table,
            "from_column": from_column,
            "to_table": to_table,
            "to_column": to_column,
            "cardinality": mapped_card,
            "direction": mapped_cf,
            "active": is_active,
        })

    # ── Build markdown ───────────────────────────────────────────────────

    lines = []

    def w(text=""):
        lines.append(text)

    # Header
    w(f"# Model Schema: {model_name}")
    w()
    w(f"**Parsed:** {now}  ")
    w(f"**Source:** `{os.path.basename(bim_path)}`  ")

    total_tables = sum(len(v) for v in tables_by_type.values())
    total_measures = sum(
        len(t["measures"]) for tlist in tables_by_type.values() for t in tlist
    )
    total_columns = sum(
        len(t["columns"]) + len(t["calc_columns"])
        for tlist in tables_by_type.values()
        for t in tlist
    )
    w(
        f"**Counts:** {total_tables} tables, {total_columns} columns, "
        f"{total_measures} measures, {len(rels_parsed)} relationships, "
        f"{len(calc_groups)} calculation groups"
    )
    w()
    w("---")
    w()

    # ── Table inventory ──────────────────────────────────────────────────

    w("## Table Inventory")
    w()
    w("| Type | Table | Columns | Calc Cols | Measures | Hidden | Description |")
    w("|------|-------|---------|-----------|----------|--------|-------------|")
    for ttype in ["fact", "dim", "bridge", "calc"]:
        for t in sorted(tables_by_type[ttype], key=lambda x: x["name"]):
            hidden_flag = "Y" if t["hidden"] else ""
            desc_short = (t["description"][:60] + "…") if len(t["description"]) > 60 else t["description"]
            w(
                f"| {ttype.upper()} | {t['name']} | {len(t['columns'])} | "
                f"{len(t['calc_columns'])} | {len(t['measures'])} | "
                f"{hidden_flag} | {desc_short} |"
            )
    w()

    # ── Relationships ────────────────────────────────────────────────────

    w("## Relationships")
    w()

    # Active first, then inactive
    active_rels = [r for r in rels_parsed if r["active"]]
    inactive_rels = [r for r in rels_parsed if not r["active"]]

    if active_rels:
        w("### Active Relationships")
        w()
        w("| From (many side) | → | To (one side) | Cardinality | Direction |")
        w("|------------------|---|---------------|-------------|-----------|")
        for r in sorted(active_rels, key=lambda x: x["from_table"]):
            w(
                f"| {r['from_table']}[{r['from_column']}] | → | "
                f"{r['to_table']}[{r['to_column']}] | {r['cardinality']} | "
                f"{r['direction']} |"
            )
        w()

    if inactive_rels:
        w("### Inactive Relationships")
        w()
        w(
            "These require `USERELATIONSHIP()` in DAX to activate. "
            "If bidirectional propagation is also needed, pair with `CROSSFILTER(…, BOTH)`."
        )
        w()
        w("| From (many side) | → | To (one side) | Cardinality | Direction |")
        w("|------------------|---|---------------|-------------|-----------|")
        for r in sorted(inactive_rels, key=lambda x: x["from_table"]):
            w(
                f"| {r['from_table']}[{r['from_column']}] | → | "
                f"{r['to_table']}[{r['to_column']}] | {r['cardinality']} | "
                f"{r['direction']} |"
            )
        w()

    # ── Table details (columns + calc columns) ───────────────────────────

    w("## Table Details")
    w()

    for ttype in ["fact", "dim", "bridge"]:
        for t in sorted(tables_by_type[ttype], key=lambda x: x["name"]):
            w(f"### {t['name']} ({ttype.upper()})")
            w()

            if t["description"]:
                w(f"_{t['description']}_")
                w()

            # Regular columns
            if t["columns"]:
                w("**Columns:**")
                w()
                w("| Column | Data Type | Flags |")
                w("|--------|-----------|-------|")
                for c in sorted(t["columns"], key=lambda x: x["name"]):
                    w(f"| {c['name']} | {c['data_type']} | {c['flags']} |")
                w()

            # Calc columns — show DAX since these are often discussed
            if t["calc_columns"]:
                w("**Calculated Columns:**")
                w()
                for c in sorted(t["calc_columns"], key=lambda x: x["name"]):
                    w(f"- **{c['name']}** ({c['data_type']})")
                    if c["expression"]:
                        # Indent DAX as code block
                        w("  ```dax")
                        for dax_line in c["expression"].strip().split("\n"):
                            w(f"  {dax_line}")
                        w("  ```")
                w()

            # Hierarchies
            if t["hierarchies"]:
                w("**Hierarchies:**")
                w()
                for h in t["hierarchies"]:
                    w(f"- {h['name']}: {' → '.join(h['levels'])}")
                w()

    # ── Calculation Groups ───────────────────────────────────────────────

    if calc_groups:
        w("## Calculation Groups")
        w()
        for cg in sorted(calc_groups, key=lambda x: x["name"]):
            w(f"### {cg['name']}")
            w()
            if cg["description"]:
                w(f"_{cg['description']}_")
                w()
            for item in cg["items"]:
                w(f"**{item['name']}** (ordinal {item['ordinal']})")
                if item["expression"]:
                    w("```dax")
                    w(item["expression"].strip())
                    w("```")
                w()

    # ── Measures ─────────────────────────────────────────────────────────

    w("## Measures")
    w()

    for ttype in ["fact", "dim", "bridge"]:
        for t in sorted(tables_by_type[ttype], key=lambda x: x["name"]):
            if not t["measures"]:
                continue

            w(f"### Measures on {t['name']}")
            w()

            # Group by display folder
            by_folder = {}
            for m in t["measures"]:
                folder = m["folder"] or "(root)"
                by_folder.setdefault(folder, []).append(m)

            for folder in sorted(by_folder.keys()):
                measures = by_folder[folder]
                if folder != "(root)":
                    w(f"**Folder: {folder}**")
                    w()

                for m in sorted(measures, key=lambda x: x["name"]):
                    hidden_tag = " *(hidden)*" if m["hidden"] else ""
                    format_tag = f" — format: `{m['format']}`" if m["format"] else ""
                    w(f"#### {m['name']}{hidden_tag}{format_tag}")
                    w()
                    if m["description"]:
                        w(f"_{m['description']}_")
                        w()
                    if m["dax"]:
                        w("```dax")
                        w(m["dax"].strip())
                        w("```")
                    w()

    return "\n".join(lines)


# ── CLI ───────────────────────────────────────────────────────────────────────

def main():
    import argparse

    parser = argparse.ArgumentParser(
        description="Convert a .bim file into a RAG-optimized markdown for Project Knowledge"
    )
    parser.add_argument("bim_path", help="Path to the .bim file")
    parser.add_argument(
        "--model-id",
        help="Override model ID (default: derived from model name)",
    )
    parser.add_argument(
        "--model-name",
        help="Override model display name (default: from .bim metadata or filename)",
    )
    parser.add_argument(
        "--output",
        "-o",
        help="Output markdown path (default: model-schema-<model_id>.md)",
    )

    args = parser.parse_args()

    if not os.path.exists(args.bim_path):
        print(f"Error: File not found: {args.bim_path}", file=sys.stderr)
        sys.exit(1)

    markdown = parse_bim_to_markdown(
        args.bim_path, args.model_id, args.model_name
    )

    # Default output filename
    if not args.output:
        # Derive from model name in the .bim
        with open(args.bim_path, "r", encoding="utf-8-sig") as f:
            bim = json.load(f)
        model_obj = bim.get("model", bim)
        name = args.model_name or model_obj.get("name") or os.path.splitext(
            os.path.basename(args.bim_path)
        )[0]
        slug = re.sub(r"[^a-zA-Z0-9]+", "-", name).strip("-").lower()
        args.output = f"model-schema-{slug}.md"

    with open(args.output, "w", encoding="utf-8") as f:
        f.write(markdown)

    print(f"Written to: {args.output}", file=sys.stderr)

    # Quick stats
    line_count = markdown.count("\n")
    print(f"  {line_count} lines", file=sys.stderr)


if __name__ == "__main__":
    main()
