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
const overrides = new Map();      // "bank:program" -> patch override DTO
const trackOverrides = new Map(); // trackIndex -> track override DTO
const catalogCache = new Map();   // font path -> catalog
// When a setup is being loaded, these hold its overrides so the pickers render pre-populated.
let loadedOverrides = null;       // "bank:program" -> override DTO  (null when not loading a setup)
let loadedTrackOverrides = null;  // trackIndex -> override DTO
let lastSetupName = '';

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
  // Restore the last-used device if it's still available; otherwise keep the engine default.
  const saved = localStorage.getItem('outputDeviceId');
  if (saved && [...dev.options].some(o => o.value === saved)) dev.value = saved;
  dev.onchange = () => localStorage.setItem('outputDeviceId', dev.value);
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

// ---------------- analysis + overrides ----------------
function resetAnalysis() {
  overrides.clear();
  trackOverrides.clear();
  $('tracks').innerHTML = '';
  $('patches').innerHTML = '';
  $('patchHint').textContent = '';
  $('trackHint').textContent = '';
  $('patchWrap').hidden = true;
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
  resetAnalysis();
  if (!selectedMidi || !selectedSoundfont) {
    $('error').textContent = 'Choose a MIDI file and a base SoundFont first.';
    return;
  }
  $('trackHint').textContent = 'Analyzing…';
  const qs = `midiPath=${encodeURIComponent(selectedMidi.path)}`
    + `&soundfontPath=${encodeURIComponent(selectedSoundfont.path)}`;
  let tracks, used;
  try {
    [tracks, used] = await Promise.all([
      fetch(`/api/tracks?${qs}`).then(r => r.json()),
      fetch(`/api/patches?${qs}`).then(r => r.json()),
    ]);
  } catch (e) {
    $('trackHint').textContent = '';
    $('error').textContent = 'Analyze failed: ' + e;
    return;
  }
  const err = tracks.error || used.error;
  if (err) {
    $('trackHint').textContent = '';
    $('error').textContent = 'Analyze failed: ' + err;
    return;
  }

  renderTracks(tracks);
  const sounding = tracks.filter(t => t.channels.length > 0).length;
  $('trackHint').textContent = `${sounding} instrument${sounding === 1 ? '' : 's'}`
    + ' — leave on the base font or override any below';

  renderPatches(used);
  $('patchHint').textContent = `${used.length} patch${used.length === 1 ? '' : 'es'} used`;
  $('patchWrap').hidden = false;
}

// Build the shared "Choose source font… → pick instrument" override control. onPick(srcPath, sb, sp)
// is called when an instrument is chosen; onClear() when reset. Returns the .ov element.
function buildPicker(onPick, onClear, resetTitle, initial) {
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
  clear.title = resetTitle;

  // Point the picker at a source font and fill its instrument list. Shared by the browse-pick
  // flow and by pre-population from a loaded setup. Returns the catalog (or null on failure).
  async function loadSource(path, name) {
    srcPath = path;
    srcBtn.classList.remove('empty');
    srcBtn.textContent = name;
    onClear();
    tag.textContent = '';
    inst.disabled = true;
    inst.innerHTML = '<option>loading…</option>';
    let cat;
    try { cat = await getCatalog(path); }
    catch { inst.innerHTML = '<option>(failed to load)</option>'; return null; }
    inst.innerHTML = '<option value="">— pick instrument —</option>';
    for (const c of cat.patches) {
      const o = document.createElement('option');
      o.value = `${c.bank}:${c.program}`;
      o.textContent = instLabel(c);
      inst.appendChild(o);
    }
    inst.disabled = false;
    return cat;
  }

  srcBtn.onclick = () => openBrowser('sf', f => loadSource(f.path, f.name));

  inst.onchange = () => {
    if (!inst.value || !srcPath) { onClear(); tag.textContent = ''; return; }
    const [sb, sp] = inst.value.split(':').map(Number);
    onPick(srcPath, sb, sp);
    tag.textContent = '→ overridden';
  };

  clear.onclick = () => {
    srcPath = null;
    srcBtn.classList.add('empty');
    srcBtn.textContent = 'Choose source font…';
    inst.disabled = true;
    inst.innerHTML = '<option>— pick instrument —</option>';
    onClear();
    tag.textContent = '';
  };

  ov.append(srcBtn, inst, tag, clear);

  // Pre-populate from a loaded setup: select the saved source font + instrument and apply it.
  if (initial && initial.sourcePath) {
    loadSource(initial.sourcePath, initial.sourcePath.split('/').pop()).then(cat => {
      if (!cat) return;
      inst.value = `${initial.sourceBank}:${initial.sourceProgram}`;
      if (inst.value) {
        onPick(initial.sourcePath, initial.sourceBank, initial.sourceProgram);
        tag.textContent = '→ overridden';
      }
    });
  }

  return ov;
}

