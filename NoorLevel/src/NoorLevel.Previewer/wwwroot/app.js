// NoorLevel Previewer: generate levels, tweak dials and parameters, orbit around or walk through
// them, export. The same page runs inside the WebView2 app and in a browser (noorlevel serve):
// both answer /api/... the same way.
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

const $ = (id) => document.getElementById(id);
const S = 1 / 32;                       // map units -> metres (one 32-unit cell = 1 m)
const host = window.chrome && window.chrome.webview ? window.chrome.webview : null;

// ---------------------------------------------------------------- state
let info = null;          // presets, dials, settings, formats
let level = null;         // the last /api/generate answer
let pinned = {};          // parameters the user set by hand
let mode = 'orbit';
let cutCeilings = true;
let showMap = true;

// ---------------------------------------------------------------- three.js
const canvas = $('c');
const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
renderer.outputColorSpace = THREE.SRGBColorSpace;
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x15131a);
scene.fog = new THREE.Fog(0x15131a, 30, 140);
const camera = new THREE.PerspectiveCamera(70, 1, 0.05, 1000);
camera.rotation.order = 'YXZ';
const orbit = new OrbitControls(camera, canvas);
orbit.enableDamping = true;
orbit.dampingFactor = 0.12;
scene.add(new THREE.HemisphereLight(0xdfe6ff, 0x3a2f28, 1.6));
const sun = new THREE.DirectionalLight(0xfff1dd, 1.1);
sun.position.set(0.4, 1, 0.25);
scene.add(sun);
const lamps = [];                        // a few point lights that follow the level's nearest lights
for (let i = 0; i < 6; i++) {
  const l = new THREE.PointLight(0xffd9a8, 0, 16, 1.2);
  scene.add(l);
  lamps.push(l);
}
let fogFar = 140;
let levelRoot = null;                    // the loaded .glb scene
const dyn = new THREE.Group();           // doors, lifts, ladders, keys, exit, lights
scene.add(dyn);
const playerMarker = new THREE.Mesh(new THREE.ConeGeometry(0.35, 1.2, 12), new THREE.MeshBasicMaterial({ color: 0x7be495 }));
playerMarker.rotation.z = Math.PI;
scene.add(playerMarker);

function gridTexture() {
  const c = document.createElement('canvas');
  c.width = c.height = 128;
  const g = c.getContext('2d');
  g.fillStyle = '#ffffff';
  g.fillRect(0, 0, 128, 128);
  g.fillStyle = '#d7d4dc';
  g.fillRect(0, 0, 64, 64);
  g.fillRect(64, 64, 64, 64);
  g.fillStyle = '#8f8a99';
  for (const p of [0, 64]) { g.fillRect(p, 0, 2, 128); g.fillRect(0, p, 128, 2); }
  const t = new THREE.CanvasTexture(c);
  t.wrapS = t.wrapT = THREE.RepeatWrapping;
  t.colorSpace = THREE.SRGBColorSpace;
  t.anisotropy = renderer.capabilities.getMaxAnisotropy();
  return t;
}
const grid = gridTexture();
const MAT_COLORS = {
  floor: 0x9a8b78, wall: 0xc4c2c9, ceiling: 0x6b6f7c, sky: 0x7fa6e8, door: 0xd24a3a, door_blue: 0x3a5fd2,
  secret: 0x9a74c8, trigger: 0x40e070, lift: 0xf2c033, ladder: 0x8c5a34, hazard_lava: 0xf05a14, hazard_slime: 0x5ad030,
  hazard_spikes: 0x6e2a22, water: 0x3a78c8, glass: 0x9fd4f0,
};
const mats = {};
function mat(name) {
  if (!mats[name]) {
    const m = new THREE.MeshLambertMaterial({ color: MAT_COLORS[name] ?? 0xaaaaaa, map: grid });
    if (name === 'trigger') { m.transparent = true; m.opacity = 0.35; m.depthWrite = false; m.emissive = new THREE.Color(0x1a6030); }
    if (name === 'sky') { m.map = null; m.emissive = new THREE.Color(0x30486a); }
    if (name === 'water' || name === 'glass') { m.transparent = true; m.opacity = name === 'water' ? 0.55 : 0.3; m.depthWrite = false; m.side = THREE.DoubleSide; m.map = null; }
    if (name === 'hazard_lava' || name === 'hazard_slime') m.emissive = new THREE.Color(name === 'hazard_lava' ? 0x802008 : 0x1c5010);
    mats[name] = m;
  }
  return mats[name];
}

// ---------------------------------------------------------------- API
async function api(path, body) {
  const r = await fetch(path, body === undefined ? {} : { method: 'POST', body: JSON.stringify(body), headers: { 'Content-Type': 'application/json' } });
  const j = await r.json();
  if (!r.ok) throw new Error(j.error || r.statusText);
  return j;
}

