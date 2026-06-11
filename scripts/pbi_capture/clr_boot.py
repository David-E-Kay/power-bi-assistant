"""pythonnet bootstrap. Call ensure_adomd() before importing any module that
touches Microsoft.AnalysisServices.AdomdClient.

Runtime selection (first match wins):
  1. PYTHONNET_RUNTIME env var (pythonnet's own override — respected as-is)
  2. "netfx" default — spike-verified: DAX Studio 3.x ADOMD DLL requires
     System.Configuration.ConfigurationManager which is only available under
     the .NET Framework (netfx) runtime. coreclr fails at conn.Open() with
     FileNotFoundException for that assembly. See spec risk table.

DLL discovery (first existing file wins):
  1. ADOMD_DLL_PATH env var (full path to Microsoft.AnalysisServices.AdomdClient.dll)
  2. DAX Studio install
  3. Power BI Desktop (non-Store) install
"""
import os
import sys
from pathlib import Path

_DLL_CANDIDATES = [
    os.environ.get("ADOMD_DLL_PATH", ""),
    r"C:\Program Files\DAX Studio\bin\Microsoft.AnalysisServices.AdomdClient.dll",
    r"C:\Program Files\Microsoft Power BI Desktop\bin\Microsoft.AnalysisServices.AdomdClient.dll",
]

_loaded = False


def find_adomd_dll() -> Path:
    for cand in _DLL_CANDIDATES:
        if cand and Path(cand).is_file():
            return Path(cand)
    raise FileNotFoundError(
        "Microsoft.AnalysisServices.AdomdClient.dll not found. Checked: "
        + " | ".join(c for c in _DLL_CANDIDATES if c)
        + ". Install DAX Studio, or set ADOMD_DLL_PATH to the DLL location."
    )


def ensure_adomd() -> None:
    """Idempotent: initialize the CLR and load the ADOMD client assembly."""
    global _loaded
    if _loaded:
        return
    dll = find_adomd_dll()
    if "clr" not in sys.modules and not os.environ.get("PYTHONNET_RUNTIME"):
        from pythonnet import load
        load("netfx")  # spike-verified default; see module docstring
    import clr
    sys.path.append(str(dll.parent))  # pythonnet probes sys.path for assemblies
    clr.AddReference("Microsoft.AnalysisServices.AdomdClient")
    _loaded = True
