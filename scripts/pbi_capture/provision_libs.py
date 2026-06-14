"""Provision the Analysis Services client DLLs into repo-local libs/ from NuGet.

Pure download/extract — no .NET SDK or nuget.exe required. See
docs/superpowers/plans/2026-06-14-nuget-client-libs-spec.md.
"""
import io
import re
import sys
import urllib.request
import zipfile
from pathlib import Path

LIBS_DIR = Path(__file__).resolve().parents[2] / "libs"
DEFAULT_VERSION = "19.114.0"
# (package id, primary dll used to detect "already provisioned")
DEFAULT_PACKAGES = [
    ("Microsoft.AnalysisServices.AdomdClient",
     "Microsoft.AnalysisServices.AdomdClient.dll"),
    ("Microsoft.AnalysisServices",
     "Microsoft.AnalysisServices.Tabular.dll"),
]
_RUNTIME_TFM_PREFIXES = {
    "netfx": ("net4",),
    "coreclr": ("net6", "net7", "net8", "netcoreapp", "netstandard"),
}


class ProvisionError(RuntimeError):
    pass


def nupkg_url(package_id: str, version: str) -> str:
    lid = package_id.lower()
    return (f"https://api.nuget.org/v3-flatcontainer/"
            f"{lid}/{version}/{lid}.{version}.nupkg")


def _tfm_key(tfm: str):
    m = re.fullmatch(r"net(\d)(\d)(\d?)", tfm)   # net45 / net472 / net48 / net481
    if m:
        return tuple(int(d) for d in m.groups() if d)
    nums = re.findall(r"\d+", tfm)                # net6.0, net8.0, netstandard2.0
    return tuple(int(n) for n in nums) or (0,)


def _select_tfm(tfms, runtime: str) -> str:
    prefixes = _RUNTIME_TFM_PREFIXES.get(runtime)
    if prefixes is None:
        raise ProvisionError(f"unknown runtime {runtime!r}")
    matches = [t for t in tfms if t.lower().startswith(prefixes)]
    if not matches:
        raise ProvisionError(
            f"no {runtime} TFM among {sorted(tfms)} (prefixes {prefixes})")
    return max(matches, key=_tfm_key)


def extract_dlls(nupkg_bytes: bytes, runtime: str) -> dict:
    """Pure: return {dll_filename: bytes} for the runtime-matching lib/<tfm>/."""
    zf = zipfile.ZipFile(io.BytesIO(nupkg_bytes))
    tfms = set()
    for name in zf.namelist():
        m = re.match(r"lib/([^/]+)/", name)
        if m:
            tfms.add(m.group(1))
    if not tfms:
        raise ProvisionError("nupkg has no lib/<tfm>/ entries")
    tfm = _select_tfm(tfms, runtime)
    prefix = f"lib/{tfm}/"
    out = {}
    for name in zf.namelist():
        if name.startswith(prefix) and name.lower().endswith(".dll"):
            out[name[len(prefix):]] = zf.read(name)
    if not out:
        raise ProvisionError(f"no DLLs under {prefix}")
    return out
