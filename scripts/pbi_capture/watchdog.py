"""Memory watchdog: aborts runaway queries before they page the machine out.

Deviation from the .csx templates (which hard-coded a 16 GB denominator
because TE's sandbox blocked P/Invoke): total physical RAM is read for real
via GlobalMemoryStatusEx. MEMORY_THRESHOLD_PCT semantics are unchanged.
Working sets summed: this python process (was: TE3) + all msmdsrv processes.
"""
import ctypes

from .clr_boot import ensure_adomd


class _MEMORYSTATUSEX(ctypes.Structure):
    _fields_ = [
        ("dwLength", ctypes.c_uint32),
        ("dwMemoryLoad", ctypes.c_uint32),
        ("ullTotalPhys", ctypes.c_uint64),
        ("ullAvailPhys", ctypes.c_uint64),
        ("ullTotalPageFile", ctypes.c_uint64),
        ("ullAvailPageFile", ctypes.c_uint64),
        ("ullTotalVirtual", ctypes.c_uint64),
        ("ullAvailVirtual", ctypes.c_uint64),
        ("ullAvailExtendedVirtual", ctypes.c_uint64),
    ]


def total_physical_ram_bytes() -> int:
    stat = _MEMORYSTATUSEX()
    stat.dwLength = ctypes.sizeof(_MEMORYSTATUSEX)
    if not ctypes.windll.kernel32.GlobalMemoryStatusEx(ctypes.byref(stat)):
        return 16 * 1024 ** 3  # unreachable in practice; matches old .csx assumption
    return stat.ullTotalPhys


def is_memory_critical(threshold_pct: float) -> bool:
    """True when (this process + all msmdsrv) working sets exceed threshold_pct
    of physical RAM. False when disabled (<=0) or on any probe error."""
    if threshold_pct <= 0:
        return False
    try:
        ensure_adomd()
        from System.Diagnostics import Process
        used = Process.GetCurrentProcess().WorkingSet64
        for p in Process.GetProcessesByName("msmdsrv"):
            try:
                used += p.WorkingSet64
            except Exception:
                pass
        return used / total_physical_ram_bytes() * 100.0 >= threshold_pct
    except Exception:
        return False
