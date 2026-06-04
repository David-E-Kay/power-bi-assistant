"""Bundle the Power BI developer guides into a single shareable HTML file.

Reads docs/guides/regression-testing-guide.html and docs/guides/measure-benchmarking-guide.html
plus their shared assets (docs/_assets/guide.css, guide.js), inlines all CDN
dependencies (mermaid, Prism core + language plugins, Prism theme CSS), and
emits one self-contained file at docs/power-bi-guides-bundle.html.

The cross-page nav-pill links are rewired to a JS switchPage() function that
swaps the aside and main innerHTML in place. localStorage state (theme, setup
checklist, progress) is preserved across page switches because both pages
already share the same rt-guide-demo:* keys.
"""

from __future__ import annotations

import json
import re
import sys
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DOCS = ROOT / "docs"
GUIDES = DOCS / "guides"
ASSETS = DOCS / "_assets"
OUT = DOCS / "power-bi-guides-bundle.html"
CACHE = ROOT / "scripts" / "_bundler-cache"

CDN = {
    "prism-theme.css":     "https://cdn.jsdelivr.net/npm/prismjs@1.29.0/themes/prism-tomorrow.min.css",
    "mermaid.js":          "https://cdn.jsdelivr.net/npm/mermaid@10.9.1/dist/mermaid.min.js",
    "prism.js":            "https://cdn.jsdelivr.net/npm/prismjs@1.29.0/prism.min.js",
    "prism-bash.js":       "https://cdn.jsdelivr.net/npm/prismjs@1.29.0/components/prism-bash.min.js",
    "prism-csharp.js":     "https://cdn.jsdelivr.net/npm/prismjs@1.29.0/components/prism-csharp.min.js",
    "prism-powershell.js": "https://cdn.jsdelivr.net/npm/prismjs@1.29.0/components/prism-powershell.min.js",
    "prism-sql.js":        "https://cdn.jsdelivr.net/npm/prismjs@1.29.0/components/prism-sql.min.js",
}


def fetch_cached(name: str, url: str) -> str:
    p = CACHE / name
    if p.exists():
        return p.read_text(encoding="utf-8")
    print(f"  downloading {url}")
    p.parent.mkdir(parents=True, exist_ok=True)
    req = urllib.request.Request(url, headers={"User-Agent": "pbi-guide-bundler"})
    with urllib.request.urlopen(req, timeout=60) as r:
        text = r.read().decode("utf-8")
    p.write_text(text, encoding="utf-8")
    return text


def get_aside(html: str) -> str:
    m = re.search(r'<aside class="sidebar">(.*?)</aside>', html, re.DOTALL)
    if not m:
        raise RuntimeError("Could not locate <aside class=\"sidebar\"> block.")
    return m.group(1).strip()


def get_main(html: str) -> str:
    m = re.search(r"<main>(.*?)</main>", html, re.DOTALL)
    if not m:
        raise RuntimeError("Could not locate <main> block.")
    return m.group(1).strip()


def get_modal(html: str) -> str:
    """Slurp the setup-modal markup. Element is `<div class="modal-overlay" id="adminModal">`."""
    s = html.find('<div class="modal-overlay"')
    if s == -1:
        return ""
    e = html.find("<!-- Toast", s)
    if e == -1:
        e = html.find('<div class="toast"', s)
    if e == -1:
        raise RuntimeError("Could not locate end of setup-modal block.")
    return html[s:e].strip()


def js_string(s: str) -> str:
    """JSON-encode a string for safe embedding inside <script> tags.

    Escapes the </ sequence so an embedded '</script>' in source HTML cannot
    terminate the wrapping <script> block.
    """
    return json.dumps(s, ensure_ascii=False).replace("</", "<\\/")


