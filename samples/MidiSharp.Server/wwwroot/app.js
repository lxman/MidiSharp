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
const overrides = new Map();      // "bank:program" -> patch override DTO (legacy patch swaps)
const partOverrides = new Map();  // "track:channel" -> part override DTO (the strips' `src`)
const catalogCache = new Map();   // font path -> catalog
// When a setup is being loaded, these hold its overrides so the pickers render pre-populated.
let loadedOverrides = null;       // "bank:program" -> override DTO  (null when not loading a setup)
let loadedPartOverrides = null;   // "track:channel" -> override DTO  (the per-part substitutions)
let loadedTrackOverrides = null;  // trackIndex -> legacy whole-track override (migrated onto its part)
let lastSetupName = '';
let availablePlugins = [];        // discovered hosted effect plugins (CLAP/LADSPA), for the rack picker
let availableInstruments = [];    // discovered hosted instrument plugins, for the per-part `src` picker
const instrumentBindings = new Map();   // channel -> { channel, format, id }  (part played by a plugin synth)
let loadedInstruments = null;     // a loaded setup's bindings, migrated onto the working map as strips render

// Mixer (per-instrument, keyed "bank:program") + master limiter. Only non-default strips are kept in
// mixMap so an untouched mixer stays empty; the browser is the source of truth and re-sends on Play.
const mixMap = new Map();         // "bank:program" -> { bank, program, gainDb, pan, mute, solo, reverbSend, chorusSend }
// Fixed master-EQ band layout (low shelf, 3 peaks, high shelf). The browser always sends all five;
// a 0 dB band is transparent. Gain is the editable parameter (freq/Q fixed for this graphic EQ).
const EQ_DEFS = [
  { type: 'lowshelf',  freqHz: 100,  q: 0.707, label: '100' },
  { type: 'peaking',   freqHz: 300,  q: 1.0,   label: '300' },
  { type: 'peaking',   freqHz: 1000, q: 1.0,   label: '1k' },
  { type: 'peaking',   freqHz: 3500, q: 1.0,   label: '3.5k' },
  { type: 'highshelf', freqHz: 8000, q: 0.707, label: '8k' },
];
const freshEqBands = () => EQ_DEFS.map(d => ({ type: d.type, freqHz: d.freqHz, q: d.q, gainDb: 0 }));
// Master bus is now an ordered effect rack: master.effects = [{ type, enabled, ...params }]. Populated
// from DEFAULT_MASTER_EFFECTS once the registry below is defined; the array reference is stable (the
// rack binds to it), so loads mutate it in place.
const master = { effects: [], masterGainDb: 0 };
let masterRackPanel = null;       // the master EQ/limiter rack, lives in a popover off the master strip
let refreshMasterBadge = () => {};
let loadedMix = null;             // "bank:program" -> mix DTO, while a setup is loading
let isPlaying = false;            // from the status socket — live mixer POSTs only matter while playing

// ---------------- output device picker ----------------
// /api/devices is a flat list, each device tagged with an `engine` (e.g. "Portaudio.Windows WASAPI").
// On Windows, PortAudio enumerates every host API (MME/DirectSound/WASAPI/WDM-KS), so each physical
// device appears once per API. When more than one host API is present we show a two-stage picker
// (host API → device) defaulting to WASAPI; otherwise (Linux PipeWire sinks, macOS Core Audio, or any
// single-API box) we keep one flat dropdown. macOS gets the two-stage UI automatically only if its
// PortAudio ever exposes multiple host APIs. Selection always resolves server-side by device id, so
// what we show here never affects which device actually opens.
let allDevices = [];

// Friendlier host-API label: "Portaudio.Windows WASAPI" → "WASAPI", "Portaudio.MME" → "MME".
const hostApiLabel = engine => ((engine || '').split('.').pop().replace(/^Windows\s+/, '') || engine || 'Audio');
// Preferred order: WASAPI first (modern default), then DirectSound, MME, WDM-KS, then anything else.
const HOST_API_RANK = { WASAPI: 0, DirectSound: 1, MME: 2, 'WDM-KS': 3 };
const hostApiRank = engine => HOST_API_RANK[hostApiLabel(engine)] ?? 9;

// The host-API <select> normally lives in index.html, but create it on the fly if it's absent —
// a browser may have a stale cached index.html while running fresh app.js, and we don't want that
// mismatch to break device loading entirely.
function ensureHostApiSelect() {
  let host = $('hostApi');
  if (!host) {
    host = document.createElement('select');
    host.id = 'hostApi';
    host.hidden = true;
    host.style.marginBottom = '.4rem';
    const dev = $('device');
    if (dev && dev.parentNode) dev.parentNode.insertBefore(host, dev);
    else document.body.appendChild(host);
  }
  return host;
}