// ---------------------------------------------------------------- panel
function slider(parent, { name, label, min, max, step, value, fmt, onInput, onChange, pin }) {
  const wrap = document.createElement('div');
  wrap.className = 'slider';
  wrap.innerHTML = `<div class="top"><span>${label}</span><span><b></b>${pin ? '<i class="pin" title="Pinned: click to unpin">●</i>' : ''}</span></div><input type="range">`;
  const input = wrap.querySelector('input');
  Object.assign(input, { min, max, step, value });
  const out = wrap.querySelector('b');
  const pinEl = wrap.querySelector('.pin');
  const show = () => { out.textContent = fmt(+input.value); };
  input.addEventListener('input', () => { show(); onInput && onInput(+input.value); });
  input.addEventListener('change', () => onChange && onChange(+input.value));
  if (pinEl) pinEl.addEventListener('click', () => pin(wrap));
  show();
  parent.appendChild(wrap);
  return { wrap, input, set(v) { input.value = v; show(); } };
}

const dialCtl = {};
const setCtl = {};
const pct = (v) => Math.round(v * 100) + '%';

function currentPreset() { return info.presets.find((p) => p.name === $('preset').value); }

function buildPanel() {
  const sel = $('preset');
  for (const p of info.presets) sel.add(new Option(p.name[0].toUpperCase() + p.name.slice(1), p.name));
  sel.value = 'classic';
  sel.addEventListener('change', () => { applyPreset(); generate(); });
  for (const d of info.dials) {
    dialCtl[d] = slider($('dials'), {
      name: d, label: d[0].toUpperCase() + d.slice(1), min: 0, max: 1, step: 0.01, value: 0.5, fmt: pct,
      onChange: () => autoGen(),
    });
  }
  for (const s of info.settings) {
    const fmt = s.step < 1 ? pct : (v) => String(Math.round(v));
    const ctl = slider($('settings'), {
      name: s.name, label: s.label, min: s.min, max: s.max, step: s.step, value: s.min, fmt,
      onInput: (v) => { pinned[s.name] = v; ctl.wrap.classList.add('pinned'); },
      onChange: () => autoGen(),
      pin: (wrap) => { delete pinned[s.name]; wrap.classList.remove('pinned'); autoGen(); },
    });
    setCtl[s.name] = ctl;
  }
  $('secret').addEventListener('change', () => { pinned.secret = $('secret').checked; autoGen(); });
  $('resetDials').addEventListener('click', () => { applyPreset(); autoGen(); });
  $('generate').addEventListener('click', () => generate());
  $('newLevel').addEventListener('click', () => newLevel());
  $('seedPrev').addEventListener('click', () => { $('seed').value = Math.max(1, +$('seed').value - 1); generate(); });
  $('seedNext').addEventListener('click', () => { $('seed').value = +$('seed').value + 1; generate(); });
  $('seedDice').addEventListener('click', () => newLevel());
  $('seed').addEventListener('change', () => generate());
  const fm = $('formats');
  for (const f of info.formats) {
    const b = document.createElement('button');
    b.innerHTML = `${f.label}<small>${f.description}</small>`;
    b.title = f.description;
    b.addEventListener('click', () => exportLevel(f));
    fm.appendChild(b);
  }
  applyPreset();
}

function applyPreset() {
  const p = currentPreset();
  $('presetDesc').textContent = p.description;
  for (const d of info.dials) dialCtl[d].set(p.dials[d]);
}

function autoGen() { if ($('auto').checked) generate(); }
function newLevel() { $('seed').value = 1 + Math.floor(Math.random() * 99999); generate(); }

let genToken = 0;
async function generate() {
  const token = ++genToken;
  const body = {
    preset: $('preset').value,
    seed: Math.max(1, Math.floor(+$('seed').value || 1)),
    dials: Object.fromEntries(info.dials.map((d) => [d, +dialCtl[d].input.value])),
    settings: { ...pinned },
  };
  $('busy').classList.remove('hidden');
  try {
    const res = await api('/api/generate', body);
    if (token !== genToken) return;
    const glb = await fetch(`/api/level.glb?id=${res.id}`).then((r) => r.arrayBuffer());
    if (token !== genToken) return;
    await showLevel(res, glb);
  } catch (e) {
    if (token === genToken) $('stats').innerHTML = `<div class="err">${e.message}</div>`;
  } finally {
    if (token === genToken) $('busy').classList.add('hidden');
  }
}

function showStats(res) {
  const st = res.stats;
  const rows = [
    ['Rooms', st.rooms], ['Connectors', st.connectors], ['Corridors', st.corridors], ['Cells', st.cells],
    ['Keys', st.keys], ['Doors', st.doors], ['Secrets', st.secrets], ['Lights', st.lights],
    ['Size (m)', st.size_m], ['Time', res.ms + ' ms'],
  ];
  $('stats').innerHTML = rows.map(([k, v]) => `<div><span>${k}</span><b>${v}</b></div>`).join('');
  for (const s of info.settings) {
    if (!(s.name in pinned)) setCtl[s.name].set(res.settings[s.name]);
  }
  if (!('secret' in pinned)) $('secret').checked = res.settings.secret;
}

