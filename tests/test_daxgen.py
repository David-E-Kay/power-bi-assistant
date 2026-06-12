from pbi_capture import daxgen


def test_measure_ref_bare():
    assert daxgen.build_measure_ref("Total Cost") == "[Total Cost]"


def test_measure_ref_global_filters():
    ref = daxgen.build_measure_ref("Total Cost", ["'Date'[Year] = 2025", "'T'[C] = \"X\""])
    assert ref == ("CALCULATE([Total Cost], KEEPFILTERS('Date'[Year] = 2025), "
                   'KEEPFILTERS(\'T\'[C] = "X"))')


def test_smoke_query():
    assert daxgen.smoke_query("[M]") == 'EVALUATE ROW("r", [M])'


def test_capture_grand_total():
    assert daxgen.build_capture_query("grand_total", {}, "[M]", 0) == \
        'SUMMARIZECOLUMNS("Result", [M])'


def test_capture_single_dim():
    q = daxgen.build_capture_query("by_year", {"by_year": "'Date'[Year]"}, "[M]", 0)
    assert q == "SUMMARIZECOLUMNS('Date'[Year], \"Result\", [M])"


def test_capture_single_dim_topn():
    q = daxgen.build_capture_query("by_year", {"by_year": "'Date'[Year]"}, "[M]", 5)
    assert q == "TOPN(5, SUMMARIZECOLUMNS('Date'[Year], \"Result\", [M]))"


def test_capture_cross_product():
    gbc = {"by_a_x_year": "'Dim'[A]|'Date'[Year]"}
    q = daxgen.build_capture_query("by_a_x_year", gbc, "[M]", 0)
    assert q == "SUMMARIZECOLUMNS('Dim'[A], 'Date'[Year], \"Result\", [M])"


def test_capture_cross_product_topn_per_group_strips_spaces():
    gbc = {"by_a_x_year": "'Dim'[A] | 'Date'[Year]"}
    q = daxgen.build_capture_query("by_a_x_year", gbc, "[M]", 3)
    assert q == ("GENERATE(VALUES('Dim'[A]), TOPN(3, "
                 "SUMMARIZECOLUMNS('Date'[Year], \"Result\", [M])))")


def test_treatas_value_formatting():
    args = daxgen.build_treatas_args({
        "'Date'[Start of Year]": ["DATE(2025,1,1)"],
        "'T'[C]": ["Value 1", "2026", "3.5"],
    })
    assert args == [
        "TREATAS({DATE(2025,1,1)}, 'Date'[Start of Year])",
        'TREATAS({"Value 1", 2026, 3.5}, \'T\'[C])',
    ]


def test_filter_fragment():
    assert daxgen.filter_fragment([]) == ""
    assert daxgen.filter_fragment(["A", "B"]) == ", A, B"


def test_benchmark_grand_total():
    assert daxgen.build_benchmark_grand_total("[M]", []) == 'ROW("Result", [M])'
    assert daxgen.build_benchmark_grand_total("[M]", ["TREATAS({2026}, 'D'[Y])"]) == \
        'CALCULATETABLE(ROW("Result", [M]), TREATAS({2026}, \'D\'[Y]))'


def test_benchmark_slice():
    q = daxgen.build_benchmark_slice("'Date'[Month]", ", TREATAS({2026}, 'D'[Y])", "[M]", 0)
    assert q == "SUMMARIZECOLUMNS('Date'[Month], TREATAS({2026}, 'D'[Y]), \"Result\", [M])"
    q = daxgen.build_benchmark_slice("'Date'[Month]", "", "[M]", 50)
    assert q == "TOPN(50, SUMMARIZECOLUMNS('Date'[Month], \"Result\", [M]))"


def test_benchmark_cross_product():
    q = daxgen.build_benchmark_cross_product(
        ["'A'[B]", "'Date'[Month]"], "", ', TREATAS({"V"}, \'A\'[B])', "[M]", 0)
    assert q == ("SUMMARIZECOLUMNS('A'[B], 'Date'[Month], "
                 'TREATAS({"V"}, \'A\'[B]), "Result", [M])')


def test_cross_product_label():
    assert daxgen.cross_product_label(["'Table A'[Column A]", "'Date'[Month]"]) == \
        "Column_A_x_Month"
    assert daxgen.cross_product_label(["no brackets here"]) == "no_brackets_here"
