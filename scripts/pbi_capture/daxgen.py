"""DAX query construction. Pure string building — no CLR imports.

1:1 port of the DAX construction blocks in capture-snapshot.csx (v9) and
benchmark-measures.csx (v5). Queries are bare table expressions; the caller
prepends EVALUATE (smoke_query is the exception and includes it).
"""
import re


def build_measure_ref(measure: str, global_filters: list[str] | None = None) -> str:
    if not global_filters:
        return f"[{measure}]"
    keep = ", ".join(f"KEEPFILTERS({f})" for f in global_filters)
    return f"CALCULATE([{measure}], {keep})"


def smoke_query(measure_ref: str) -> str:
    # IGNORE() is invalid inside ROW() — never wrap (v8+ .csx behavior).
    return f'EVALUATE ROW("r", {measure_ref})'


def build_capture_query(context: str, group_by_columns: dict[str, str],
                        measure_ref: str, max_rows: int) -> str:
    if context == "grand_total":
        return f'SUMMARIZECOLUMNS("Result", {measure_ref})'
    group_col = group_by_columns[context]
    if "|" in group_col:
        cols = [c.strip() for c in group_col.split("|")]
        if max_rows > 0:
            # TOPN-per-group: first column = partition (lowest cardinality first)
            partition, detail = cols[0], ", ".join(cols[1:])
            return (f"GENERATE(VALUES({partition}), TOPN({max_rows}, "
                    f'SUMMARIZECOLUMNS({detail}, "Result", {measure_ref})))')
        return f'SUMMARIZECOLUMNS({", ".join(cols)}, "Result", {measure_ref})'
    inner = f'SUMMARIZECOLUMNS({group_col}, "Result", {measure_ref})'
    return f"TOPN({max_rows}, {inner})" if max_rows > 0 else inner


def _format_treatas_value(value: str) -> str:
    t = value.strip()
    if re.match(r"^[A-Za-z]+\(", t):  # DAX expression like DATE(2025,1,1)
        return t
    try:
        float(t)
        return t
    except ValueError:
        return f'"{t}"'


def build_treatas_args(filter_dict: dict[str, list[str]]) -> list[str]:
    args = []
    for col, values in filter_dict.items():
        vals = ", ".join(_format_treatas_value(v) for v in values)
        args.append(f"TREATAS({{{vals}}}, {col})")
    return args


def filter_fragment(args: list[str]) -> str:
    return ", " + ", ".join(args) if args else ""


def build_benchmark_grand_total(measure_ref: str, global_filter_args: list[str]) -> str:
    # ROW, not SUMMARIZECOLUMNS: SC with filter args but no grouping cols returns 0 rows.
    if global_filter_args:
        return f'CALCULATETABLE(ROW("Result", {measure_ref}), {", ".join(global_filter_args)})'
    return f'ROW("Result", {measure_ref})'


def build_benchmark_slice(group_col: str, global_filter_fragment: str,
                          measure_ref: str, max_rows: int) -> str:
    inner = f'SUMMARIZECOLUMNS({group_col}{global_filter_fragment}, "Result", {measure_ref})'
    return f"TOPN({max_rows}, {inner})" if max_rows > 0 else inner


def build_benchmark_cross_product(columns: list[str], global_filter_fragment: str,
                                  cross_filter_fragment: str, measure_ref: str,
                                  max_rows: int) -> str:
    all_cols = ", ".join(columns)
    inner = (f"SUMMARIZECOLUMNS({all_cols}{global_filter_fragment}"
             f'{cross_filter_fragment}, "Result", {measure_ref})')
    return f"TOPN({max_rows}, {inner})" if max_rows > 0 else inner


def cross_product_label(columns: list[str]) -> str:
    parts = []
    for c in columns:
        start, end = c.find("[") + 1, c.find("]")
        parts.append((c[start:end] if 0 < start < end else c).replace(" ", "_"))
    return "_x_".join(parts)