// ---------------------------------------------------------------- level -> scene
const loader = new GLTFLoader();
let cols = new Map();       // collision columns
let bounds = null;          // map-unit bounds
let movers = [];            // doors / secret doors / lifts: solid boxes that move
let ladders = [];
let keys = [];
let exitBox = null;
let lightSpots = [];
let keysFound = 0;
let finished = false;

const ck = (x, y) => x * 8192 + y;

async function showLevel(res, glb) {
  level = res;
  showStats(res);
  if (levelRoot) { scene.remove(levelRoot); levelRoot.traverse((o) => o.geometry && o.geometry.dispose()); }
  const gltf = await loader.parseAsync(glb, '');
  levelRoot = gltf.scene;
  levelRoot.traverse((o) => {
    if (o.isMesh) {
      const name = (o.material.name || '').replace(/^nl_/, '');
      o.material = mat(name);
      o.userData.mat = name;
    }
  });
  scene.add(levelRoot);
  applyCut();

  cols = new Map();
  let zlo = Infinity, zhi = -Infinity, xlo = Infinity, xhi = -Infinity, ylo = Infinity, yhi = -Infinity;
  for (const c of res.columns) {
    const spans = [];
    for (let i = 3; i < c.length; i += 2) { spans.push([c[i], c[i + 1]]); zlo = Math.min(zlo, c[i]); zhi = Math.max(zhi, c[i + 1]); }
    cols.set(ck(c[0], c[1]), { diag: c[2], spans, slope: null, water: null });
    xlo = Math.min(xlo, c[0]); xhi = Math.max(xhi, c[0] + 1); ylo = Math.min(ylo, c[1]); yhi = Math.max(yhi, c[1] + 1);
  }
  for (const f of res.floor_slopes || []) { const col = cols.get(ck(f[0], f[1])); if (col) col.slope = f.slice(2); }
  for (const w of res.water || []) { const col = cols.get(ck(w[0], w[1])); if (col) col.water = w[2]; }
  const s = res.cell;
  bounds = { x0: xlo * s, x1: xhi * s, y0: ylo * s, y1: yhi * s, z0: zlo, z1: zhi };
  buildEntities(res);
  buildMinimap(res);
  frameOrbit();
  respawn();
}

function box3(lo, hi, material) {
  const g = new THREE.BoxGeometry((hi[0] - lo[0]) * S, (hi[2] - lo[2]) * S, (hi[1] - lo[1]) * S);
  // world-aligned UVs are overkill for movers: scale the grid to the box size
  const m = new THREE.Mesh(g, material);
  m.position.set((lo[0] + hi[0]) / 2 * S, (lo[2] + hi[2]) / 2 * S, -(lo[1] + hi[1]) / 2 * S);
  return m;
}

function buildEntities(res) {
  dyn.clear();
  movers = []; ladders = []; keys = []; exitBox = null; lightSpots = []; keysFound = 0; finished = false;
  $('banner').classList.add('hidden');
  const glow = new THREE.SphereGeometry(0.07, 10, 8);
  for (const e of res.entities) {
    const p = e.props || {};
    if (e.kind === 'door' || e.kind === 'secret_door') {
      const material = e.kind === 'secret_door' ? mat('wall') : mat(p.key === 'blue' ? 'door_blue' : 'door');
      const mesh = box3(e.lo, e.hi, material);
      dyn.add(mesh);
      movers.push({ kind: e.kind, lo: [...e.lo], hi: [...e.hi], base: [...e.lo], height: e.hi[2] - e.lo[2], off: 0, target: 0, mesh, key: p.key });
    } else if (e.kind === 'lift') {
      const lo = [e.lo[0], e.lo[1], p.low - 16], hi = [e.hi[0], e.hi[1], p.low];
      const mesh = box3(lo, hi, mat('lift'));
      dyn.add(mesh);
      movers.push({ kind: 'lift', lo, hi, base: [...lo], travel: p.travel, off: 0, target: 0, mesh, wait: 0 });
    } else if (e.kind === 'ladder') {
      const mesh = box3(e.lo, e.hi, mat('ladder'));
      dyn.add(mesh);
      ladders.push({ lo: e.lo, hi: e.hi, low: p.low, high: p.high });
      // solid up to the landing, so you climb it instead of walking past (Quake 2's ladders are solid too)
      // (a thin plate on the landing's side: standing at its foot never overlaps it)
      const link = res.links.filter((l) => l.kind === 'ladder').sort((a, b) => Math.hypot(a.b[0] - e.pos[0], a.b[1] - e.pos[1]) - Math.hypot(b.b[0] - e.pos[0], b.b[1] - e.pos[1]))[0];
      const lo = [...e.lo], hi = [e.hi[0], e.hi[1], p.high];
      const ax = e.hi[0] - e.lo[0] < e.hi[1] - e.lo[1] ? 0 : 1;
      if (link && link.b[ax] < e.lo[ax]) hi[ax] = lo[ax] + 0.5; else lo[ax] = hi[ax] - 0.5;
      movers.push({ kind: 'ladder', lo, hi, off: 0, mesh });
    } else if (e.kind === 'exit') {
      const mesh = box3(e.lo, e.hi, mat('trigger'));
      dyn.add(mesh);
      exitBox = { lo: e.lo, hi: e.hi };
    } else if (e.kind === 'key') {
      const color = p.color === 'blue' ? 0x5a8aff : 0xff5a5a;
      const mesh = new THREE.Mesh(new THREE.OctahedronGeometry(0.35), new THREE.MeshLambertMaterial({ color, emissive: color, emissiveIntensity: 0.6 }));
      mesh.position.set(e.pos[0] * S, (e.pos[2] + 16) * S, -e.pos[1] * S);
      dyn.add(mesh);
      keys.push({ pos: e.pos, mesh, color: p.color, got: false });
    } else if (e.kind === 'light') {
      const m = new THREE.Mesh(glow, new THREE.MeshBasicMaterial({ color: 0xfff0c8 }));
      m.position.set(e.pos[0] * S, e.pos[2] * S, -e.pos[1] * S);
      dyn.add(m);
      lightSpots.push(new THREE.Vector3(e.pos[0] * S, e.pos[2] * S, -e.pos[1] * S));
    }
  }
  updateMovers(0);
}