async function loadDevices() {
  allDevices = await fetch('/api/devices').then(r => r.json());

  const engines = [...new Set(allDevices.map(d => d.engine || ''))].sort((a, b) => hostApiRank(a) - hostApiRank(b));
  const hostSel = ensureHostApiSelect();
  const twoStage = engines.length > 1;
  hostSel.hidden = !twoStage;

  if (!twoStage) { fillDeviceOptions(allDevices); return; }

  hostSel.innerHTML = '';
  for (const eng of engines) {
    const o = document.createElement('option');
    o.value = eng;
    o.textContent = hostApiLabel(eng);
    hostSel.appendChild(o);
  }
  // Restore the saved host API if still present; otherwise the top-ranked one (WASAPI when available).
  const savedApi = localStorage.getItem('outputHostApi');
  hostSel.value = (savedApi && engines.includes(savedApi)) ? savedApi : engines[0];
  localStorage.setItem('outputHostApi', hostSel.value);
  hostSel.onchange = () => {
    localStorage.setItem('outputHostApi', hostSel.value);
    fillDeviceOptions(allDevices.filter(d => (d.engine || '') === hostSel.value));
  };
  fillDeviceOptions(allDevices.filter(d => (d.engine || '') === hostSel.value));
}

// Fill the device <select> from a device list and pick a sensible selection: the last-used device if
// it's in this list, else this host's own default, else the device matching the system default by name
// (the global default may live under another host API), else the first. Persist the result so the
// dropdown, localStorage, and the next Play request all agree.
function fillDeviceOptions(devices) {
  const dev = $('device');
  dev.innerHTML = '';
  for (const d of devices) {
    const o = document.createElement('option');
    o.value = d.id;
    o.textContent = d.name + (d.isDefault ? '  (default)' : '');
    dev.appendChild(o);
  }
  const saved = localStorage.getItem('outputDeviceId');
  const globalDefaultName = allDevices.find(d => d.isDefault)?.name;
  const pick = devices.find(d => d.id === saved)
    || devices.find(d => d.isDefault)
    || devices.find(d => d.name === globalDefaultName)
    || devices[0];
  if (pick) { dev.value = pick.id; localStorage.setItem('outputDeviceId', pick.id); }
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
  const DRIVES = '::drives';
  const crumbs = $('bwCrumbs');
  crumbs.innerHTML = '';
  const addCrumb = (label, full) => {
    const a = document.createElement('a');
    a.textContent = label;
    a.onclick = () => browseTo(full);
    crumbs.appendChild(a);
  };
  const addSep = () => {
    const sep = document.createElement('span'); sep.className = 'sep'; sep.textContent = '›';
    crumbs.appendChild(sep);
  };
  if (d.path === DRIVES) {
    addCrumb('Drives', DRIVES);
  } else {
    // Windows paths use backslashes and a drive-letter root (C:\); POSIX paths use '/'.
    const win = /^[A-Za-z]:/.test(d.path) || d.path.includes('\\');
    const sepCh = win ? '\\' : '/';
    addCrumb(win ? 'Drives' : '/', win ? DRIVES : '/');
    let acc = '';
    const segs = d.path.split(/[\\/]+/).filter(Boolean);
    segs.forEach((seg, i) => {
      // First Windows segment is the drive ("C:" → "C:\"); thereafter join with the separator.
      acc = i === 0 ? (win ? seg + '\\' : '/' + seg) : acc.replace(/[\\/]+$/, '') + sepCh + seg;
      addSep();
      addCrumb(seg, acc);
    });
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
  partOverrides.clear();
  instrumentBindings.clear();
  mixMap.clear();
  $('mixer').innerHTML = '';
  $('masterStrip').innerHTML = '';
  $('mixerHint').style.display = '';
  $('mixerCount').textContent = '';
  $('trackHint').textContent = '';
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
  let parts;
  try {
    parts = await fetch(`/api/parts?${qs}`).then(r => r.json());
  } catch (e) {
    $('trackHint').textContent = '';
    $('error').textContent = 'Analyze failed: ' + e;
    return;
  }
  if (parts.error) {
    $('trackHint').textContent = '';
    $('error').textContent = 'Analyze failed: ' + parts.error;
    return;
  }

  $('trackHint').textContent = `${parts.length} part${parts.length === 1 ? '' : 's'}`;

  // Seed the mixer from a loading setup, then render one strip per part — a (track, channel), which the
  // engine mixes by Synthesizer.TrackPart(track, channel).
  if (loadedMix) for (const [k, m] of loadedMix) mixMap.set(k, m);
  renderMixer(parts);
  $('mixerCount').textContent = `${parts.length} part${parts.length === 1 ? '' : 's'}`;
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


// ---------------- mixer + master ----------------
// One persistent entry per used instrument (created on render, never pruned) so the strip controls and
// its insert rack bind to stable objects. Only non-default entries are sent on Play / saved into setups.
const partKey = p => `${p.trackIndex}:${p.channel}`;   // the (track, channel) mixMap key
const newMixEntry = p => ({
  trackIndex: p.trackIndex, channel: p.channel,
  gainDb: 0, pan: 0, mute: false, solo: false, reverbSend: 0, chorusSend: 0, inserts: [],
});
const isNonDefaultMix = m =>
  m.gainDb !== 0 || m.pan !== 0 || m.mute || m.solo || m.reverbSend !== 0 || m.chorusSend !== 0
  || (m.inserts && m.inserts.length > 0);

// Throttled live POSTs (one timer per key) so dragging a slider doesn't flood the server: fire at once
// if it's been a while, else schedule a trailing send so the final value always lands.
const liveTimers = new Map();
// `always` sends even when stopped — used for inserts, which load their plugin server-side (so its editor
// can be opened without a song playing); mix gain/pan changes have no engine to apply to when stopped.
function postLive(url, key, body, always) {
  if (!isPlaying && !always) return;
  const rec = liveTimers.get(key) || { t: 0, last: 0 };
  const now = performance.now();
  clearTimeout(rec.t);
  const send = () => { rec.last = performance.now(); rec.t = 0; fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }).catch(() => {}); };
  if (now - rec.last >= 50) { rec.last = now; rec.t = 0; send(); }
  else rec.t = setTimeout(send, 50 - (now - rec.last));
  liveTimers.set(key, rec);
}
const postMixLive = e => postLive('/api/mix', 'mix:' + partKey(e), e);
const postInsertLive = e => postLive('/api/insert', 'ins:' + partKey(e),
  { trackIndex: e.trackIndex, channel: e.channel, effects: e.inserts }, true);
