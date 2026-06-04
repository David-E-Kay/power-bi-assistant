// Initialize mermaid first, opt out of auto-render
if (window.mermaid) {
  mermaid.initialize({ startOnLoad: false, theme: 'dark', securityLevel: 'loose' });
}

// ===== Theme =====
const THEME_KEY = 'rt-guide-demo:theme';
function setTheme(t) {
  document.documentElement.setAttribute('data-theme', t);
  localStorage.setItem(THEME_KEY, t);
  const btn = document.getElementById('themeBtn');
  if (btn) btn.textContent = t === 'light' ? '☀️' : '🌙';
  if (window.mermaid) {
    mermaid.initialize({ startOnLoad: false, theme: t === 'light' ? 'default' : 'dark', securityLevel: 'loose' });
    rerenderMermaid();
  }
}
function toggleTheme() {
  const cur = document.documentElement.getAttribute('data-theme') || 'dark';
  setTheme(cur === 'light' ? 'dark' : 'light');
}

// ===== Mermaid =====
function rerenderMermaid() {
  document.querySelectorAll('.mermaid').forEach(el => {
    if (!el.dataset.original) el.dataset.original = el.textContent.trim();
    el.innerHTML = el.dataset.original;
    el.removeAttribute('data-processed');
  });
  if (window.mermaid && mermaid.run) mermaid.run();
}

// ===== Code copy buttons =====
function enhanceCodeBlocks() {
  document.querySelectorAll('.code-wrap').forEach(wrap => {
    if (wrap.querySelector('.copy-btn')) return;
    const pre = wrap.querySelector('pre');
    if (!pre) return;
    const btn = document.createElement('button');
    btn.className = 'copy-btn';
    btn.textContent = 'Copy';
    btn.onclick = (e) => {
      e.stopPropagation();
      navigator.clipboard.writeText(pre.textContent.trim()).then(() => {
        btn.textContent = 'Copied!';
        btn.classList.add('copied');
        setTimeout(() => { btn.textContent = 'Copy'; btn.classList.remove('copied'); }, 1500);
      });
    };
    wrap.appendChild(btn);
  });
}

// ===== Layer filter =====
function toggleLayer(n, on) {
  document.body.classList.toggle('hide-l' + n, !on);
}

// ===== Active section highlight on scroll =====
const tocLinks = () => document.querySelectorAll('.toc a');
const sections = () => document.querySelectorAll('section[id]');
function updateActive() {
  const scrollY = window.scrollY + 120;
  let active = null;
  const secs = sections();
  secs.forEach(sec => {
    if (sec.offsetTop <= scrollY) active = sec.id;
  });
  // Snap to last section when scrolled to bottom — handles short last sections
  // whose offsetTop can never satisfy the check above (page can't scroll further).
  if (secs.length && window.innerHeight + window.scrollY >= document.body.offsetHeight - 2) {
    active = secs[secs.length - 1].id;
  }
  tocLinks().forEach(link => {
    link.classList.toggle('active', link.getAttribute('href') === '#' + active);
  });
}
window.addEventListener('scroll', updateActive, { passive: true });

// ===== Search =====
function setupSearch() {
  const input = document.getElementById('search');
  if (!input) return;
  input.addEventListener('input', (e) => {
    const q = e.target.value.toLowerCase().trim();
    const toc = document.getElementById('toc');
    if (!q) {
      toc.classList.remove('search-active');
      tocLinks().forEach(l => l.classList.remove('search-match'));
      return;
    }
    toc.classList.add('search-active');
    tocLinks().forEach(link => {
      const targetId = link.getAttribute('href').slice(1);
      const sec = document.getElementById(targetId);
      const match = (sec && sec.textContent.toLowerCase().includes(q)) || link.textContent.toLowerCase().includes(q);
      link.classList.toggle('search-match', match);
    });
  });
}

// ===== Progress checklists =====
const PROGRESS_KEY = 'rt-guide-demo:progress';
function loadProgress() {
  try { return JSON.parse(localStorage.getItem(PROGRESS_KEY) || '{}'); } catch { return {}; }
}
function saveProgress(state) {
  localStorage.setItem(PROGRESS_KEY, JSON.stringify(state));
}
function initChecklists() {
  const state = loadProgress();
  document.querySelectorAll('.checklist input[type="checkbox"]').forEach(cb => {
    cb.checked = !!state[cb.id];
    cb.addEventListener('change', () => {
      const s = loadProgress();
      s[cb.id] = cb.checked;
      saveProgress(s);
      updateProgressDisplay();
    });
  });
  updateProgressDisplay();
}
function updateProgressDisplay() {
  const allBoxes = document.querySelectorAll('.checklist input[type="checkbox"]');
  const checked = [...allBoxes].filter(c => c.checked).length;
  const total = allBoxes.length;
  const pct = total ? Math.round((checked / total) * 100) : 0;
  const fill = document.getElementById('progressFill');
  const txt = document.getElementById('progressText');
  if (fill) fill.style.width = pct + '%';
  if (txt) txt.textContent = `${checked} of ${total} complete (${pct}%)`;
  document.querySelectorAll('.checklist').forEach(list => {
    const boxes = list.querySelectorAll('input[type="checkbox"]');
    const done = [...boxes].filter(c => c.checked).length;
    const span = list.querySelector('.checklist-progress');
    if (span) span.textContent = `${done}/${boxes.length}`;
  });
}
function resetProgress() {
  if (!confirm('Clear all checked items?')) return;
  saveProgress({});
  document.querySelectorAll('.checklist input[type="checkbox"]').forEach(cb => cb.checked = false);
  updateProgressDisplay();
  toast('Progress reset', 'success');
}