function applyCut() {
  if (!levelRoot) return;
  const hide = cutCeilings && mode === 'orbit';
  levelRoot.traverse((o) => { if (o.isMesh && (o.userData.mat === 'ceiling' || o.userData.mat === 'sky')) o.visible = !hide; });
}

function frameOrbit() {
  if (!bounds) return;
  const cx = (bounds.x0 + bounds.x1) / 2 * S, cy = -(bounds.y0 + bounds.y1) / 2 * S, cz = (bounds.z0 + bounds.z1) / 2 * S;
  const span = Math.max(bounds.x1 - bounds.x0, bounds.y1 - bounds.y0) * S;
  orbit.target.set(cx, cz, cy);
  if (mode === 'orbit') {
    camera.position.set(cx + span * 0.3, cz + span * 0.55, cy + span * 0.55);
    camera.lookAt(orbit.target);
  }
  fogFar = scene.fog.far = Math.max(140, span * 2.5);
}

// ---------------------------------------------------------------- minimap
let mapImg = null;
let mapXf = null;
function buildMinimap(res) {
  const s = res.cell;
  const W = (bounds.x1 - bounds.x0) / s, H = (bounds.y1 - bounds.y0) / s;
  const scale = Math.min(200 / W, 200 / H);
  const c = document.createElement('canvas');
  c.width = c.height = 220;
  const g = c.getContext('2d');
  const ox = (220 - W * scale) / 2, oy = (220 - H * scale) / 2;
  const zl = bounds.z0, zr = Math.max(1, bounds.z1 - bounds.z0);
  for (const col of res.columns) {
    const top = col[col.length - 2];
    const t = (top - zl) / zr;
    g.fillStyle = `rgb(${70 + 150 * t | 0},${90 + 110 * t | 0},${150 - 50 * t | 0})`;
    const x = ox + (col[0] - bounds.x0 / s) * scale, y = oy + (bounds.y1 / s - col[1] - 1) * scale;
    g.fillRect(x, y, Math.ceil(scale), Math.ceil(scale));
  }
  for (const e of res.entities) {
    const color = { player_start: '#7be495', exit: '#ffd84c', key: e.props && e.props.color === 'blue' ? '#5a8aff' : '#ff5a5a', door: '#ff8a3c', lift: '#ffcc33', ladder: '#c08850', secret_door: '#b48cff' }[e.kind];
    if (!color) continue;
    g.fillStyle = color;
    g.beginPath();
    g.arc(ox + (e.pos[0] / s - bounds.x0 / s) * scale, oy + (bounds.y1 / s - e.pos[1] / s) * scale, 3, 0, Math.PI * 2);
    g.fill();
  }
  mapImg = c;
  mapXf = { ox, oy, scale, s };
}