function postMaster() {
  fetch('/api/master', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(master) }).catch(() => {});
}

function renderMixer(parts) {
  const box = $('mixer');
  box.innerHTML = '';
  $('masterStrip').innerHTML = '';
  closePopover();
  document.querySelectorAll('body > .pop:not(.master-pop)').forEach(p => p.remove());   // drop per-strip popovers from a prior render (keep the master rack)
  if (!parts || !parts.length) { $('mixerHint').style.display = ''; return; }
  $('mixerHint').style.display = 'none';
  for (const p of parts) {
    const key = partKey(p);
    let e = mixMap.get(key);
    if (!e) {
      // Migrate a legacy per-track mix entry (older setups keyed mix by trackIndex only) onto this part.
      const legacy = mixMap.get(String(p.trackIndex));
      if (legacy) { mixMap.delete(String(p.trackIndex)); e = legacy; } else e = newMixEntry(p);
      mixMap.set(key, e);
    }
    e.trackIndex = p.trackIndex; e.channel = p.channel;   // pin the part identity
    if (!Array.isArray(e.inserts)) e.inserts = [];        // normalize entries loaded from a setup
    box.appendChild(buildStrip(p, e));
  }
  $('masterStrip').appendChild(buildMasterStrip());     // master pinned on the right
}

// The master channel strip: an FX button (opens the master EQ/limiter rack popover) + a master fader.
function buildMasterStrip() {
  const strip = document.createElement('div');
  strip.className = 'vstrip master';

  const name = document.createElement('div'); name.className = 'vname';
  name.innerHTML = 'MASTER<span class="vaddr">bus</span>';

  const head = document.createElement('div'); head.className = 'vhead';
  const fxBtn = document.createElement('button'); fxBtn.className = 'solo-w'; fxBtn.title = 'master EQ / limiter';
  head.append(fxBtn);
  refreshMasterBadge = () => {
    const n = master.effects.filter(e => e.enabled).length;
    fxBtn.textContent = n ? `FX ${n}` : 'FX';
    fxBtn.classList.toggle('on', n > 0);
  };
  refreshMasterBadge();
  fxBtn.onclick = () => togglePopover(fxBtn, masterRackPanel);

  const fader = document.createElement('div'); fader.className = 'vfader';
  const gIn = document.createElement('input');
  gIn.type = 'range'; gIn.min = -36; gIn.max = 6; gIn.step = 0.5; gIn.value = master.masterGainDb;
  const gfmt = v => `${v > 0 ? '+' : ''}${v.toFixed(1)} dB`;
  const gVal = document.createElement('div'); gVal.className = 'vgain'; gVal.textContent = gfmt(master.masterGainDb);
  fader.append(gIn, gVal);
  gIn.addEventListener('input', () => { master.masterGainDb = parseFloat(gIn.value); gVal.textContent = gfmt(master.masterGainDb); postMaster(); });

  strip.append(name, head, fader);
  return strip;
}

// ---- popovers (source-font picker / FX rack) — wide panels that float off a narrow strip ----
let openPop = null;
function closePopover() {
  if (!openPop) return;
  openPop.panel.classList.remove('pop-open');
  openPop = null;
}
function togglePopover(btn, panel) {
  if (openPop && openPop.panel === panel) { closePopover(); return; }
  closePopover();
  if (panel.parentElement !== document.body) document.body.appendChild(panel);
  panel.classList.add('pop-open');           // display:block so we can measure it
  panel.style.maxHeight = '';                  // measure full content height first
  const r = btn.getBoundingClientRect();
  let left = Math.min(r.left, window.innerWidth - panel.offsetWidth - 8);
  let top = r.bottom + 4;
  if (top + panel.offsetHeight > window.innerHeight - 8) top = Math.max(8, r.top - panel.offsetHeight - 4);
  panel.style.left = Math.max(8, left) + 'px';
  panel.style.top = top + 'px';
  // Cap the panel to the space from its top to the viewport bottom so a tall plugin rack scrolls inside the
  // popover instead of running off-screen (where the buttons below it are unreachable).
  panel.style.maxHeight = Math.max(120, window.innerHeight - top - 8) + 'px';
  openPop = { panel, btn };
}
document.addEventListener('click', e => {
  if (!openPop) return;
  if (openPop.panel.contains(e.target) || openPop.btn.contains(e.target)) return;
  closePopover();
});
window.addEventListener('resize', closePopover);

