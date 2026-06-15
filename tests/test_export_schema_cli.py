import json


def _load_cli():
    # Import the CLI module by path so its top-of-file sys.path bootstrap runs.
    import importlib.util
    from pathlib import Path
    path = Path(__file__).resolve().parents[1] / "scripts" / "export_schema.py"
    spec = importlib.util.spec_from_file_location("export_schema_cli", path)
    mod = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(mod)
    return mod


_FIXTURE_BIM = {
    "name": "Sales", "compatibilityLevel": 1567,
    "model": {"name": "Sales",
              "tables": [{"name": "Date",
                          "columns": [{"name": "Year", "dataType": "int64"}]}],
              "relationships": []},
}


def test_cli_resolves_connection_and_exports(tmp_path, monkeypatch):
    mod = _load_cli()
    from pbi_capture import discovery, schema_export

    monkeypatch.setattr(discovery, "resolve_connection",
                        lambda connection_string=None, port=None:
                        "Data Source=localhost:51234;Catalog=Sales;")
    monkeypatch.setattr(schema_export, "serialize_live_database",
                        lambda cs: json.dumps(_FIXTURE_BIM))

    md_out = tmp_path / "out.md"
    rc = mod.main(["--name", "Sales",
                   "--bim-out", str(tmp_path / "out.bim"),
                   "--md-out", str(md_out)])
    assert rc == 0
    assert "Model Schema: Sales" in md_out.read_text(encoding="utf-8")
