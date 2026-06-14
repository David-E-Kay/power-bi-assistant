import pytest

from pbi_capture import clr_boot


def test_libs_dir_is_repo_root_libs():
    # parents[2] of scripts/pbi_capture/clr_boot.py is the repo root.
    assert clr_boot._LIBS_DIR.name == "libs"
    assert (clr_boot._LIBS_DIR.parent / "scripts").is_dir()


def test_adomd_candidates_prefer_libs_over_dax_studio():
    cands = clr_boot._DLL_CANDIDATES
    libs_i = next(i for i, c in enumerate(cands) if c and "libs" in c.replace("\\", "/"))
    dax_i = next(i for i, c in enumerate(cands) if "DAX Studio" in c)
    assert libs_i < dax_i


def test_tom_candidates_prefer_libs_over_dax_studio():
    cands = clr_boot._TOM_CANDIDATES
    libs_i = next(i for i, c in enumerate(cands) if c and "libs" in c.replace("\\", "/"))
    dax_i = next(i for i, c in enumerate(cands) if "DAX Studio" in c)
    assert libs_i < dax_i


def test_find_tom_dll_uses_env_override(tmp_path, monkeypatch):
    dll = tmp_path / "Microsoft.AnalysisServices.Tabular.dll"
    dll.write_bytes(b"stub")
    monkeypatch.setenv("TOM_DLL_PATH", str(dll))
    # _TOM_CANDIDATES reads the env var at module import; rebuild it for the test.
    monkeypatch.setattr(clr_boot, "_TOM_CANDIDATES",
                        [str(dll)] + clr_boot._TOM_CANDIDATES[1:])
    assert clr_boot.find_tom_dll() == dll


def test_find_tom_dll_missing_raises_with_provision_hint(monkeypatch):
    monkeypatch.setattr(clr_boot, "_TOM_CANDIDATES", ["/no/such/Tabular.dll"])
    with pytest.raises(FileNotFoundError, match="provision_libs"):
        clr_boot.find_tom_dll()