// A vertical channel strip: name, source/FX header buttons (open popovers), pan + sends, mute/solo,
// and a vertical gain fader. Same persistent entry + live posting as before.
function buildStrip(p, e) {
  const strip = document.createElement('div');
  strip.className = 'vstrip' + (p.isDrum ? ' drum' : '');

  // A strip IS a part = a (track, channel). Name is resolved server-side (track name when it uniquely
  // names the part, else the GM program name); `sound` is the program info underneath.
  const name = document.createElement('div');
  name.className = 'vname';
  name.title = `${p.name}${p.sound ? ' — ' + p.sound : ''}`;
  name.innerHTML = `${escapeHtml(p.name)}<span class="vaddr">${escapeHtml(p.sound || '')}</span>`;

  // header: src + FX buttons, each opening a floating popover
  const head = document.createElement('div'); head.className = 'vhead';
  const srcBtn = document.createElement('button'); srcBtn.textContent = 'src';
  const fxBtn = document.createElement('button'); fxBtn.textContent = 'FX'; fxBtn.title = 'insert effects';
  head.append(srcBtn, fxBtn);

  // src popover → per-part override keyed by (track, channel), so each part substitutes on its own —
  // including the several instruments that share one track in a format-0 file. A loaded setup's
  // per-part override pre-populates the picker; a legacy whole-track override migrates onto its part
  // (when its picker fires onPick during init, it's re-recorded as a part override).
  const pk = partKey(p);
  srcBtn.title = 'assign a font to this part';
  const srcPop = document.createElement('div'); srcPop.className = 'pop src';
  const loadedSub = (loadedPartOverrides && loadedPartOverrides.get(pk))
    || (loadedTrackOverrides && loadedTrackOverrides.get(p.trackIndex));
  srcPop.appendChild(buildPicker(
    (srcPath, sb, sp) => { partOverrides.set(pk, { trackIndex: p.trackIndex, channel: p.channel, partName: p.name, sourcePath: srcPath, sourceBank: sb, sourceProgram: sp }); srcBtn.classList.add('on'); },
    () => { partOverrides.delete(pk); srcBtn.classList.remove('on'); },
    'revert this part to its original sound',
    loadedSub));
  if (loadedSub) srcBtn.classList.add('on');

  // …or play the part with a hosted plugin instrument instead of the SoundFont. Bound per channel: the
  // synth is muted on that channel and the plugin renders its notes. Applied on the next Play.
  if (availableInstruments.length) {
    const hint = document.createElement('div'); hint.className = 'hint'; hint.textContent = '— or play with a plugin instrument —';
    const isel = document.createElement('select'); isel.className = 'instsel';
    isel.innerHTML = '<option value="">(SoundFont)</option>'
      + availableInstruments.map((x, i) => `<option value="${i}">${escapeHtml(x.name)} · ${x.format}</option>`).join('');
    // A loaded setup's binding lives in loadedInstruments until its strip renders; migrate it onto the
    // working map here so Play/Save include it even if the user never touches the selector.
    let bound = instrumentBindings.get(p.channel);
    if (!bound && loadedInstruments && loadedInstruments.has(p.channel)) {
      bound = loadedInstruments.get(p.channel);
      instrumentBindings.set(p.channel, bound);
    }
    if (bound) {
      const idx = availableInstruments.findIndex(x => x.format === bound.format && x.id === bound.id);
      if (idx >= 0) { isel.value = String(idx); srcBtn.classList.add('inst'); }
    }
    isel.addEventListener('change', () => {
      if (isel.value === '') { instrumentBindings.delete(p.channel); srcBtn.classList.remove('inst'); }
      else { const x = availableInstruments[+isel.value]; instrumentBindings.set(p.channel, { channel: p.channel, format: x.format, id: x.id }); srcBtn.classList.add('inst'); }
    });
    srcPop.append(hint, isel);
  }

  srcBtn.onclick = () => togglePopover(srcBtn, srcPop);

  // insert-rack popover (the SAME buildRack/widgets as the master bus)
  const fxPop = document.createElement('div'); fxPop.className = 'pop fxpop rack';
  const updateFx = () => { fxBtn.textContent = e.inserts.length ? `FX ${e.inserts.length}` : 'FX'; fxBtn.classList.toggle('on', e.inserts.length > 0); };
  buildRack(fxPop, e.inserts, () => { updateFx(); postInsertLive(e); });
  updateFx();
  fxBtn.onclick = () => togglePopover(fxBtn, fxPop);

  // pan + reverb/chorus sends (compact horizontal mini-sliders)
  const pan = vsend('pan', -1, 1, 0.05, e.pan, v => v === 0 ? 'C' : (v < 0 ? `L${Math.round(-v * 100)}` : `R${Math.round(v * 100)}`));
  const rev = vsend('rev', 0, 1, 0.05, e.reverbSend, v => v.toFixed(2));
  const cho = vsend('cho', 0, 1, 0.05, e.chorusSend, v => v.toFixed(2));

  const ms = document.createElement('div'); ms.className = 'vms';
  const mute = toggle('M', 'mute', e.mute);
  const solo = toggle('S', 'solo', e.solo);
  ms.append(mute.el, solo.el);

  // vertical gain fader
  const fader = document.createElement('div'); fader.className = 'vfader';
  const gainIn = document.createElement('input');
  gainIn.type = 'range'; gainIn.min = -36; gainIn.max = 12; gainIn.step = 0.5; gainIn.value = e.gainDb;
  const gfmt = v => `${v > 0 ? '+' : ''}${v.toFixed(1)} dB`;
  const gainVal = document.createElement('div'); gainVal.className = 'vgain'; gainVal.textContent = gfmt(e.gainDb);
  fader.append(gainIn, gainVal);

  const onMix = () => {
    e.gainDb = parseFloat(gainIn.value); e.pan = pan.value(); e.reverbSend = rev.value(); e.chorusSend = cho.value();
    e.mute = mute.on(); e.solo = solo.on();
    postMixLive(e);
  };
  gainIn.addEventListener('input', () => { gainVal.textContent = gfmt(parseFloat(gainIn.value)); onMix(); });
  pan.oninput(onMix); rev.oninput(onMix); cho.oninput(onMix);
  mute.onclick(onMix); solo.onclick(onMix);

  strip.append(name, head, pan.row, rev.row, cho.row, ms, fader);
  return strip;
}