// One row per track: the named instrument (e.g. "Violoncello") and what it currently sounds as,
// with an override picker that forces every note of that track to a chosen font's instrument.
function renderTracks(tracks) {
  const box = $('tracks');
  box.innerHTML = '';

  for (const t of tracks) {
    const silent = t.channels.length === 0;
    const row = document.createElement('div');
    row.className = 'patch' + (silent ? ' silent' : '');

    const head = document.createElement('div');
    head.className = 'head';
    const name = t.name && t.name.trim() ? t.name : `Track ${t.trackIndex}`;
    let sub;
    if (silent) {
      sub = 'no notes';
    } else {
      const chans = t.channels.map(c => c + 1).join(', ');   // show 1-based channels
      const progs = t.programs.join(', ');
      sub = `${t.baseName ?? '(not in base font)'} · ch ${chans} · prog ${progs}`;
    }
    head.innerHTML = `<span class="track-name">${escapeHtml(name)}</span>`
      + `<span class="track-sub">${escapeHtml(sub)}</span>`;
    row.appendChild(head);

    if (!silent) {
      row.appendChild(buildPicker(
        (srcPath, sb, sp) => trackOverrides.set(t.trackIndex, {
          trackIndex: t.trackIndex, trackName: t.name,
          sourcePath: srcPath, sourceBank: sb, sourceProgram: sp,
        }),
        () => trackOverrides.delete(t.trackIndex),
        'revert this instrument to the base font',
        loadedTrackOverrides && loadedTrackOverrides.get(t.trackIndex)));
    }
    box.appendChild(row);
  }
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

    row.appendChild(buildPicker(
      (srcPath, sb, sp) => overrides.set(key, {
        logicalBank: p.bank, logicalProgram: p.program,
        sourcePath: srcPath, sourceBank: sb, sourceProgram: sp,
      }),
      () => overrides.delete(key),
      'revert this patch to the base font',
      loadedOverrides && loadedOverrides.get(key)));
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
    trackOverrides: [...trackOverrides.values()],
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

// ---------------- setups ----------------
async function refreshSetups() {
  const list = $('setupList');
  if (!selectedMidi) { list.innerHTML = '<option value="">— saved setups —</option>'; return; }
  let items = [];
  try { items = await fetch(`/api/setups?midiPath=${encodeURIComponent(selectedMidi.path)}`).then(r => r.json()); } catch {}
  list.innerHTML = '<option value="">— saved setups —</option>'
    + (items || []).map(s => `<option value="${s.id}">${escapeHtml(s.name)}</option>`).join('');
}

async function saveSetup() {
  if (!selectedMidi || !selectedSoundfont) {
    $('error').textContent = 'Choose a MIDI file and a base SoundFont first.'; return;
  }
  const name = prompt('Save setup as:', lastSetupName || selectedMidi.name.replace(/\.midi?$/i, ''));
  if (!name) return;
  const body = {
    name, midiPath: selectedMidi.path, midiName: selectedMidi.name,
    soundfontPath: selectedSoundfont.path, soundfontName: selectedSoundfont.name,
    overrides: [...overrides.values()], trackOverrides: [...trackOverrides.values()],
  };
  let res;
  try {
    res = await fetch('/api/setups', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body),
    }).then(r => r.json());
  } catch (e) { $('error').textContent = 'Save failed: ' + e; return; }
  lastSetupName = name;
  await refreshSetups();
  if (res && res.id) $('setupList').value = res.id;
}

async function loadSetup(id) {
  let setup;
  try { setup = await fetch(`/api/setups/${id}`).then(r => r.json()); }
  catch (e) { $('error').textContent = 'Load failed: ' + e; return; }
  if (!setup || setup.error || !setup.soundfontPath) { $('error').textContent = 'Could not load that setup.'; return; }

  selectedSoundfont = { name: setup.soundfontName || setup.soundfontPath.split('/').pop(), path: setup.soundfontPath };
  $('sfPick').classList.remove('empty'); $('sfPick').textContent = selectedSoundfont.name;
  // Stash the saved overrides so the pickers render pre-selected, then analyze rebuilds the UI.
  loadedOverrides = new Map((setup.overrides || []).map(o => [`${o.logicalBank}:${o.logicalProgram}`, o]));
  loadedTrackOverrides = new Map((setup.trackOverrides || []).map(o => [o.trackIndex, o]));
  lastSetupName = setup.name || lastSetupName;
  await analyze();
  loadedOverrides = null; loadedTrackOverrides = null;
}

async function deleteSetup() {
  const id = $('setupList').value;
  if (!id || !confirm('Delete this setup?')) return;
  try { await fetch(`/api/setups/${id}`, { method: 'DELETE' }); } catch {}
  await refreshSetups();
}

// Revert every instrument in the song to the base font.
function resetAll() {
  if (!selectedMidi || !selectedSoundfont) return;
  overrides.clear();
  trackOverrides.clear();
  loadedOverrides = null; loadedTrackOverrides = null;
  analyze();
}

// ---------------- wiring ----------------
$('midiPick').onclick = () => openBrowser('midi', f => {
  selectedMidi = f; $('midiPick').classList.remove('empty'); $('midiPick').textContent = f.name;
  resetAnalysis(); refreshSetups();
});
$('sfPick').onclick = () => openBrowser('sf', f => {
  selectedSoundfont = f; $('sfPick').classList.remove('empty'); $('sfPick').textContent = f.name; resetAnalysis();
});
$('analyze').onclick = analyze;
$('play').onclick = play;
$('stop').onclick = () => fetch('/api/stop', { method: 'POST' });
$('exit').onclick = () => fetch('/api/exit', { method: 'POST' });
$('saveSetup').onclick = saveSetup;
$('loadSetup').onclick = () => { const id = $('setupList').value; if (id) loadSetup(id); };
$('delSetup').onclick = deleteSetup;
$('resetAll').onclick = resetAll;
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
