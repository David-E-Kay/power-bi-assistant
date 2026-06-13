from pbi_capture import watchdog


def test_total_ram_is_plausible():
    total = watchdog.total_physical_ram_bytes()
    assert total >= 4 * 1024 ** 3            # any dev machine has >= 4 GB


def test_disabled_threshold_short_circuits():
    assert watchdog.is_memory_critical(0) is False
    assert watchdog.is_memory_critical(-5) is False


def test_impossible_threshold_not_critical():
    # 100% of RAM used by python+msmdsrv alone is implausible; must be False
    assert watchdog.is_memory_critical(100.0) is False
