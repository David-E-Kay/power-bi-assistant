import io
import zipfile

import pytest

from pbi_capture import provision_libs
from pbi_capture.provision_libs import ProvisionError


def _fixture_nupkg() -> bytes:
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        zf.writestr("lib/net45/A.dll", b"netfx-A")
        zf.writestr("lib/net45/B.dll", b"netfx-B")
        zf.writestr("lib/net45/A.xml", b"<doc/>")        # non-dll, ignored
        zf.writestr("lib/net6.0/A.dll", b"core-A")
        zf.writestr("[Content_Types].xml", b"<x/>")        # non-lib, ignored
    return buf.getvalue()


def test_nupkg_url_lowercases_id():
    url = provision_libs.nupkg_url("Microsoft.AnalysisServices.AdomdClient", "19.114.0")
    assert url == (
        "https://api.nuget.org/v3-flatcontainer/"
        "microsoft.analysisservices.adomdclient/19.114.0/"
        "microsoft.analysisservices.adomdclient.19.114.0.nupkg"
    )


def test_select_tfm_netfx_picks_highest_net4():
    assert provision_libs._select_tfm(["net45", "net472", "net6.0"], "netfx") == "net472"


def test_select_tfm_coreclr_picks_highest():
    assert provision_libs._select_tfm(
        ["net45", "net6.0", "net8.0", "netstandard2.0"], "coreclr") == "net8.0"


def test_select_tfm_netfx_prefers_net48_over_net472():
    # net4.8 is newer than net4.7.2; integer-collapsing the moniker would
    # mis-rank net472 above net48, so this pins the per-digit ordering.
    assert provision_libs._select_tfm(["net45", "net472", "net48"], "netfx") == "net48"


def test_select_tfm_no_match_raises():
    with pytest.raises(ProvisionError):
        provision_libs._select_tfm(["net6.0"], "netfx")


def test_extract_dlls_netfx_returns_net4_dlls_only():
    dlls = provision_libs.extract_dlls(_fixture_nupkg(), "netfx")
    assert set(dlls) == {"A.dll", "B.dll"}
    assert dlls["A.dll"] == b"netfx-A"


def test_extract_dlls_coreclr_returns_core_dlls():
    dlls = provision_libs.extract_dlls(_fixture_nupkg(), "coreclr")
    assert set(dlls) == {"A.dll"}
    assert dlls["A.dll"] == b"core-A"


def test_extract_dlls_no_lib_raises():
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as zf:
        zf.writestr("readme.txt", b"hi")
    with pytest.raises(ProvisionError):
        provision_libs.extract_dlls(buf.getvalue(), "netfx")
