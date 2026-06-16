"""Provision the Analysis Services client DLLs into repo-local libs/ from NuGet.

Pure download/extract — no .NET SDK or nuget.exe required. See the README
("Setup") for the one-time provisioning step.
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
            rel = name[len(prefix):]
            if "/" in rel:
                continue  # skip localized satellites: lib/<tfm>/<lang>/*.resources.dll
            out[rel] = zf.read(name)
    if not out:
        raise ProvisionError(f"no DLLs under {prefix}")
    return out


def _download(url: str) -> bytes:
    try:
        with urllib.request.urlopen(url, timeout=60) as resp:
            return resp.read()
    except Exception as ex:  # network/HTTP errors -> actionable failure
        raise ProvisionError(f"download failed: {url} -> {ex}")


def provision(packages=DEFAULT_PACKAGES, runtime="netfx", dest=LIBS_DIR,
              *, version=DEFAULT_VERSION, force=False) -> list:
    dest = Path(dest)
    dest.mkdir(parents=True, exist_ok=True)
    written = []
    for package_id, primary_dll in packages:
        if not force and (dest / primary_dll).is_file():
            continue  # idempotent: already provisioned
        data = _download(nupkg_url(package_id, version))
        for fname, content in extract_dlls(data, runtime).items():
            path = dest / fname
            path.write_bytes(content)
            written.append(path)
    return written


def main() -> int:
    import argparse
    p = argparse.ArgumentParser(
        description="Provision Analysis Services client DLLs from NuGet into libs/")
    p.add_argument("--runtime", default="netfx", choices=["netfx", "coreclr"])
    p.add_argument("--version", default=DEFAULT_VERSION)
    p.add_argument("--dest", default=str(LIBS_DIR))
    p.add_argument("--force", action="store_true")
    p.add_argument("--packages", default=None,
                   help="comma-separated package ids (primary dll inferred as <id>.dll)")
    args = p.parse_args()

    packages = DEFAULT_PACKAGES
    if args.packages:
        packages = [(pid.strip(), pid.strip() + ".dll")
                    for pid in args.packages.split(",") if pid.strip()]
    try:
        written = provision(packages, args.runtime, args.dest,
                            version=args.version, force=args.force)
    except ProvisionError as ex:
        print(f"FATAL: {ex}", file=sys.stderr)
        return 2
    if written:
        print(f"Provisioned {len(written)} DLL(s) to {args.dest}")
        for w in written:
            print(f"  {w.name}")
    else:
        print(f"Already provisioned in {args.dest} (use --force to refresh)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