function drawMinimap() {
  const mm = $('minimap');
  if (!showMap || !mapImg) return;
  const g = mm.getContext('2d');
  g.clearRect(0, 0, 220, 220);
  g.drawImage(mapImg, 0, 0);
  const { ox, oy, scale, s } = mapXf;
  const px = ox + (P.x - bounds.x0) / s * scale, py = oy + (bounds.y1 - P.y) / s * scale;
  g.save();
  g.translate(px, py);
  g.rotate(-P.yaw);
  g.fillStyle = 'rgba(255,255,255,0.18)';
  g.beginPath(); g.moveTo(0, 0); g.arc(0, 0, 26, -0.6, 0.6); g.closePath(); g.fill();
  g.fillStyle = '#ffffff';
  g.beginPath(); g.moveTo(7, 0); g.lineTo(-4, -4); g.lineTo(-4, 4); g.closePath(); g.fill();
  g.restore();
}

// ---------------------------------------------------------------- walking
const R = 15.5, H = 56, STEP = 18, EYE = 46, GRAV = 800, JUMP = 270;
const P = { x: 0, y: 0, z: 0, vx: 0, vy: 0, vz: 0, yaw: 0, pitch: 0, ground: true, eyeLag: 0, plat: null };
const keysDown = new Set();

function respawn() {
  if (!level) return;
  const st = level.entities.find((e) => e.kind === 'player_start');
  if (!st) return;
  P.x = st.pos[0]; P.y = st.pos[1];
  const p = probe(P.x, P.y, st.pos[2] - 24, STEP + 24);
  P.z = p ? p.ground : st.pos[2] - 24;
  P.vx = P.vy = P.vz = 0;
  P.yaw = ((st.props && st.props.angle) || 0) * Math.PI / 180;
  P.pitch = 0;
  P.eyeLag = 0;
  finished = false;
  $('banner').classList.add('hidden');
}

// Does a solid half (45-degree cell) overlap the player's box? u/v are cell-local, 0..1.
function triHit(diag, u0, v0, u1, v1) {
  const e = 0.002;
  u0 = Math.max(0, u0); v0 = Math.max(0, v0); u1 = Math.min(1, u1); v1 = Math.min(1, v1);
  if (u0 >= u1 || v0 >= v1) return false;
  switch (diag) {
    case 0: return u0 + v0 < 1 - e;        // SW
    case 1: return v0 - u1 < -e;           // SE
    case 2: return v1 - u0 > e;            // NW
    case 3: return u1 + v1 > 1 + e;        // NE
  }
  return false;
}

// The space the player's box would have with feet at (x, y, z): the floor it stands on and the
// ceiling over it, or null if it doesn't fit. `up` is how far it may step up.
function probe(x, y, z, up) {
  const s = level.cell;
  const cx0 = Math.floor((x - R) / s), cx1 = Math.floor((x + R - 0.001) / s);
  const cy0 = Math.floor((y - R) / s), cy1 = Math.floor((y + R - 0.001) / s);
  let ground = -Infinity, ceil = Infinity, plat = null;
  for (let cx = cx0; cx <= cx1; cx++) {
    for (let cy = cy0; cy <= cy1; cy++) {
      const col = cols.get(ck(cx, cy));
      if (!col) return null;
      if (col.diag >= 0 && triHit(col.diag, (x - R) / s - cx, (y - R) / s - cy, (x + R) / s - cx, (y + R) / s - cy)) return null;
      let best = null, bestF = 0;
      for (let i = 0; i < col.spans.length; i++) {
        const sp = col.spans[i];
        // a ramp: the floor under the box is the plane's highest point over the part of the cell it covers
        const f = i === 0 && col.slope ? slopeMax(col.slope, Math.max(0, (x - R) / s - cx), Math.max(0, (y - R) / s - cy), Math.min(1, (x + R) / s - cx), Math.min(1, (y + R) / s - cy)) : sp[0];
        if (f <= z + up && (!best || f > bestF)) { best = sp; bestF = f; }
      }
      if (!best || best[1] < Math.max(bestF, z) + H) return null;
      if (bestF > ground) ground = bestF;
      if (best[1] < ceil) ceil = best[1];
    }
  }
  for (const m of movers) {
    if (x + R <= m.lo[0] || x - R >= m.hi[0] || y + R <= m.lo[1] || y - R >= m.hi[1]) continue;
    if (m.hi[2] <= z + up + 0.01) { if (m.hi[2] >= ground) { ground = m.hi[2]; plat = m; } }
    else if (m.lo[2] >= Math.max(z, ground) + H) ceil = Math.min(ceil, m.lo[2]);
    else return null;
  }
  if (ceil - Math.max(ground, z) < H - 0.01) return null;
  return { ground, ceil, plat };
}

// the highest point of a sloped floor (corner heights z00, z10, z11, z01) over a part [u0,u1] x [v0,v1] of its cell
function slopeMax(sl, u0, v0, u1, v1) {
  const at = (u, v) => sl[0] * (1 - u) * (1 - v) + sl[1] * u * (1 - v) + sl[2] * u * v + sl[3] * (1 - u) * v;
  return Math.max(at(u0, v0), at(u1, v0), at(u1, v1), at(u0, v1));
}