def main() -> int:
    print("Reading sources…")
    reg_html   = (GUIDES / "regression-testing-guide.html").read_text(encoding="utf-8")
    bench_html = (GUIDES / "measure-benchmarking-guide.html").read_text(encoding="utf-8")
    css        = (ASSETS / "guide.css").read_text(encoding="utf-8")
    guide_js   = (ASSETS / "guide.js").read_text(encoding="utf-8")

    print("Fetching CDN libs (cached after first run)…")
    libs = {name: fetch_cached(name, url) for name, url in CDN.items()}

    pages = {
        "regression": {
            "title": "Regression Testing — Developer Guide",
            "h1":    "Regression Testing Guide",
            "aside": get_aside(reg_html),
            "main":  get_main(reg_html),
        },
        "benchmarking": {
            "title": "Measure Benchmarking — Developer Guide",
            "h1":    "Measure Benchmarking Guide",
            "aside": get_aside(bench_html),
            "main":  get_main(bench_html),
        },
    }
    modal = get_modal(reg_html)
    if not modal:
        modal = get_modal(bench_html)

    pages_js = (
        "const PAGES = {\n"
        + ",\n".join(
            f"  {key}: {{\n"
            f"    title: {js_string(p['title'])},\n"
            f"    h1: {js_string(p['h1'])},\n"
            f"    aside: {js_string(p['aside'])},\n"
            f"    main: {js_string(p['main'])}\n"
            f"  }}"
            for key, p in pages.items()
        )
        + "\n};\n"
    )

    switch_js = pages_js + r"""
function switchPage(target) {
  const p = PAGES[target];
  if (!p) return;
  document.getElementById('asideHost').innerHTML = p.aside;
  document.getElementById('mainHost').innerHTML = p.main;
  document.getElementById('pageTitle').textContent = p.h1;
  document.title = p.title;
  document.querySelectorAll('.nav-pill').forEach(np => {
    np.classList.toggle('active', np.dataset.target === target);
  });
  if (window.Prism) {
    try { Prism.highlightAllUnder(document.getElementById('mainHost')); } catch (e) {}
  }
  enhanceCodeBlocks();
  initChecklists();
  rerenderMermaid();
  updateActive();
  window.scrollTo(0, 0);
  try { localStorage.setItem('rt-guide-demo:page', target); } catch (e) {}
}

function _bundleInit() {
  let saved = 'regression';
  try { saved = localStorage.getItem('rt-guide-demo:page') || 'regression'; } catch (e) {}
  switchPage(saved);
}
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', _bundleInit);
} else {
  _bundleInit();
}
"""

    bundled = f"""<!DOCTYPE html>
<html lang="en">
<head>
<meta charset="UTF-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Power BI Developer Guides — Bundled</title>
<style>
{libs['prism-theme.css']}
</style>
<style>
{css}
</style>
</head>
<body>

<header>
  <h1 id="pageTitle">Regression Testing Guide</h1>
  <nav class="nav-pills">
    <a class="nav-pill active" href="#" data-target="regression" onclick="switchPage('regression');return false;">Regression Testing</a>
    <a class="nav-pill" href="#" data-target="benchmarking" onclick="switchPage('benchmarking');return false;">Measure Benchmarking</a>
  </nav>
  <div class="search-box">
    <input type="search" id="search" placeholder="Search the guide…">
  </div>
  <div class="header-actions">
    <button class="btn" onclick="toggleTheme()" id="themeBtn" title="Toggle theme">🌙</button>
    <button class="btn btn-primary" onclick="openAdmin()">Setup</button>
  </div>
</header>

<div class="layout">
  <aside class="sidebar" id="asideHost"></aside>
  <main id="mainHost"></main>
</div>

{modal}

<div class="toast" id="toast"></div>

<script>
{libs['mermaid.js']}
</script>
<script>
{libs['prism.js']}
</script>
<script>
{libs['prism-bash.js']}
</script>
<script>
{libs['prism-csharp.js']}
</script>
<script>
{libs['prism-powershell.js']}
</script>
<script>
{libs['prism-sql.js']}
</script>
<script>
{guide_js}
</script>
<script>
{switch_js}
</script>

</body>
</html>
"""

    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(bundled, encoding="utf-8")
    size_mb = len(bundled.encode("utf-8")) / (1024 * 1024)
    print(f"Wrote {OUT} ({size_mb:.2f} MB)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
