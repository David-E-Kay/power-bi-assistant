from pbi_capture import runner
from pbi_capture.config import BenchmarkConfig


def _cfg(**kw):
    base = dict(label="b", measures=["M One", "M Two"],
                single_slice_dimensions={"by_month": "'Date'[Month]"},
                cross_product_columns=["'A'[B]", "'Date'[Month]"],
                cross_product_value_filters={"'A'[B]": ["V1"]},
                global_filters={"'Date'[Year]": ["2026"]})
    base.update(kw)
    return BenchmarkConfig(**base)


def test_build_benchmark_cases_matrix():
    cases = runner._build_benchmark_cases(_cfg())
    # per measure: 1 grand_total + 1 slice + 1 cross-product = 3; 2 measures = 6
    assert len(cases) == 6
    assert [c[0] for c in cases] == ["b0001", "b0002", "b0003", "b0004", "b0005", "b0006"]
    tid, measure, context, dax = cases[0]
    assert (measure, context) == ("M One", "grand_total")
    assert dax == ('CALCULATETABLE(ROW("Result", [M One]), '
                   "TREATAS({2026}, 'Date'[Year]))")
    tid, measure, context, dax = cases[1]
    assert context == "by_month"
    assert dax == ("SUMMARIZECOLUMNS('Date'[Month], TREATAS({2026}, 'Date'[Year]), "
                   '"Result", [M One])')
    tid, measure, context, dax = cases[2]
    assert context == "B_x_Month"
    assert dax == ("SUMMARIZECOLUMNS('A'[B], 'Date'[Month], "
                   "TREATAS({2026}, 'Date'[Year]), "
                   'TREATAS({"V1"}, \'A\'[B]), "Result", [M One])')


def test_build_benchmark_cases_no_filters():
    cfg = _cfg(global_filters={}, cross_product_columns=[],
               cross_product_value_filters={})
    cases = runner._build_benchmark_cases(cfg)
    assert len(cases) == 4  # (grand_total + 1 slice) x 2 measures
    assert cases[0][3] == 'ROW("Result", [M One])'


def test_distinct_values():
    rows = [{"c": "x", "[Result]": 1.0}, {"c": "y", "[Result]": 1.0},
            {"c": "z", "[Result]": None}]
    assert runner._distinct_values(["c", "[Result]"], rows) == 2
    assert runner._distinct_values([], []) == 0


def test_false_fast_detection():
    timing = [
        {"test_id": "b1", "measure": "M", "context": "by_x", "status": "ok",
         "row_count": 5, "duration_ms": 10, "distinct_values": 1},   # false-fast
        {"test_id": "b2", "measure": "M", "context": "grand_total", "status": "ok",
         "row_count": 1, "duration_ms": 10, "distinct_values": 1},   # grand total exempt
        {"test_id": "b3", "measure": "M", "context": "by_y", "status": "ok",
         "row_count": 5, "duration_ms": 10, "distinct_values": 3},   # fine
    ]
    flagged = runner._false_fast(timing)
    assert [t["test_id"] for t in flagged] == ["b1"]


def test_report_launch_uri_none_when_missing(tmp_path):
    # When the report wasn't written (e.g. openpyxl absent), the toast must be
    # non-clickable rather than pointing Explorer at a missing file.
    missing = tmp_path / "absent-report.xlsx"
    assert runner._report_launch_uri(missing) is None


def test_report_launch_uri_set_when_present(tmp_path):
    present = tmp_path / "present-report.xlsx"
    present.write_text("x")
    assert runner._report_launch_uri(present) == present.as_uri()


def test_status_emoji_clean_is_check():
    assert runner._status_emoji(True) == "✅"


def test_status_emoji_not_clean_is_warning():
    assert runner._status_emoji(False) == "⚠️"