// A compact labelled mini-slider for a vertical strip (pan / sends). Returns the row + accessors.
function vsend(label, min, max, step, value, fmt) {
  const row = document.createElement('div'); row.className = 'vsend';
  const l = document.createElement('label'); l.textContent = label;
  const input = document.createElement('input');
  input.type = 'range'; input.min = min; input.max = max; input.step = step; input.value = value;
  const v = document.createElement('span'); v.className = 'v'; v.textContent = fmt(value);
  row.append(l, input, v);
  return {
    row,
    value: () => parseFloat(input.value),
    oninput: cb => input.addEventListener('input', () => { v.textContent = fmt(parseFloat(input.value)); cb(); }),
  };
}

// A labelled range control. fmt(value) renders the readout; extraClass tightens narrow controls.
function knob(label, min, max, step, value, fmt, extraClass) {
  const el = document.createElement('span');
  el.className = 'knob' + (extraClass ? ' ' + extraClass : '');
  const input = document.createElement('input');
  input.type = 'range'; input.min = min; input.max = max; input.step = step; input.value = value;
  const val = document.createElement('span');
  val.className = 'val';
  val.textContent = fmt(value);
  el.append(document.createTextNode(label + ' '), input, val);
  return {
    el,
    value: () => parseFloat(input.value),
    oninput: cb => input.addEventListener('input', () => { val.textContent = fmt(parseFloat(input.value)); cb(); }),
  };
}

function toggle(text, cls, on) {
  const b = document.createElement('button');
  b.className = 'ms ' + cls + (on ? ' on' : '');
  b.textContent = text;
  let state = on;
  return {
    el: b,
    on: () => state,
    onclick: cb => b.addEventListener('click', () => { state = !state; b.classList.toggle('on', state); cb(); }),
  };
}

// ---- effect-widget registry ----
// Each effect is a reusable "precut" widget: create() builds its model object, body() renders its
// editor bound to that model and calls onChange() on every edit. New effect types (compressor, reverb,
// …) register here and the rack, palette, save/load and (later) per-instrument inserts pick them up.
const eqFmt = v => `${v > 0 ? '+' : ''}${v.toFixed(1)}`;

const EFFECTS = {
  eq: {
    title: 'EQ',
    create: () => ({ type: 'eq', enabled: true, eqBands: freshEqBands() }),
    body: buildEqBody,
  },
  limiter: {
    title: 'Limiter',
    create: () => ({ type: 'limiter', enabled: true, ceilingDb: -1.0, releaseMs: 100 }),
    body: buildLimiterBody,
  },
  // Hosted plugin (CLAP/LADSPA). Not added from the static palette — picked from the discovered list in
  // the rack's add-bar, which fills in pluginFormat/pluginId/name. create() is just a safe skeleton.
  plugin: {
    title: 'Plugin',
    create: () => ({ type: 'plugin', enabled: true, pluginFormat: '', pluginId: '', instanceId: '', name: 'Plugin', pluginParams: [], params: null }),
    body: buildPluginBody,
  },
};
const EFFECT_PALETTE = ['eq', 'limiter'];   // offer-to-add order
// The master rack starts with EQ + limiter present but bypassed (discoverable, off until you enable).
const defaultMasterEffects = () =>
  [{ ...EFFECTS.eq.create(), enabled: false }, { ...EFFECTS.limiter.create(), enabled: false }];
