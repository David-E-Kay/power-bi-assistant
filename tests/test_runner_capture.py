from pbi_capture import runner
from pbi_capture.config import CaptureConfig, TestCase


def test_build_capture_cases():
    cfg = CaptureConfig(
        label="x", model_name="m",
        tests=[TestCase("t1", "M One", "grand_total"),
               TestCase("t2", "M One", "by_year")],
        group_by_columns={"by_year": "'Date'[Year]"})
    cases = runner._build_capture_cases(cfg)
    assert cases[0] == ("t1", "M One", "grand_total", 'SUMMARIZECOLUMNS("Result", [M One])')
    assert cases[1] == ("t2", "M One", "by_year",
                        "SUMMARIZECOLUMNS('Date'[Year], \"Result\", [M One])")


def test_timeout_type_classification():
    assert runner._timeout_type("memory threshold 80% sustained ...") == "memory_watchdog"
    assert runner._timeout_type("wall-clock timeout after 60000ms ...") == "query_timeout"
    assert runner._timeout_type("something else") == "query_error"


def test_smoke_type_classification():
    assert runner._smoke_type("timeout", "memory threshold 80% ...") == "memory_watchdog"
    assert runner._smoke_type("timeout", "wall-clock timeout ...") == "smoketest_timeout"
    assert runner._smoke_type("error", "syntax error") == "smoketest_error"
