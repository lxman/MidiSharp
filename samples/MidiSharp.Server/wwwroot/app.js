const $ = id => document.getElementById(id);
const fmt = s => {
  if (!isFinite(s) || s < 0) s = 0;
  const m = Math.floor(s / 60), sec = Math.floor(s % 60);
  return `${m}:${sec.toString().padStart(2, '0')}`;
};

async function loadOptions() {
  const [devices, midis, fonts] = await Promise.all([
    fetch('/api/devices').then(r => r.json()),
    fetch('/api/midi').then(r => r.json()),
    fetch('/api/soundfonts').then(r => r.json()),
  ]);

  const dev = $('device');
  dev.innerHTML = '';
  for (const d of devices) {
    const o = document.createElement('option');
    o.value = d.id;
    o.textContent = d.name + (d.isDefault ? '  (default)' : '');
    if (d.isDefault) o.selected = true;
    dev.appendChild(o);
  }

  fill($('midi'), midis);
  fill($('soundfont'), fonts);
}

function fill(sel, items) {
  sel.innerHTML = '';
  if (!items.length) {
    const o = document.createElement('option');
    o.textContent = '(none found — check the server root)';
    o.value = '';
    sel.appendChild(o);
    return;
  }
  for (const it of items) {
    const o = document.createElement('option');
    o.value = it.path;
    o.textContent = it.name;
    sel.appendChild(o);
  }
}

async function play() {
  $('error').textContent = '';
  $('defects').textContent = '';
  const body = {
    deviceId: $('device').value || null,
    midiPath: $('midi').value,
    soundfontPath: $('soundfont').value,
  };
  if (!body.midiPath || !body.soundfontPath) {
    $('error').textContent = 'Pick a MIDI file and a SoundFont first.';
    return;
  }
  const res = await fetch('/api/play', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  }).then(r => r.json());

  if (!res.ok) { $('error').textContent = 'Error: ' + res.error; return; }
  if (res.defects && res.defects.length)
    $('defects').textContent = 'Repaired this file:\n  ' + res.defects.join('\n  ');
}

$('play').onclick = play;
$('stop').onclick = () => fetch('/api/stop', { method: 'POST' });
$('exit').onclick = () => fetch('/api/exit', { method: 'POST' });

// Live status over WebSocket: state, playhead, completion.
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

loadOptions().catch(e => $('error').textContent = 'Failed to load options: ' + e);
connectStatus();