// Fill any params missing from a saved/normalised effect with that type's defaults.
const normalizeEffect = e => (EFFECTS[e.type] ? { ...EFFECTS[e.type].create(), ...e } : e);

function buildEqBody(fx, onChange) {
  const wrap = document.createElement('div'); wrap.className = 'eq';
  EQ_DEFS.forEach((d, i) => {
    const col = document.createElement('div'); col.className = 'eqband';
    const g = document.createElement('span'); g.className = 'g'; g.textContent = eqFmt(fx.eqBands[i].gainDb);
    const inp = document.createElement('input');
    inp.type = 'range'; inp.min = -12; inp.max = 12; inp.step = 0.5; inp.value = fx.eqBands[i].gainDb;
    const lbl = document.createElement('span'); lbl.className = 'lbl'; lbl.textContent = d.label;
    inp.addEventListener('input', () => { fx.eqBands[i].gainDb = parseFloat(inp.value); g.textContent = eqFmt(fx.eqBands[i].gainDb); onChange(); });
    col.append(g, inp, lbl); wrap.appendChild(col);
  });
  return wrap;
}

function buildLimiterBody(fx, onChange) {
  const wrap = document.createElement('div'); wrap.className = 'fxrow';
  const mk = (label, min, max, step, get, set, fmt) => {
    const k = document.createElement('span'); k.className = 'knob';
    const inp = document.createElement('input'); inp.type = 'range'; inp.min = min; inp.max = max; inp.step = step; inp.value = get();
    const val = document.createElement('span'); val.className = 'val'; val.textContent = fmt(get());
    inp.addEventListener('input', () => { set(parseFloat(inp.value)); val.textContent = fmt(get()); onChange(); });
    k.append(document.createTextNode(label + ' '), inp, val); return k;
  };
  wrap.append(
    mk('ceiling', -12, 0, 0.5, () => fx.ceilingDb, v => fx.ceilingDb = v, v => `${v.toFixed(1)} dB`.replace('-', '−')),
    mk('release', 10, 500, 10, () => fx.releaseMs, v => fx.releaseMs = v, v => `${v | 0} ms`),
  );
  return wrap;
}

// A hosted plugin's editor: one normalized 0..1 knob per parameter. The param list is fetched lazily
// (it needs the plugin loaded server-side) and the body re-renders once it arrives — so this works both
// for a freshly-added plugin and one restored from a saved setup.
function buildPluginBody(fx, onChange) {
  const wrap = document.createElement('div'); wrap.className = 'fxrow plugin';

  function renderParams() {
    wrap.innerHTML = '';
    if (!fx.params) { wrap.innerHTML = '<span class="hint">loading…</span>'; return; }
    if (!fx.params.length) { wrap.innerHTML = '<span class="hint">no parameters</span>'; return; }
    fx.params.forEach(p => {
      const k = document.createElement('span'); k.className = 'knob';
      const inp = document.createElement('input'); inp.type = 'range'; inp.min = 0; inp.max = 1; inp.step = 0.001;
      const cur = (fx.pluginParams && fx.pluginParams[p.index] != null) ? fx.pluginParams[p.index] : p.defaultNormalized;
      inp.value = cur;
      const val = document.createElement('span'); val.className = 'val'; val.textContent = (+cur).toFixed(2);
      inp.addEventListener('input', () => {
        fx.pluginParams[p.index] = parseFloat(inp.value);
        val.textContent = parseFloat(inp.value).toFixed(2);
        onChange();
      });
      k.append(document.createTextNode(p.name + ' '), inp, val);
      wrap.appendChild(k);
    });
    // Plugins with a native editor get an open/close button; the window opens on the server host (in the
    // sandbox worker that holds the live instance), so it's for local use.
    if (fx.hasEditor) wrap.appendChild(editorButton(fx));
  }

  renderParams();
  if (!fx.params) {
    fetch(`/api/plugin-info?format=${encodeURIComponent(fx.pluginFormat)}&id=${encodeURIComponent(fx.pluginId)}`)
      .then(r => r.json())
      .then(info => {
        if (info && Array.isArray(info.params)) {
          fx.params = info.params;
          fx.hasEditor = !!info.hasEditor;
          if (!fx.pluginParams || !fx.pluginParams.length) fx.pluginParams = info.params.map(p => p.defaultNormalized);
          renderParams();
        } else { wrap.innerHTML = '<span class="hint">unavailable</span>'; }
      })
      .catch(() => { wrap.innerHTML = '<span class="hint">load failed</span>'; });
  }
  return wrap;
}