// the water surface over the player's feet, or null
function waterAt(x, y) {
  const col = cols.get(ck(Math.floor(x / level.cell), Math.floor(y / level.cell)));
  return col && col.water != null ? col.water : null;
}

function onLadder() {
  for (const l of ladders) {
    if (P.x + R + 6 > l.lo[0] && P.x - R - 6 < l.hi[0] && P.y + R + 6 > l.lo[1] && P.y - R - 6 < l.hi[1] &&
        P.z >= l.low - 8 && P.z < l.high) return l;
  }
  return null;
}

function tick(dt) {
  if (!level) return;
  const fwd = (keysDown.has('KeyW') || keysDown.has('ArrowUp') ? 1 : 0) - (keysDown.has('KeyS') || keysDown.has('ArrowDown') ? 1 : 0);
  const side = (keysDown.has('KeyD') || keysDown.has('ArrowRight') ? 1 : 0) - (keysDown.has('KeyA') || keysDown.has('ArrowLeft') ? 1 : 0);
  const surface = waterAt(P.x, P.y);
  const swimming = surface != null && P.z + 30 < surface;           // more than half under: swim
  const speed = (keysDown.has('ShiftLeft') || keysDown.has('ShiftRight') ? 420 : 260) * (swimming ? 0.55 : 1);
  const cy = Math.cos(P.yaw), sy = Math.sin(P.yaw);
  let wx = fwd * cy + side * sy, wy = fwd * sy - side * cy;
  const wl = Math.hypot(wx, wy);
  if (wl > 0) { wx /= wl; wy /= wl; }
  const k = P.ground ? Math.min(1, 12 * dt) : Math.min(1, 2.5 * dt);
  P.vx += (wx * speed - P.vx) * k;
  P.vy += (wy * speed - P.vy) * k;

  // ride a platform
  if (P.plat && P.plat.dz) P.z += P.plat.dz;

  // horizontal, one axis at a time (slides along walls)
  const up = P.ground || swimming ? STEP : 0.5;
  for (const axis of ['x', 'y']) {
    const nx = axis === 'x' ? P.x + P.vx * dt : P.x;
    const ny = axis === 'y' ? P.y + P.vy * dt : P.y;
    const p = probe(nx, ny, P.z, up);
    if (p) {
      P.x = nx; P.y = ny;
      if (p.ground > P.z) { P.eyeLag += p.ground - P.z; P.z = p.ground; }
    } else if (axis === 'x') P.vx = 0; else P.vy = 0;
  }

  // vertical
  const ladder = onLadder();
  if (ladder && fwd !== 0) P.vz = fwd * 170;
  else if (ladder && P.vz <= 0) P.vz = Math.max(P.vz - GRAV * dt, -60);
  else if (swimming) {
    // water: buoyant and slow; Space swims up (and out over the edge), otherwise sink gently
    P.vz += ((keysDown.has('Space') ? 150 : -50) - P.vz) * Math.min(1, 4 * dt);
  } else P.vz -= GRAV * dt;
  if (P.ground && keysDown.has('Space') && !ladder && !swimming) { P.vz = JUMP; P.ground = false; }
  const here = probe(P.x, P.y, P.z, 0.5);
  if (here) {
    let nz = P.z + P.vz * dt;
    P.plat = null;
    if (nz <= here.ground) { nz = here.ground; P.vz = 0; P.ground = true; P.plat = here.plat; }
    else P.ground = nz - here.ground < 0.5;
    if (nz + H > here.ceil) { nz = here.ceil - H; P.vz = Math.min(P.vz, 0); }
    P.z = nz;
  } else {
    // squeezed by a mover (a lift, a door): nudge up; anywhere else, stay put
    if (movers.some((m) => m.kind !== 'ladder' && P.x + R > m.lo[0] && P.x - R < m.hi[0] && P.y + R > m.lo[1] && P.y - R < m.hi[1] && P.z < m.hi[2] && P.z + H > m.lo[2])) P.z += 1;
  }
  if (P.z < bounds.z0 - 800) respawn();

  // things to touch
  for (const key of keys) {
    if (!key.got && Math.hypot(key.pos[0] - P.x, key.pos[1] - P.y) < 48 && Math.abs(key.pos[2] - (P.z + 24)) < 64) {
      key.got = true; key.mesh.visible = false; keysFound++;
      flash(`${key.color[0].toUpperCase() + key.color.slice(1)} key found`, 'Doors open for you either way — this is a walk-through.');
    }
  }
  if (exitBox && !finished && P.x + R > exitBox.lo[0] && P.x - R < exitBox.hi[0] && P.y + R > exitBox.lo[1] && P.y - R < exitBox.hi[1] && P.z < exitBox.hi[2] && P.z + H > exitBox.lo[2]) {
    finished = true;
    flash('Exit reached! 🎉', 'Press N for a new level, R to walk this one again', 0);
  }
}

