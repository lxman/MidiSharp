const $ = id => document.getElementById(id);
const fmt = s => {
  if (!isFinite(s) || s < 0) s = 0;
  const m = Math.floor(s / 60), sec = Math.floor(s % 60);
  return `${m}:${sec.toString().padStart(2, '0')}`;
};
const escapeHtml = s => String(s).replace(/[&<>"']/g,
  c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

let selectedMidi = null;        // { name, path }
let selectedSoundfont = null;   // { name, path }
const overrides = new Map();    // "bank:program" -> override DTO
const catalogCache = new Map(); // font path -> catalog

async function loadDevices() {
  const devices = await fetch('/api/devices').then(r => r.json());
  const dev = $('device');
  dev.innerHTML = '';
  for (const d of devices) {
    const o = document.createElement('option');
    o.value = d.id;
    o.textContent = d.name + (d.isDefault ? '  (default)' : '');
    if (d.isDefault) o.selected = true;
    dev.appendChild(o);
  }
}

// ---------------- file browser ----------------
let bwKind = 'sf';
let bwOnPick = null;
let bwData = null;                       // current directory listing
const bwLast = { midi: null, sf: null }; // remember last folder per kind

function openBrowser(kind, onPick) {
  bwKind = kind;
  bwOnPick = onPick;
  $('bwFilter').value = '';
  $('browseBackdrop').hidden = false;
  browseTo(bwLast[kind]);
  $('bwFilter').focus();
}

function closeBrowser() {
  $('browseBackdrop').hidden = true;
  bwOnPick = null;
}

async function browseTo(path) {
  $('bwStatus').textContent = 'Loading…';
  $('bwList').innerHTML = '';
  let data;
  try {
    data = await fetch(`/api/browse?kind=${bwKind}&path=${encodeURIComponent(path ?? '')}`).then(r => r.json());
  } catch (e) {
    $('bwStatus').textContent = 'Error: ' + e;
    return;
  }
  if (data.error) { $('bwStatus').textContent = 'Error: ' + data.error; return; }
  bwData = data;
  bwLast[bwKind] = data.path;
  $('bwFilter').value = '';
  renderBrowse();
}

function renderBrowse() {
  if (!bwData) return;
  const d = bwData;

  // breadcrumb
  const crumbs = $('bwCrumbs');
  crumbs.innerHTML = '';
  const addCrumb = (label, full) => {
    const a = document.createElement('a');
    a.textContent = label;
    a.onclick = () => browseTo(full);
    crumbs.appendChild(a);
  };
  addCrumb('/', '/');
  let acc = '';
  for (const seg of d.path.split('/').filter(Boolean)) {
    acc += '/' + seg;
    const sep = document.createElement('span'); sep.className = 'sep'; sep.textContent = '›';
    crumbs.appendChild(sep);
    addCrumb(seg, acc);
  }

  // list (folders then files), filtered by the current folder filter
  const f = $('bwFilter').value.trim().toLowerCase();
  const match = n => !f || n.toLowerCase().includes(f);
  const fileIcon = bwKind === 'midi' ? '🎵' : '🔊';
  const list = $('bwList');
  list.innerHTML = '';

  if (d.parent && !f) addRow(list, '⬆', '..', 'dir', () => browseTo(d.parent));
  const dirs = d.dirs.filter(x => match(x.name));
  const files = d.files.filter(x => match(x.name));
  for (const dir of dirs) addRow(list, '📁', dir.name, 'dir', () => browseTo(dir.path));

  const CAP = 500;
  for (const file of files.slice(0, CAP)) {
    addRow(list, fileIcon, file.name, 'file', () => { const cb = bwOnPick; closeBrowser(); cb && cb(file); });
  }

  let status = `${dirs.length} folder(s), ${files.length} file(s)` + (f ? ' matching' : '');
  if (files.length > CAP) status += ` — showing first ${CAP}, filter to narrow`;
  $('bwStatus').textContent = status;
}

function addRow(list, icon, label, cls, onClick) {
  const row = document.createElement('div');
  row.className = 'bw-row ' + cls;
  row.innerHTML = `<span class="ic">${icon}</span><span class="nm">${escapeHtml(label)}</span>`;
  row.onclick = onClick;
  list.appendChild(row);
}

// ---------------- patches + overrides ----------------
function resetPatches() {
  overrides.clear();
  $('patches').innerHTML = '';
  $('patchHint').textContent = '';
}

async function getCatalog(path) {
  if (catalogCache.has(path)) return catalogCache.get(path);
  const res = await fetch('/api/soundfont-patches?path=' + encodeURIComponent(path)).then(r => r.json());
  if (res.error) throw new Error(res.error);
  catalogCache.set(path, res);
  return res;
}

function instLabel(c) {
  const kind = c.bank === 128 ? 'kit ' : 'prog ';
  const bankPart = (c.bank && c.bank !== 128) ? ' (bank ' + c.bank + ')' : '';
  return c.name + '  ·  ' + kind + c.program + bankPart;
}

async function analyze() {
  $('error').textContent = '';
  resetPatches();
  if (!selectedMidi || !selectedSoundfont) {
    $('error').textContent = 'Choose a MIDI file and a base SoundFont first.';
    return;
  }
  $('patchHint').textContent = 'Analyzing…';
  let used;
  try {
    used = await fetch(`/api/patches?midiPath=${encodeURIComponent(selectedMidi.path)}`
      + `&soundfontPath=${encodeURIComponent(selectedSoundfont.path)}`).then(r => r.json());
  } catch (e) {
    $('patchHint').textContent = '';
    $('error').textContent = 'Analyze failed: ' + e;
    return;
  }
  if (used.error) {
    $('patchHint').textContent = '';
    $('error').textContent = 'Analyze failed: ' + used.error;
    return;
  }
  renderPatches(used);
  $('patchHint').textContent = `${used.length} patch${used.length === 1 ? '' : 'es'} used`
    + ' — leave on the base font or override any below';
}

function renderPatches(used) {
  const box = $('patches');
  box.innerHTML = '';

  for (const p of used) {
    const key = `${p.bank}:${p.program}`;
    const row = document.createElement('div');
    row.className = 'patch' + (p.isDrum ? ' drum' : '');

    const head = document.createElement('div');
    head.className = 'head';
    const addr = p.isDrum ? `kit · prog ${p.program}`
      : `prog ${p.program}` + (p.bank ? ` · bank ${p.bank}` : '');
    head.innerHTML = `<span class="addr">${escapeHtml(addr)}</span>`
      + `<span class="name">${escapeHtml(p.name ?? '(not in base font)')}</span>`;
    row.appendChild(head);

    const ov = document.createElement('div');
    ov.className = 'ov';

    let srcPath = null;
    const srcBtn = document.createElement('button');
    srcBtn.className = 'srcbtn empty';
    srcBtn.textContent = 'Choose source font…';

    const inst = document.createElement('select');
    inst.disabled = true;
    inst.innerHTML = '<option>— pick instrument —</option>';

    const tag = document.createElement('span');
    tag.className = 'tag';

    const clear = document.createElement('button');
    clear.className = 'clear';
    clear.textContent = 'reset';
    clear.title = 'revert this patch to the base font';

    srcBtn.onclick = () => openBrowser('sf', async f => {
      srcPath = f.path;
      srcBtn.classList.remove('empty');
      srcBtn.textContent = f.name;
      overrides.delete(key);
      tag.textContent = '';
      inst.disabled = true;
      inst.innerHTML = '<option>loading…</option>';
      let cat;
      try { cat = await getCatalog(f.path); }
      catch { inst.innerHTML = '<option>(failed to load)</option>'; return; }
      inst.innerHTML = '<option value="">— pick instrument —</option>';
      for (const c of cat.patches) {
        const o = document.createElement('option');
        o.value = `${c.bank}:${c.program}`;
        o.textContent = instLabel(c);
        inst.appendChild(o);
      }
      inst.disabled = false;
    });

    inst.onchange = () => {
      if (!inst.value || !srcPath) { overrides.delete(key); tag.textContent = ''; return; }
      const [sb, sp] = inst.value.split(':').map(Number);
      overrides.set(key, {
        logicalBank: p.bank, logicalProgram: p.program,
        sourcePath: srcPath, sourceBank: sb, sourceProgram: sp,
      });
      tag.textContent = '→ overridden';
    };

    clear.onclick = () => {
      srcPath = null;
      srcBtn.classList.add('empty');
      srcBtn.textContent = 'Choose source font…';
      inst.disabled = true;
      inst.innerHTML = '<option>— pick instrument —</option>';
      overrides.delete(key);
      tag.textContent = '';
    };

    ov.appendChild(srcBtn);
    ov.appendChild(inst);
    ov.appendChild(tag);
    ov.appendChild(clear);
    row.appendChild(ov);
    box.appendChild(row);
  }
}

// ---------------- transport ----------------
async function play() {
  $('error').textContent = '';
  $('defects').textContent = '';
  if (!selectedMidi || !selectedSoundfont) {
    $('error').textContent = 'Choose a MIDI file and a base SoundFont first.';
    return;
  }
  const body = {
    deviceId: $('device').value || null,
    midiPath: selectedMidi.path,
    soundfontPath: selectedSoundfont.path,
    overrides: [...overrides.values()],
  };
  const res = await fetch('/api/play', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).then(r => r.json());

  if (!res.ok) { $('error').textContent = 'Error: ' + res.error; return; }
  if (res.defects && res.defects.length)
    $('defects').textContent = 'Repaired this file:\n  ' + res.defects.join('\n  ');
}

$('midiPick').onclick = () => openBrowser('midi', f => {
  selectedMidi = f; $('midiPick').classList.remove('empty'); $('midiPick').textContent = f.name; resetPatches();
});
$('sfPick').onclick = () => openBrowser('sf', f => {
  selectedSoundfont = f; $('sfPick').classList.remove('empty'); $('sfPick').textContent = f.name; resetPatches();
});
$('analyze').onclick = analyze;
$('play').onclick = play;
$('stop').onclick = () => fetch('/api/stop', { method: 'POST' });
$('exit').onclick = () => fetch('/api/exit', { method: 'POST' });
$('bwClose').onclick = closeBrowser;
$('browseBackdrop').onclick = e => { if (e.target === $('browseBackdrop')) closeBrowser(); };
$('bwFilter').oninput = renderBrowse;
document.addEventListener('keydown', e => { if (e.key === 'Escape' && !$('browseBackdrop').hidden) closeBrowser(); });

// ---------------- live status ----------------
function connectStatus() {
  const ws = new WebSocket(`ws://${location.host}/ws`);
  ws.onmessage = ev => {
    const s = JSON.parse(ev.data);
    const playing = s.state === 'playing';
    $('state').textContent = s.state.charAt(0).toUpperCase() + s.state.slice(1);
    $('time').textContent = (s.durationSeconds > 0)
      ? `  ${fmt(s.positionSeconds)} / ${fmt(s.durationSeconds)}` : '';
    $('fill').style.width = (s.durationSeconds > 0)
      ? `${Math.min(100, 100 * s.positionSeconds / s.durationSeconds)}%` : '0';
    $('play').disabled = playing;
    $('stop').disabled = !playing;
  };
  ws.onclose = () => setTimeout(connectStatus, 1000);
}

loadDevices().catch(e => $('error').textContent = 'Failed to load devices: ' + e);
connectStatus();