// Open/close the plugin's native editor window on the server host. Tracks state on the button so a second
// click closes it. The instanceId ties to the live loaded insert.
function editorButton(fx) {
  const btn = document.createElement('button');
  btn.className = 'btn editor'; btn.type = 'button'; btn.textContent = 'Open editor';
  let open = false;
  btn.addEventListener('click', async () => {
    btn.disabled = true;
    try {
      if (!open) {
        const r = await fetch('/api/plugins/editor/open', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ instanceId: fx.instanceId, title: (fx.name || 'Plugin') + ' — ' + fx.pluginId }),
        }).then(r => r.json()).catch(() => ({ ok: false }));
        open = !!(r && r.ok);
        btn.textContent = open ? 'Close editor' : 'Open editor';
        if (!open) { btn.textContent = 'No editor / not loaded'; setTimeout(() => { btn.textContent = 'Open editor'; }, 1500); }
      } else {
        await fetch('/api/plugins/editor/close', {
          method: 'POST', headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ instanceId: fx.instanceId }),
        }).catch(() => {});
        open = false; btn.textContent = 'Open editor';
      }
    } finally { btn.disabled = false; }
  });
  return btn;
}

// ---- drag-reorderable effect rack ----
// Binds to `model` (an array of effect objects) and calls onChange() on any edit — param tweak, add,
// remove, enable/bypass, or reorder. Generic: the master bus uses it now, per-instrument inserts later.
function buildRack(container, model, onChange) {
  let dragFrom = -1;
  const present = type => model.some(f => f.type === type);

  function render() {
    container.innerHTML = '';
    model.forEach((fx, i) => container.appendChild(card(fx, i)));
    container.appendChild(addBar());
  }

  function card(fx, i) {
    const def = EFFECTS[fx.type];
    const el = document.createElement('div');
    el.className = 'fxcard' + (fx.enabled ? '' : ' off');

    const head = document.createElement('div'); head.className = 'fxhead';
    const grip = document.createElement('span'); grip.className = 'fxgrip'; grip.textContent = '⠿'; grip.title = 'drag to reorder';
    grip.draggable = true;
    grip.addEventListener('dragstart', e => { dragFrom = i; el.classList.add('dragging'); e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setDragImage(el, 12, 12); });
    grip.addEventListener('dragend', () => { dragFrom = -1; el.classList.remove('dragging'); });

    const title = document.createElement('span'); title.className = 'fxtitle';
    title.textContent = fx.type === 'plugin' ? (fx.name || 'Plugin') : (def ? def.title : fx.type);

    const en = document.createElement('button'); en.className = 'fxbtn en'; en.textContent = fx.enabled ? 'on' : 'off'; en.title = 'enable / bypass';
    en.addEventListener('click', () => { fx.enabled = !fx.enabled; render(); onChange(); });

    const collapse = document.createElement('button'); collapse.className = 'fxbtn'; collapse.textContent = '⌃'; collapse.title = 'collapse';
    const rm = document.createElement('button'); rm.className = 'fxbtn rm'; rm.textContent = '✕'; rm.title = 'remove';
    rm.addEventListener('click', () => { model.splice(i, 1); render(); onChange(); });

    head.append(grip, title, en, collapse, rm);

    const body = document.createElement('div'); body.className = 'fxbody';
    if (def) body.appendChild(def.body(fx, onChange));
    collapse.addEventListener('click', () => body.classList.toggle('collapsed'));

    el.append(head, body);

    // Whole card is a drop target; reorder happens on drop (no mid-drag re-render to fight the DnD).
    el.addEventListener('dragover', e => { if (dragFrom >= 0) { e.preventDefault(); el.classList.add('drop'); } });
    el.addEventListener('dragleave', () => el.classList.remove('drop'));
    el.addEventListener('drop', e => {
      e.preventDefault(); el.classList.remove('drop');
      if (dragFrom < 0 || dragFrom === i) return;
      const [m] = model.splice(dragFrom, 1); model.splice(i, 0, m); dragFrom = -1; render(); onChange();
    });
    return el;
  }

  function addBar() {
    const bar = document.createElement('div'); bar.className = 'fxadd';
    const avail = EFFECT_PALETTE.filter(t => !present(t));
    if (avail.length) {
      const sel = document.createElement('select');
      sel.innerHTML = '<option value="">+ Add effect…</option>'
        + avail.map(t => `<option value="${t}">${EFFECTS[t].title}</option>`).join('');
      sel.addEventListener('change', () => { if (!sel.value) return; model.push(EFFECTS[sel.value].create()); render(); onChange(); });
      bar.appendChild(sel);
    }
    // Discovered hosted plugins (indexed so duplicate plugin ids — common in DISTRHO bundles — stay distinct).
    if (availablePlugins.length) {
      const psel = document.createElement('select');
      psel.innerHTML = '<option value="">+ Add plugin…</option>'
        + availablePlugins.map((p, i) => `<option value="${i}">${escapeHtml(p.name)} · ${p.format}</option>`).join('');
      psel.addEventListener('change', () => {
        if (psel.value === '') return;
        const p = availablePlugins[+psel.value];
        model.push({
          type: 'plugin', enabled: true, pluginFormat: p.format, pluginId: p.id,
          instanceId: (crypto.randomUUID ? crypto.randomUUID() : 'p' + Math.random().toString(36).slice(2)),
          name: p.name, pluginParams: [], params: null,
        });
        render(); onChange();
      });
      bar.appendChild(psel);
    }
    if (!avail.length && !availablePlugins.length) bar.innerHTML = '<span class="hint">all effects added</span>';
    return bar;
  }

  render();
  return { render };
}