let flashTimer = 0;
function flash(text, sub, seconds = 2.5) {
  const b = $('banner');
  b.innerHTML = `${text}<small>${sub || ''}</small>`;
  b.classList.remove('hidden');
  clearTimeout(flashTimer);
  if (seconds) flashTimer = setTimeout(() => b.classList.add('hidden'), seconds * 1000);
}

function updateMovers(dt) {
  for (const m of movers) {
    if (m.kind === 'ladder') { m.dz = 0; continue; }
    const prev = m.off;
    if (m.kind === 'lift') {
      const on = P.plat === m;
      const near = !on && Math.hypot((m.lo[0] + m.hi[0]) / 2 - P.x, (m.lo[1] + m.hi[1]) / 2 - P.y) < 140;
      if (on) { m.wait += dt; if (m.wait > 0.5) m.target = m.off < m.travel / 2 || m.target === m.travel ? m.travel : 0; }
      else if (near && Math.abs(P.z - (m.off > m.travel / 2 ? m.base[2] + 16 : m.base[2] + 16 + m.travel)) < 40) { m.wait += dt; if (m.wait > 0.8) m.target = m.off > m.travel / 2 ? 0 : m.travel; }
      else m.wait = 0;
      if (m.off !== m.target) {
        const d = Math.sign(m.target - m.off) * Math.min(Math.abs(m.target - m.off), 150 * dt);
        m.off += d;
        if (m.off === m.target) m.wait = -1.5;   // pause at each end
      }
    } else {
      const dx = Math.max(m.base[0] - P.x, 0, P.x - (m.base[0] + (m.hi[0] - m.lo[0])));
      const dy = Math.max(m.base[1] - P.y, 0, P.y - (m.base[1] + (m.hi[1] - m.lo[1])));
      const reach = m.kind === 'secret_door' ? (keysDown.has('KeyE') ? 64 : R + 2) : 110;
      if (Math.hypot(dx, dy) < reach && P.z < m.base[2] + m.height && P.z + H > m.base[2]) m.target = m.height - 6;
      if (m.off < m.target) m.off = Math.min(m.target, m.off + 220 * dt);
    }
    m.dz = m.off - prev;
    m.lo[2] = m.base[2] + m.off;
    m.hi[2] = m.base[2] + m.off + (m.kind === 'lift' ? 16 : m.height);
    m.mesh.position.y = (m.lo[2] + m.hi[2]) / 2 * S;
  }
}

// ---------------------------------------------------------------- modes and input
function setMode(m) {
  mode = m;
  $('modeOrbit').classList.toggle('on', m === 'orbit');
  $('modeWalk').classList.toggle('on', m === 'walk');
  orbit.enabled = m === 'orbit';
  playerMarker.visible = m === 'orbit';
  $('crosshair').classList.toggle('hidden', m !== 'walk');
  if (m === 'orbit' && document.pointerLockElement) document.exitPointerLock();
  if (m === 'orbit') { camera.position.set(P.x * S + 6, (P.z + 400) * S, -P.y * S + 10); orbit.target.set(P.x * S, P.z * S, -P.y * S); }
  applyCut();
  updateHint();
}

function updateHint() {
  const h = $('hint');
  if (mode === 'orbit') h.innerHTML = '<b>Drag</b> orbit · <b>Right-drag</b> pan · <b>Wheel</b> zoom · <b>Tab</b> walk · <b>N</b> new level · <b>G</b> generate · <b>C</b> cut ceilings';
  else if (!document.pointerLockElement) h.innerHTML = '<b>Click the view</b> to walk · <b>Tab</b> back to orbit';
  else h.innerHTML = '<b>WASD</b> move · <b>Mouse</b> look · <b>Space</b> jump · <b>Shift</b> run · <b>W</b> on a ladder climbs · <b>E</b> secret doors · <b>R</b> respawn · <b>N</b> new level · <b>Esc</b> release mouse';
}

$('modeOrbit').addEventListener('click', () => setMode('orbit'));
$('modeWalk').addEventListener('click', () => setMode('walk'));
$('toggleMap').addEventListener('click', () => toggleMap());
$('toggleRoof').addEventListener('click', () => toggleRoof());
function toggleMap() { showMap = !showMap; $('minimap').classList.toggle('hidden', !showMap); $('toggleMap').classList.toggle('on', showMap); }
function toggleRoof() { cutCeilings = !cutCeilings; $('toggleRoof').classList.toggle('on', cutCeilings); applyCut(); }

