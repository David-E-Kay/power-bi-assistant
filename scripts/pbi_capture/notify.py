"""Desktop toast notifications (Windows). Zero-dependency: shells PowerShell to
show a native WinRT toast. The XML builder is pure (unit-tested); the sender is
thin I/O. Non-fatal — a failure returns a warning string, never raises.
"""
import base64
import subprocess
import sys
from xml.sax.saxutils import escape

# Well-known AppUserModelID for Windows PowerShell. WinRT toasts require a
# registered AppId to render at all; this one ships with Windows.
_POWERSHELL_AUMID = (
    r"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe")


def build_toast_xml(title, body_lines, launch_uri=None):
    """Build a WinRT ToastGeneric XML payload. Pure — no I/O.

    The first <text> is the title; each body line is an additional <text>.
    When launch_uri is given, the whole toast becomes clickable via protocol
    activation: clicking hands the URI to the shell launcher (opens a folder in
    Explorer or a file in its default app).
    """
    texts = "".join(f"<text>{escape(line)}</text>"
                    for line in [title, *body_lines])
    root_attrs = ""
    if launch_uri:
        root_attrs = f' activationType="protocol" launch="{escape(launch_uri)}"'
    return (f"<toast{root_attrs}><visual>"
            f'<binding template="ToastGeneric">{texts}</binding>'
            f"</visual></toast>")


def _powershell_script(toast_xml):
    """PowerShell that loads WinRT and shows the toast under the PS AUMID.
    The XML is embedded as a single-quoted PS string (single quotes doubled)."""
    ps_xml = toast_xml.replace("'", "''")
    return (
        "[Windows.UI.Notifications.ToastNotificationManager,"
        " Windows.UI.Notifications, ContentType=WindowsRuntime] | Out-Null;"
        "[Windows.Data.Xml.Dom.XmlDocument,"
        " Windows.Data.Xml.Dom, ContentType=WindowsRuntime] | Out-Null;"
        "$doc = New-Object Windows.Data.Xml.Dom.XmlDocument;"
        f"$doc.LoadXml('{ps_xml}');"
        "$toast = New-Object Windows.UI.Notifications.ToastNotification $doc;"
        "[Windows.UI.Notifications.ToastNotificationManager]"
        f"::CreateToastNotifier('{_POWERSHELL_AUMID}').Show($toast);")


def send_desktop_toast(title, body_lines, launch_uri=None):
    """Show a desktop toast. Returns None on success or non-Windows (silent
    no-op), or a one-line warning string on failure. Never raises.

    The script is passed via -EncodedCommand (UTF-16LE base64) to sidestep
    command-line quoting and PowerShell execution-policy restrictions.
    """
    if sys.platform != "win32":
        return None
    try:
        xml = build_toast_xml(title, body_lines, launch_uri)
        script = _powershell_script(xml)
        encoded = base64.b64encode(script.encode("utf-16-le")).decode("ascii")
        subprocess.run(
            ["powershell", "-NoProfile", "-NonInteractive",
             "-EncodedCommand", encoded],
            capture_output=True, timeout=15, check=True)
        return None
    except Exception as ex:  # never fail a completed run over a toast
        return f"  Desktop notification failed: {ex}"