// The master rack lives in a floating popover opened from the master strip's FX button. Built once;
// bound to master.effects (stable reference). The `.master-pop` class exempts it from popover cleanup.
master.effects.push(...defaultMasterEffects());
masterRackPanel = document.createElement('div');
masterRackPanel.className = 'pop fxpop rack master-pop';
const masterRack = buildRack(masterRackPanel, master.effects, () => { postMaster(); refreshMasterBadge(); });

// Replace master.effects' contents in place (keeps the rack's binding) and redraw.
function setMasterEffects(effects) {
  master.effects.length = 0;
  master.effects.push(...effects);
  masterRack.render();
}

// Build the rack model from a saved setup's master block — the new effects[] if present, else map the
// legacy scalar fields, else the default (present-but-bypassed EQ + limiter).
function effectsFromSetupMaster(m) {
  if (!m) return defaultMasterEffects();
  if (Array.isArray(m.effects)) return m.effects.map(normalizeEffect);
  const eq = { ...EFFECTS.eq.create(), enabled: !!m.eqEnabled };
  if (Array.isArray(m.eqBands) && m.eqBands.length === EQ_DEFS.length) eq.eqBands = m.eqBands;
  const lim = { ...EFFECTS.limiter.create(), enabled: !!m.limiterEnabled };
  if (typeof m.ceilingDb === 'number') lim.ceilingDb = m.ceilingDb;
  if (typeof m.releaseMs === 'number') lim.releaseMs = m.releaseMs;
  return [eq, lim];
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
    partOverrides: [...partOverrides.values()],
    instruments: [...instrumentBindings.values()],
    mix: [...mixMap.values()].filter(isNonDefaultMix),
    master,
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
    overrides: [...overrides.values()], partOverrides: [...partOverrides.values()],
    instruments: [...instrumentBindings.values()],
    mix: [...mixMap.values()].filter(isNonDefaultMix), master,
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

  // Substitution: per-part overrides are edited on the strips' `src`. Legacy whole-track overrides are
  // mapped by trackIndex and migrated onto their part as each strip renders. Legacy patch-overrides
  // (older setups like brand51) are carried through passively so their sound swaps still apply on play.
  overrides.clear();
  for (const o of setup.overrides || []) overrides.set(`${o.logicalBank}:${o.logicalProgram}`, o);
  loadedPartOverrides = new Map((setup.partOverrides || []).map(o => [`${o.trackIndex}:${o.channel}`, o]));
  loadedTrackOverrides = new Map((setup.trackOverrides || []).map(o => [o.trackIndex, o]));
  // Per-part plugin-instrument bindings (channel -> binding). Staged in loadedInstruments; analyze()
  // clears the working map and each strip migrates its loaded binding back as it renders.
  loadedInstruments = new Map((setup.instruments || []).map(b => [b.channel, b]));
  // Per-part mix, keyed (track:channel). Older setups stored mix by trackIndex only (no channel) — key
  // those by the bare trackIndex so renderMixer can migrate them onto the matching part.
  loadedMix = new Map((setup.mix || []).map(m =>
    [m.channel !== undefined && m.channel !== null ? `${m.trackIndex}:${m.channel}` : `${m.trackIndex}`, m]));

  master.masterGainDb = (setup.master && typeof setup.master.masterGainDb === 'number') ? setup.master.masterGainDb : 0;
  setMasterEffects(effectsFromSetupMaster(setup.master));
  postMaster();   // push the loaded master rack to the engine (persists across plays)

  lastSetupName = setup.name || lastSetupName;
  await analyze();
  loadedOverrides = null; loadedPartOverrides = null; loadedTrackOverrides = null; loadedMix = null; loadedInstruments = null;
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
  partOverrides.clear();
  loadedOverrides = null; loadedPartOverrides = null; loadedTrackOverrides = null; loadedInstruments = null;
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
    isPlaying = playing;
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
// Discover hosted effect plugins (instruments can't be inserts) for the rack picker.
fetch('/api/plugins').then(r => r.json()).then(list => {
  availablePlugins = (list || []).filter(p => !p.isInstrument);
  availableInstruments = (list || []).filter(p => p.isInstrument);
  // The master rack was built at page load, before this list arrived, so its add-bar had no "+ Add plugin"
  // option yet — re-render now that plugins are known.
  masterRack.render();
}).catch(() => {});
connectStatus();