canvas.addEventListener('click', () => { if (mode === 'walk' && !document.pointerLockElement) canvas.requestPointerLock(); });
document.addEventListener('pointerlockchange', updateHint);
document.addEventListener('mousemove', (e) => {
  if (mode !== 'walk' || document.pointerLockElement !== canvas) return;
  P.yaw -= e.movementX * 0.0022;
  P.pitch = Math.max(-1.5, Math.min(1.5, P.pitch - e.movementY * 0.0022));
});
const typing = () => ['INPUT', 'SELECT', 'TEXTAREA'].includes(document.activeElement && document.activeElement.tagName) && document.activeElement.type !== 'range' && document.activeElement.type !== 'checkbox';
document.addEventListener('keydown', (e) => {
  if (typing()) return;
  if (e.code === 'Tab') { e.preventDefault(); setMode(mode === 'orbit' ? 'walk' : 'orbit'); return; }
  if (e.code === 'KeyN') { newLevel(); return; }
  if (e.code === 'KeyG') { generate(); return; }
  if (e.code === 'KeyM') { toggleMap(); return; }
  if (e.code === 'KeyC') { toggleRoof(); return; }
  if (e.code === 'KeyR' && mode === 'walk') { respawn(); return; }
  if (mode === 'walk' && ['Space', 'ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(e.code)) e.preventDefault();
  keysDown.add(e.code);
});
document.addEventListener('keyup', (e) => keysDown.delete(e.code));
window.addEventListener('blur', () => keysDown.clear());

// ---------------------------------------------------------------- export
async function exportLevel(f) {
  if (!level) return;
  const msg = $('exportMsg');
  if (host) {                                       // the WebView2 app: a native Save dialog
    host.postMessage({ type: 'export', id: level.id, format: f.id });
    return;
  }
  msg.textContent = 'Exporting…';
  const r = await fetch(`/api/export?id=${level.id}&format=${f.id}`);
  if (!r.ok) { msg.textContent = 'Export failed: ' + (await r.text()); return; }
  const blob = await r.blob();
  const name = (r.headers.get('Content-Disposition') || '').match(/filename="([^"]+)"/)?.[1] || `noorlevel.${f.ext}`;
  const a = document.createElement('a');
  a.href = URL.createObjectURL(blob);
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(a.href), 5000);
  msg.textContent = `Saved ${name} (${(blob.size / 1024).toFixed(1)} KB)`;
}
if (host) host.addEventListener('message', (e) => { const d = e.data; if (d && d.type === 'exported') $('exportMsg').textContent = d.text; });

// ---------------------------------------------------------------- loop
function resize() {
  const w = canvas.clientWidth, h = canvas.clientHeight;
  renderer.setSize(w, h, false);
  camera.aspect = w / Math.max(1, h);
  camera.updateProjectionMatrix();
}
window.addEventListener('resize', resize);

let last = performance.now(), acc = 0, lampTimer = 0;
function frame(now) {
  const dt = Math.min(0.1, (now - last) / 1000);
  last = now;
  if (level) {
    acc += dt;
    while (acc >= 1 / 120) { updateMovers(1 / 120); if (mode === 'walk') tick(1 / 120); acc -= 1 / 120; }
    for (const k of keys) k.mesh.rotation.y += dt * 1.5;
  }
  if (mode === 'walk') {
    P.eyeLag = Math.max(0, P.eyeLag - Math.max(P.eyeLag * 10, 60) * dt);
    camera.position.set(P.x * S, (P.z + EYE - P.eyeLag) * S, -P.y * S);
    const wsurf = level ? waterAt(P.x, P.y) : null;
    const under = wsurf != null && P.z + EYE < wsurf;
    scene.fog.color.setHex(under ? 0x1c4a78 : 0x15131a);
    scene.fog.near = under ? 1 : 30;
    scene.fog.far = under ? 18 : fogFar;
    camera.rotation.set(P.pitch, P.yaw - Math.PI / 2, 0);
  } else {
    orbit.update();
    scene.fog.color.setHex(0x15131a); scene.fog.near = 30; scene.fog.far = fogFar;
    playerMarker.position.set(P.x * S, (P.z + H + 24) * S, -P.y * S);
  }
  lampTimer -= dt;
  if (lampTimer <= 0) {                             // the nearest of the level's lights get real light
    lampTimer = 0.25;
    const sorted = lightSpots.slice().sort((a, b) => a.distanceToSquared(camera.position) - b.distanceToSquared(camera.position));
    lamps.forEach((l, i) => {
      if (sorted[i] && mode === 'walk') { l.position.copy(sorted[i]); l.intensity = 2.2; } else l.intensity = 0;
    });
  }
  drawMinimap();
  renderer.render(scene, camera);
  requestAnimationFrame(frame);
}

window.NL = { step: (n) => { for (let i = 0; i < n; i++) { updateMovers(1 / 120); tick(1 / 120); } }, P, probe: (...a) => probe(...a), level: () => level, setMode, respawn, keysDown, movers: () => movers };

// ---------------------------------------------------------------- start
(async function start() {
  resize();
  updateHint();
  try {
    info = await api('/api/info');
  } catch (e) {
    $('stats').innerHTML = `<div class="err">Can't reach the generator: ${e.message}</div>`;
    return;
  }
  buildPanel();
  requestAnimationFrame(frame);
  await generate();
})();
