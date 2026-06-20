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
function postLive(url, key, body) {
  if (!isPlaying) return;
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
  { trackIndex: e.trackIndex, channel: e.channel, effects: e.inserts });
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
  const r = btn.getBoundingClientRect();
  let left = Math.min(r.left, window.innerWidth - panel.offsetWidth - 8);
  let top = r.bottom + 4;
  if (top + panel.offsetHeight > window.innerHeight - 8) top = Math.max(8, r.top - panel.offsetHeight - 4);
  panel.style.left = Math.max(8, left) + 'px';
  panel.style.top = top + 'px';
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

  // src popover → per-track override. Only meaningful when this part's track has one channel (so the
  // override hits just this part); on a multi-channel/format-0 track it's disabled until per-part
  // substitution lands.
  if (p.canSubstitute) {
    srcBtn.title = 'assign a font to this part';
    const srcPop = document.createElement('div'); srcPop.className = 'pop src';
    srcPop.appendChild(buildPicker(
      (srcPath, sb, sp) => { trackOverrides.set(p.trackIndex, { trackIndex: p.trackIndex, trackName: p.name, sourcePath: srcPath, sourceBank: sb, sourceProgram: sp }); srcBtn.classList.add('on'); },
      () => { trackOverrides.delete(p.trackIndex); srcBtn.classList.remove('on'); },
      'revert this part to its original sound',
      loadedTrackOverrides && loadedTrackOverrides.get(p.trackIndex)));
    if (loadedTrackOverrides && loadedTrackOverrides.get(p.trackIndex)) srcBtn.classList.add('on');
    srcBtn.onclick = () => togglePopover(srcBtn, srcPop);
  } else {
    srcBtn.disabled = true;
    srcBtn.title = 'this part shares a track with others (e.g. format 0) — per-part substitution coming soon';
  }

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

    const title = document.createElement('span'); title.className = 'fxtitle'; title.textContent = def ? def.title : fx.type;

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
    if (!avail.length) { bar.innerHTML = '<span class="hint">all effects added</span>'; return bar; }
    const sel = document.createElement('select');
    sel.innerHTML = '<option value="">+ Add effect…</option>'
      + avail.map(t => `<option value="${t}">${EFFECTS[t].title}</option>`).join('');
    sel.addEventListener('change', () => { if (!sel.value) return; model.push(EFFECTS[sel.value].create()); render(); onChange(); });
    bar.appendChild(sel);
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
    trackOverrides: [...trackOverrides.values()],
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
    overrides: [...overrides.values()], trackOverrides: [...trackOverrides.values()],
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

  // Substitution: track-overrides are edited on the strips' `src`. Legacy patch-overrides (older setups
  // like brand51) are carried through passively so their sound swaps still apply on play, even though
  // the new track strips don't edit them.
  overrides.clear();
  for (const o of setup.overrides || []) overrides.set(`${o.logicalBank}:${o.logicalProgram}`, o);
  loadedTrackOverrides = new Map((setup.trackOverrides || []).map(o => [o.trackIndex, o]));
  // Per-part mix, keyed (track:channel). Older setups stored mix by trackIndex only (no channel) — key
  // those by the bare trackIndex so renderMixer can migrate them onto the matching part.
  loadedMix = new Map((setup.mix || []).map(m =>
    [m.channel !== undefined && m.channel !== null ? `${m.trackIndex}:${m.channel}` : `${m.trackIndex}`, m]));

  master.masterGainDb = (setup.master && typeof setup.master.masterGainDb === 'number') ? setup.master.masterGainDb : 0;
  setMasterEffects(effectsFromSetupMaster(setup.master));
  postMaster();   // push the loaded master rack to the engine (persists across plays)

  lastSetupName = setup.name || lastSetupName;
  await analyze();
  loadedOverrides = null; loadedTrackOverrides = null; loadedMix = null;
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
connectStatus();