// ===== Admin modal =====
const ADMIN_KEY = 'rt-guide-demo:admin';
function loadAdmin() {
  try { return JSON.parse(localStorage.getItem(ADMIN_KEY) || '{}'); } catch { return {}; }
}
function saveAdmin(s) { localStorage.setItem(ADMIN_KEY, JSON.stringify(s)); }
function openAdmin() {
  document.getElementById('adminModal').classList.add('open');
  refreshAdminStatus();
}
function closeAdmin() {
  document.getElementById('adminModal').classList.remove('open');
}
function toggleCard(id) {
  document.getElementById(id).classList.toggle('open');
}
function markCardDone(key) {
  const s = loadAdmin();
  s[key] = !s[key];  // toggle so user can un-mark if needed
  saveAdmin(s);
  refreshAdminStatus();
  toast(s[key] ? 'Marked as done' : 'Unmarked', 'success');
}
function refreshAdminStatus() {
  const s = loadAdmin();
  ['webhook', 'plugin', 'outdir', 'python'].forEach(k => {
    const el = document.getElementById('status-' + k);
    if (!el) return;
    if (s[k]) {
      el.textContent = 'Done ✓';
      el.classList.add('done');
    } else {
      el.textContent = 'Not done';
      el.classList.remove('done');
    }
  });
  const done = ['webhook','plugin','outdir','python'].filter(k => s[k]).length;
  const banner = document.getElementById('setupBanner');
  if (banner) {
    document.getElementById('bannerCount').textContent = `${done} of 4`;
    if (done === 4) {
      banner.querySelector('.setup-banner-text').innerHTML =
        `<strong>Setup complete:</strong> all 4 helpers done — you're ready to run regressions.`;
      banner.style.borderColor = 'var(--success)';
    }
  }
}
function dismissBanner() {
  document.getElementById('setupBanner').classList.add('dismissed');
}
function testWebhook() {
  const url = document.getElementById('webhookUrl').value.trim();
  if (!url) { toast('Paste your webhook URL first', 'error'); return; }
  toast('Sending test payload…');
  // Power Automate "Send webhook alerts to a chat" trigger validates against an Adaptive Card schema.
  // Must match the envelope used by capture-snapshot.csx / benchmark-measures.csx, or the trigger silently rejects.
  const cardJson = {
    type: 'message',
    attachments: [{
      contentType: 'application/vnd.microsoft.card.adaptive',
      content: {
        $schema: 'http://adaptivecards.io/schemas/adaptive-card.json',
        type: 'AdaptiveCard',
        version: '1.4',
        body: [
          { type: 'TextBlock', text: 'PBI Developer Guide — Webhook Test', weight: 'Bolder', size: 'Medium' },
          { type: 'FactSet', facts: [
            { title: 'Source', value: "Developer guide 'Send test payload' button" },
            { title: 'Status', value: '✅ Webhook is wired up correctly' }
          ]}
        ]
      }
    }]
  };
  fetch(url, {
    method: 'POST',
    mode: 'cors',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(cardJson)
  }).then(async response => {
    if (response.ok) {
      toast('Payload sent — check your Teams channel', 'success');
    } else {
      let detail = '';
      try { detail = (await response.text()).slice(0, 120); } catch (_) {}
      toast('Failed: HTTP ' + response.status + (detail ? ' — ' + detail : ''), 'error');
    }
  }).catch(err => {
    toast('Failed: ' + err.message, 'error');
  });
}
function updateOutdirCmd() {
  const path = document.getElementById('outdirPath').value || 'C:\\Users\\dkay\\Desktop\\PBI-Regression';
  const cmdEl = document.getElementById('outdirCmd');
  cmdEl.textContent = `[Environment]::SetEnvironmentVariable("OUTPUT_DIR", "${path}", "User")`;
  // re-highlight
  if (window.Prism) Prism.highlightElement(cmdEl);
}

// ===== Toast =====
let toastTimer;
function toast(msg, kind) {
  const t = document.getElementById('toast');
  t.textContent = msg;
  t.className = 'toast show' + (kind ? ' ' + kind : '');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => t.classList.remove('show'), 2500);
}

// ===== Init =====
function init() {
  setTheme(localStorage.getItem(THEME_KEY) || 'dark');
  rerenderMermaid();
  if (window.Prism) Prism.highlightAll();
  enhanceCodeBlocks();
  setupSearch();
  initChecklists();
  refreshAdminStatus();
  updateActive();
}
if (document.readyState === 'loading') {
  document.addEventListener('DOMContentLoaded', init);
} else {
  init();
}
