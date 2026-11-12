"""Self-contained HTML preview: 3D (three.js) + top-down map + mission graph."""
from __future__ import annotations

import base64
import html
import json

from ..level import Level
from ..shapes import corner_vertices
from .gltf import to_glb

ENT_COLORS = {
    "player_start": "#22c55e", "enemy": "#ef4444", "key": "#facc15", "door": "#f97316",
    "secret_door": "#a855f7", "exit": "#10b981", "health": "#f472b6", "megahealth": "#ec4899",
    "ammo": "#eab308", "armor": "#38bdf8", "weapon": "#e5e7eb", "light": "#fde68a",
    "lift": "#f59e0b", "ladder": "#a16207", "window": "#7dd3fc",
}
KIND_LABEL = {"start": "START", "exit": "EXIT", "arena": "ARENA", "hub": "hub", "combat": "fight",
              "supply": "supply", "key": "KEY", "secret": "secret", "stairwell": "stairs ⇅",
              "lift": "lift ⇅", "ladder": "ladder ⇅"}


def _height_color(f: int, lo: int, hi: int) -> str:
    t = 0.0 if hi == lo else (f - lo) / (hi - lo)
    # deep blue -> teal -> sand -> warm
    stops = [(0.0, (40, 60, 110)), (0.35, (40, 130, 140)), (0.7, (200, 180, 110)), (1.0, (235, 120, 70))]
    for (t0, c0), (t1, c1) in zip(stops, stops[1:]):
        if t <= t1:
            k = (t - t0) / (t1 - t0 or 1)
            return "rgb(%d,%d,%d)" % tuple(round(a + (b - a) * k) for a, b in zip(c0, c1))
    return "rgb(235,120,70)"


def map_svg(level: Level) -> str:
    s = 6  # px per cell
    W, H = level.width * s, level.height * s
    floors = [sp[0][0] for sp in level.spans.values()]
    lo, hi = min(floors), max(floors)
    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {W} {H}" class="map">',
             f'<rect width="{W}" height="{H}" class="bg"/>']
    # flip Y so +Y (north in the editor) points up
    fy = lambda y: (level.height - 1 - y) * s
    for (x, y), spans in level.spans.items():
        col = _height_color(spans[-1][0], lo, hi)
        if (x, y) in level.diag:
            _, tri = corner_vertices(level.diag[(x, y)])
            pts = " ".join(f"{(x + a) * s},{(level.height - y - b) * s}" for a, b in tri)
            parts.append(f'<polygon points="{pts}" fill="{col}"/>')
            continue
        parts.append(f'<rect x="{x * s}" y="{fy(y)}" width="{s}" height="{s}" fill="{col}"/>')
        if len(spans) > 1:
            parts.append(f'<rect x="{x * s + 1}" y="{fy(y) + 1}" width="{s - 2}" height="{s - 2}" '
                         f'fill="none" stroke="#fff" stroke-opacity=".55" stroke-width=".8"/>')
    g = level.graph
    for e in g.edges:
        a, b = level.rooms[e.a], level.rooms[e.b]
        (ax, ay), (bx, by) = a.center(), b.center()
        cls = "loop" if not e.tree else e.kind
        if getattr(e, "merged", False):
            cls = "merged"
        drop = any(c.edge == e.id and c.connector == "drop" for c in level.corridors)
        if drop:
            cls = "drop"
        color = {"locked": "#f97316", "secret": "#a855f7", "loop": "#93c5fd", "drop": "#f472b6",
                 "merged": "#4ade80"}.get(cls, "#ffffff")
        dash = {"loop": "6 4", "secret": "2 3", "drop": "1 3"}.get(cls, "")
        parts.append(f'<line x1="{ax * s}" y1="{(level.height - ay) * s}" x2="{bx * s}" y2="{(level.height - by) * s}" '
                     f'stroke="{color}" stroke-width="1.6" stroke-opacity=".8" stroke-dasharray="{dash}"/>')
    for r in level.rooms:
        cx, cy = r.center()
        label = KIND_LABEL.get(r.kind, r.kind)
        node = g.nodes[r.node]
        if node.key:
            label += f" ({node.key})"
        parts.append(f'<text x="{cx * s}" y="{(level.height - cy) * s - 4}" class="lbl">{html.escape(label)}</text>')
        sub = f"z{r.floor}–{r.top_floor}" if r.top_floor is not None else f"z{r.floor}"
        if level.config and level.config.storeys > 1 and r.top_floor is None:
            sub = f"S{r.storey} · " + sub
        parts.append(f'<text x="{cx * s}" y="{(level.height - cy) * s + 8}" class="sub">{sub}</text>')
    for e in level.entities:
        if e.kind == "light":
            continue
        x, y = e.pos[0] / level.cell * s, (level.height - e.pos[1] / level.cell) * s
        parts.append(f'<circle cx="{x:.1f}" cy="{y:.1f}" r="2.2" fill="{ENT_COLORS.get(e.kind, "#fff")}" '
                     f'stroke="#000" stroke-width=".5"/>')
    parts.append("</svg>")
    return "".join(parts)


TEMPLATE = r"""<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>BadLevel Preview</title>
<style>
:root { --bg:#0f1115; --panel:#171a21; --text:#e6e6e6; --muted:#9aa3b2; --accent:#f97316; --line:#2a2f3a; }
@media (prefers-color-scheme: light) { :root:not([data-theme="dark"]) { --bg:#eef0f3; --panel:#ffffff; --text:#1d2330; --muted:#5b6475; --line:#d8dce3; } }
:root[data-theme="light"] { --bg:#eef0f3; --panel:#ffffff; --text:#1d2330; --muted:#5b6475; --line:#d8dce3; }
* { box-sizing: border-box; }
html, body { margin:0; height:100%; background:var(--bg); color:var(--text); font: 14px/1.45 system-ui, -apple-system, Segoe UI, sans-serif; }
.wrap { display:grid; grid-template-columns: 1fr 380px; height:100vh; }
#view { position:relative; min-height:300px; }
#view canvas { display:block; width:100%; height:100%; }
.hud { position:absolute; left:12px; top:12px; display:flex; gap:6px; flex-wrap:wrap; }
.hud button { background:rgba(20,22,28,.85); color:#eee; border:1px solid #3a3f4b; border-radius:6px; padding:6px 10px; cursor:pointer; font:inherit; }
.hud button.on { border-color: var(--accent); color: var(--accent); }
.hud .cut { background:rgba(20,22,28,.85); color:#eee; border:1px solid #3a3f4b; border-radius:6px; padding:3px 10px; display:flex; align-items:center; gap:6px; font-size:13px; }
.hud .cut input { width:120px; accent-color: var(--accent); }
.help { position:absolute; left:12px; bottom:12px; color:#cfd4dc; background:rgba(20,22,28,.7); padding:6px 10px; border-radius:6px; font-size:12px; }
aside { background:var(--panel); border-left:1px solid var(--line); overflow:auto; padding:16px; }
h1 { font-size:18px; margin:0 0 2px; } h1 span { color:var(--accent); }
.muted { color:var(--muted); font-size:12px; }
.map { width:100%; height:auto; border-radius:8px; margin:12px 0; background:#0b0d11; }
.map .bg { fill:#0b0d11; } .map .lbl { fill:#fff; font:bold 9px system-ui; text-anchor:middle; paint-order:stroke; stroke:#000; stroke-width:2.5px; }
.map .sub { fill:#cbd5e1; font:7px system-ui; text-anchor:middle; paint-order:stroke; stroke:#000; stroke-width:2px; }
table { width:100%; border-collapse:collapse; font-size:13px; } td { padding:3px 0; border-bottom:1px solid var(--line); } td:last-child { text-align:right; color:var(--muted); }
.legend { display:flex; flex-wrap:wrap; gap:6px 12px; margin:10px 0; font-size:12px; }
.legend i { display:inline-block; width:10px; height:10px; border-radius:50%; margin-right:5px; vertical-align:-1px; }
.an h2 { font-size:15px; margin:18px 0 6px; }
.an .modes { display:flex; flex-wrap:wrap; gap:4px; margin:6px 0; }
.an .modes button { background:transparent; color:var(--text); border:1px solid var(--line); border-radius:6px; padding:4px 8px; cursor:pointer; font:inherit; font-size:12px; }
.an .modes button.on { border-color:var(--accent); color:var(--accent); }
.an .scale { display:flex; gap:10px; flex-wrap:wrap; font-size:12px; color:var(--muted); margin:4px 0 8px; align-items:center; }
.an .bar { height:10px; width:140px; border-radius:3px; }
.an .sw { display:inline-block; width:12px; height:12px; border-radius:3px; margin-right:4px; vertical-align:-2px; }
.an .flag { border-left:3px solid var(--line); padding:4px 8px; margin:4px 0; font-size:12.5px; cursor:pointer; border-radius:0 4px 4px 0; }
.an .flag:hover { background:rgba(127,127,127,.12); }
.an .flag b { font-size:11px; text-transform:uppercase; letter-spacing:.04em; margin-right:6px; }
.an .flag.problem { border-color:#d03b3b; } .an .flag.problem b { color:#d03b3b; }
.an .flag.warn { border-color:#fab219; } .an .flag.warn b { color:#c98500; }
.an .flag.info { border-color:#2a78d6; } .an .flag.info b { color:#2a78d6; }
.an table.rooms td, .an table.rooms th { font-size:12px; padding:3px 4px; text-align:right; border-bottom:1px solid var(--line); }
.an table.rooms th { color:var(--muted); font-weight:500; }
.an table.rooms td:first-child, .an table.rooms th:first-child { text-align:left; }
.an table.rooms tr.r { cursor:pointer; } .an table.rooms tr.r:hover td { background:rgba(127,127,127,.12); }
.an .pace { width:100%; height:auto; }
.an .pace text { fill:var(--muted); font:10px system-ui; }
.tip { position:absolute; right:12px; bottom:12px; background:rgba(20,22,28,.92); color:#e6e6e6; border:1px solid #3a3f4b; border-radius:8px; padding:8px 10px; font-size:12px; line-height:1.5; display:none; max-width:260px; }
.tip b { color:#fff; }
.ctip { position:fixed; pointer-events:none; background:rgba(20,22,28,.92); color:#e6e6e6; padding:4px 8px; border-radius:6px; font-size:12px; display:none; z-index:5; }
@media (max-width: 800px) { .wrap { grid-template-columns: 1fr; grid-template-rows: 60vh auto; height:auto; } aside { border-left:0; border-top:1px solid var(--line); padding:16px; } }
</style>
</head>
<body>
<div class="wrap">
  <div id="view">
    <div class="hud">
      <button id="orbit" class="on">Orbit</button>
      <button id="fly">Fly (WASD)</button>
      <button id="ceil">Ceilings: off</button>
      <button id="ents" class="on">Entities</button>
      <label class="cut" title="Hide everything above this height, to look into lower storeys">Cut <input type="range" id="cut" min="0" max="1" step="0.05" value="1"><span id="cutv">all</span></label>
    </div>
    <div class="help" id="help">Drag to orbit · scroll to zoom · right-drag to pan</div>
    <div class="tip" id="tip"></div>
  </div>
  <aside>
    <h1>Bad<span>Level</span> · seed __SEED__</h1>
    <div class="muted">Procedural Quake-style blockout · 1 cell = 32 units = 1 m in glTF</div>
    __SVG__
    <div class="muted">Top-down: colour = floor height, outlined cells = room-over-room. White lines = critical/tree links, green = merged into one hall, orange = locked, dashed blue = loops, dotted purple = secret.</div>
    <div class="legend">__LEGEND__</div>
    <table>__STATS__</table>
    <section class="an" id="analysis"></section>
  </aside>
  <div class="ctip" id="ctip"></div>
</div>
<script type="importmap">
{ "imports": {
  "three": "https://cdn.jsdelivr.net/npm/three@0.160.0/build/three.module.js",
  "three/addons/": "https://cdn.jsdelivr.net/npm/three@0.160.0/examples/jsm/"
} }
</script>
<script type="module">
import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';

const GLB_B64 = "__GLB__";
const $ = id => document.getElementById(id);
const COLORS = __COLORS__;
const view = document.getElementById('view');
const renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
view.appendChild(renderer.domElement);
const scene = new THREE.Scene();
scene.background = new THREE.Color(0x1a1d24);
const camera = new THREE.PerspectiveCamera(60, 1, 0.05, 2000);
scene.add(new THREE.HemisphereLight(0xdfe8ff, 0x3a2f28, 1.1));
const sun = new THREE.DirectionalLight(0xffffff, 1.3); sun.position.set(30, 60, 20); scene.add(sun);

// grid texture so artists can read scale (1 tile = 2 m = 64 units)
function gridTex() {
  const c = document.createElement('canvas'); c.width = c.height = 128;
  const g = c.getContext('2d'); g.fillStyle = '#fff'; g.fillRect(0, 0, 128, 128);
  g.strokeStyle = 'rgba(0,0,0,.18)'; g.lineWidth = 1;
  for (let i = 0; i <= 128; i += 32) { g.beginPath(); g.moveTo(i, 0); g.lineTo(i, 128); g.stroke(); g.beginPath(); g.moveTo(0, i); g.lineTo(128, i); g.stroke(); }
  g.strokeStyle = 'rgba(0,0,0,.4)'; g.lineWidth = 3; g.strokeRect(0, 0, 128, 128);
  const t = new THREE.CanvasTexture(c); t.wrapS = t.wrapT = THREE.RepeatWrapping; t.anisotropy = 8; t.colorSpace = THREE.SRGBColorSpace; return t;
}
const tex = gridTex();
const controls = new OrbitControls(camera, renderer.domElement);
controls.enableDamping = true;

const bytes = Uint8Array.from(atob(GLB_B64), c => c.charCodeAt(0));
const ceilings = [], entityObjs = [];
new GLTFLoader().parse(bytes.buffer, '', (gltf) => {
  const root = gltf.scene; scene.add(root);
  root.traverse(o => {
    if (o.isMesh) {
      const m = o.material;
      if (/bl_(floor|wall|ceiling)/.test(m.name)) { m.map = tex; m.needsUpdate = true; }
      if (m.name === 'bl_ceiling') { ceilings.push(o); }
    }
    const kind = o.userData && o.userData.kind;
    if (kind && !o.isMesh && kind !== 'light') {
      const col = new THREE.Color(COLORS[kind] || '#ffffff');
      const geo = kind === 'enemy' ? new THREE.ConeGeometry(0.3, 0.9, 8) : kind === 'player_start' ? new THREE.CylinderGeometry(0.35, 0.35, 1.7, 12) : new THREE.OctahedronGeometry(0.28);
      const mesh = new THREE.Mesh(geo, new THREE.MeshStandardMaterial({ color: col, emissive: col, emissiveIntensity: 0.45 }));
      if (kind === 'player_start') mesh.position.y = 0.1;
      o.add(mesh); entityObjs.push(mesh);
    }
  });
  // ceilings hidden by default so the level reads from above
  ceilings.forEach(m => m.visible = false);
  const box = new THREE.Box3().setFromObject(root), size = box.getSize(new THREE.Vector3()), ctr = box.getCenter(new THREE.Vector3());
  controls.target.copy(ctr);
  camera.position.set(ctr.x + size.x * 0.15, ctr.y + Math.max(size.x, size.z) * 0.75, ctr.z + size.z * 0.6);
  const start = root.getObjectByName('player_start_0');
  window.__startPos = start ? start.getWorldPosition(new THREE.Vector3()) : ctr.clone();
  const a = ((start && start.userData.angle) || 0) * Math.PI / 180;
  window.__startYaw = Math.atan2(-Math.cos(a), Math.sin(a));
  const cut = $('cut'), plane = new THREE.Plane(new THREE.Vector3(0, -1, 0), box.max.y);
  cut.min = box.min.y.toFixed(2); cut.max = box.max.y.toFixed(2); cut.value = cut.max;
  window.__setCut = (y) => {
    cut.value = y; const all = y >= box.max.y - 0.01;
    plane.constant = +y; renderer.clippingPlanes = all ? [] : [plane];
    $('cutv').textContent = all ? 'all' : Math.round(y * 32) + 'u';
  };
  cut.oninput = () => window.__setCut(+cut.value);
  window.__view = { camera, controls, root }; window.__ready = true;
});

// --- fly mode -----------------------------------------------------------
let mode = 'orbit', yaw = 0, pitch = 0; const keys = {};
addEventListener('keydown', e => keys[e.code] = true); addEventListener('keyup', e => keys[e.code] = false);
renderer.domElement.addEventListener('click', () => { if (mode === 'fly') renderer.domElement.requestPointerLock(); });
addEventListener('mousemove', e => { if (mode === 'fly' && document.pointerLockElement === renderer.domElement) { yaw -= e.movementX * 0.0025; pitch = Math.max(-1.5, Math.min(1.5, pitch - e.movementY * 0.0025)); } });
function setMode(m) {
  mode = m; window.__mode = m; $('orbit').classList.toggle('on', m === 'orbit'); $('fly').classList.toggle('on', m === 'fly');
  controls.enabled = m === 'orbit';
  if (m === 'fly') {
    const p = window.__startPos; camera.position.set(p.x, p.y + 0.55, p.z); yaw = window.__startYaw || 0; pitch = 0;
    ceilings.forEach(c => c.visible = true); $('ceil').textContent = 'Ceilings: on';
    if (window.__setCut) window.__setCut(+$('cut').max);
    $('help').textContent = 'Click to look around · WASD move · Space/C up/down · Shift fast · Esc release';
  } else { $('help').textContent = 'Drag to orbit · scroll to zoom · right-drag to pan'; document.exitPointerLock(); }
}
$('orbit').onclick = () => setMode('orbit'); $('fly').onclick = () => setMode('fly');
$('ceil').onclick = () => { const v = !(ceilings[0] && ceilings[0].visible); ceilings.forEach(c => c.visible = v); $('ceil').textContent = 'Ceilings: ' + (v ? 'on' : 'off'); };
$('ents').onclick = () => { const v = !$('ents').classList.contains('on'); $('ents').classList.toggle('on', v); entityObjs.forEach(o => o.visible = v); };

// --- analysis overlay (data comes from badlevel.analysis; this only draws it) ---
const ANALYSIS = __ANALYSIS__;
if (ANALYSIS) setupAnalysis(ANALYSIS);

function setupAnalysis(A) {
  const fi = Object.fromEntries(A.fields.map((f, i) => [f, i]));
  const D = A.data, cfg = A.config;
  const toV = (x, y, z) => new THREE.Vector3(x / 32, z / 32, -y / 32);
  const lerp = (c1, c2, t) => new THREE.Color(c1).lerp(new THREE.Color(c2), Math.max(0, Math.min(1, t)));
  const STATUS = ['#0ca30c', '#fab219', '#d03b3b'], STATUS_LABEL = ['protected', 'moderate', 'exposed'];
  const MODES = {
    exposure: { label: 'Exposure', color: r => new THREE.Color(STATUS[r[fi.cls]]),
      legend: () => STATUS.map((c, i) => `<span><i class="sw" style="background:${c}"></i>${STATUS_LABEL[i]} ${['&lt; ' + cfg.green_max, cfg.green_max + '–' + cfg.yellow_max, '≥ ' + cfg.yellow_max][i]} m²</span>`).join('') + '<span>m² of floor that can see you</span>' },
    cover: { label: 'Crouch cover', color: r => lerp('#b7d3f6', '#184f95', r[fi.cover]),
      legend: () => scale('#b7d3f6', '#184f95', '0%', '100%', 'share of viewers lost by crouching') },
    overlook: { label: 'Overlook', color: r => lerp('#fbd7c4', '#a8401a', r[fi.overlook] / 400),
      legend: () => scale('#fbd7c4', '#a8401a', '0', '400+ m²', 'floor area you can see') },
    height: { label: 'High ground', color: r => { const h = r[fi.height_adv]; return h >= 0 ? lerp('#d9d8d4', '#d03b3b', h / 96) : lerp('#d9d8d4', '#256abf', -h / 96); },
      legend: () => `<span><i class="bar" style="display:inline-block;background:linear-gradient(90deg,#256abf,#d9d8d4,#d03b3b)"></i></span><span>below targets · level · above (±96 units)</span>` },
    lanes: { label: 'Longest sightline', color: r => lerp('#cde2fb', '#104281', r[fi.longest] / 1536),
      legend: () => scale('#cde2fb', '#104281', '0', '48 m', 'longest mutual sightline from here') },
  };
  function scale(a, b, lo, hi, what) { return `<span>${lo}</span><span class="bar" style="background:linear-gradient(90deg,${a},${b})"></span><span>${hi}</span><span>${what}</span>`; }

  // heat tiles: one per walkable cell, coloured by its sample
  const T = A.tiles;
  const geo = new THREE.PlaneGeometry(0.94, 0.94); geo.rotateX(-Math.PI / 2);
  const tiles = new THREE.InstancedMesh(geo, new THREE.MeshBasicMaterial({ transparent: true, opacity: 0.88, depthWrite: false }), T.length);
  const m4 = new THREE.Matrix4();
  T.forEach((t, i) => { const p = toV(t[0], t[1], t[2]); tiles.setMatrixAt(i, m4.makeTranslation(p.x, p.y + 0.03, p.z)); });
  tiles.renderOrder = 2; scene.add(tiles);
  // firing lanes at eye height
  const lp = [];
  A.lanes.forEach(l => { lp.push(toV(l.a[0], l.a[1], l.a[2] + cfg.eye_height), toV(l.b[0], l.b[1], l.b[2] + cfg.eye_height)); });
  const lanes = new THREE.LineSegments(new THREE.BufferGeometry().setFromPoints(lp), new THREE.LineBasicMaterial({ color: 0xff3d9a }));
  lanes.visible = false; scene.add(lanes);
  // chokepoints
  const chokes = new THREE.Group();
  A.chokepoints.forEach(c => { const m = new THREE.Mesh(new THREE.TorusGeometry(1.4, 0.12, 8, 32), new THREE.MeshBasicMaterial({ color: c.mandatory ? 0xd03b3b : 0xfab219 }));
    m.rotation.x = Math.PI / 2; m.position.copy(toV(...c.pos)).add(new THREE.Vector3(0, 0.15, 0)); chokes.add(m); });
  chokes.visible = false; scene.add(chokes);
  // visual-only connections (see, can't walk directly): cyan sight lines, warmer if across storeys
  const vis = new THREE.Group();
  ((A.visual && A.visual.links) || []).forEach(l => {
    const g = new THREE.BufferGeometry().setFromPoints([toV(...l.from), toV(...l.to)]);
    vis.add(new THREE.Line(g, new THREE.LineBasicMaterial({ color: l.cross_storey ? 0xfacc15 : 0x22d3ee })));
  });
  vis.visible = false; scene.add(vis);

  let mode = 'exposure';
  function paint() {
    const on = mode !== 'off'; tiles.visible = on;
    if (on) { T.forEach((t, i) => tiles.setColorAt(i, MODES[mode].color(D[t[3]]))); tiles.instanceColor.needsUpdate = true; }
    $('an-legend').innerHTML = on ? MODES[mode].legend() : '';
    document.querySelectorAll('#an-modes button').forEach(b => b.classList.toggle('on', b.dataset.m === mode));
  }

  // --- panel ---
  const lv = A.level, es = lv.exposure_share, pct = v => Math.round(v * 100) + '%';
  const rows = [['Positions analysed', `${lv.positions} (${lv.rays_cast.toLocaleString()} rays)`],
    ['Exposure: protected / moderate / exposed', `${pct(es.green)} / ${pct(es.yellow)} / ${pct(es.red)}`],
    ['Longest firing lane', (lv.longest_lane / 32).toFixed(0) + ' m'],
    ['Positions with vertical sightlines', pct(lv.vertical_sightline_share)],
    ['Loops / mandatory chokepoints', `${lv.loops} / ${lv.mandatory_chokepoints}`]];
  const sel = A.selection ? `<div class="muted">Variant ${A.selection.chosen_variant} chosen from ${A.selection.candidates.length} candidate(s), gate ${A.selection.passed_gate ? 'passed' : 'not passed'}.</div>` : '';
  const flags = A.flags.map((f, i) => `<div class="flag ${f.severity}" data-i="${i}"><b>${f.severity}</b>${esc(f.message)}</div>`).join('') || '<div class="muted">No flags.</div>';
  const roomRows = A.rooms.map(r => `<tr class="r" data-room="${r.id}"><td>${r.id} ${esc(r.kind)}<br><span class="muted">${esc(r.shape)}</span></td><td>${r.area_m2}</td><td>${pct(r.mutual_visibility)}</td><td>${pct(r.exposure_share.red)}</td><td>${r.cover_spots}</td><td>${r.height_levels}</td><td>${r.entrances}</td></tr>`).join('');
  $('analysis').innerHTML = `<h2>Combat analysis</h2>${sel}
    <div class="modes" id="an-modes">${Object.entries(MODES).map(([k, m]) => `<button data-m="${k}">${m.label}</button>`).join('')}<button data-m="off">Off</button></div>
    <div class="scale" id="an-legend"></div>
    <div class="modes"><button id="an-lanes">Firing lanes</button><button id="an-chokes">Chokepoints</button><button id="an-vis">Sight links</button></div>
    <div class="muted">Click a tile in the 3D view to inspect that position.</div>
    <table>${rows.map(r => `<tr><td>${r[0]}</td><td>${r[1]}</td></tr>`).join('')}</table>
    ${visualPanel(A.visual)}
    <h2>Pacing along the critical path</h2><div class="muted">Mean exposure per room, start → exit. Variety is good.</div>${pacing(lv.pacing)}
    <h2>Flags (${A.flags.length})</h2><div class="muted">Potential issues and notable spots, not a fun score. Click to look.</div>${flags}
    <h2>Rooms</h2><table class="rooms"><tr><th>room</th><th>m²</th><th>mutual vis.</th><th>exposed</th><th>cover spots</th><th>heights</th><th>ways in</th></tr>${roomRows}</table>`;
  function visualPanel(V) {
    if (!V) return '';
    const cross = V.links.filter(l => l.cross_storey).length, glass = V.links.filter(l => l.through_glass).length;
    const lm = V.landmarks.map(l => `${esc(l.area)} (${l.seen_from})`).join(', ') || 'none';
    const pv = V.previews.filter(p => p.steps_ahead >= 2).map(p => `${esc(p.room)}: ${p.steps_ahead} ahead`).join(', ') || 'none';
    return `<h2>Visual connectivity</h2><div class="muted">Seeing is not walking: areas that see each other but are 3+ steps apart on foot. Yellow lines cross storeys.</div>
      <table><tr><td>Sight links / across storeys / through glass</td><td>${V.links.length} / ${cross} / ${glass}</td></tr>
      <tr><td>Landmarks (seen from)</td><td>${lm}</td></tr>
      <tr><td>Critical-path rooms seen 2+ rooms ahead</td><td>${Math.round(V.previewed_share * 100)}%</td></tr>
      <tr><td>Previews</td><td>${pv}</td></tr></table>`;
  }
  function esc(t) { return String(t).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]); }
  function pacing(P) {
    const w = 340, h = 90, max = Math.max(1, ...P.map(p => p.exposure_mean)), bw = w / P.length;
    return `<svg class="pace" viewBox="0 0 ${w} ${h + 16}">` + P.map((p, i) => {
      const bh = Math.max(2, (h - 6) * p.exposure_mean / max), x = i * bw + bw * 0.2, ww = bw * 0.6;
      return `<path d="M${x} ${h} V${h - bh + 4} q0 -4 4 -4 H${x + ww - 4} q4 0 4 4 V${h} Z" fill="#2a78d6" data-tip="${esc(p.kind)} (room ${p.room}): ${p.exposure_mean} m² mean exposure, ${Math.round(p.mutual_visibility * 100)}% mutual visibility, ${p.enemies} enemies"/>`
        + `<rect x="${i * bw}" y="0" width="${bw}" height="${h}" fill="transparent" data-tip="${esc(p.kind)} (room ${p.room}): ${p.exposure_mean} m² mean exposure, ${Math.round(p.mutual_visibility * 100)}% mutual visibility, ${p.enemies} enemies"/>`
        + `<text x="${i * bw + bw / 2}" y="${h + 12}" text-anchor="middle">${esc(({combat: 'fight', supply: 'supply'})[p.kind] || p.kind)}</text>`;
    }).join('') + `<line x1="0" x2="${w}" y1="${h}" y2="${h}" stroke="currentColor" stroke-opacity=".25"/></svg>`;
  }
  const ctip = $('ctip');
  $('analysis').addEventListener('mousemove', e => { const t = e.target.dataset && e.target.dataset.tip; ctip.style.display = t ? 'block' : 'none'; if (t) { ctip.textContent = t; ctip.style.left = (e.clientX + 12) + 'px'; ctip.style.top = (e.clientY + 12) + 'px'; } });
  $('analysis').addEventListener('mouseleave', () => ctip.style.display = 'none');
  document.querySelectorAll('#an-modes button').forEach(b => b.onclick = () => { mode = b.dataset.m; paint(); });
  $('an-lanes').onclick = () => { lanes.visible = !lanes.visible; $('an-lanes').classList.toggle('on', lanes.visible); };
  $('an-chokes').onclick = () => { chokes.visible = !chokes.visible; $('an-chokes').classList.toggle('on', chokes.visible); };
  $('an-vis').onclick = () => { vis.visible = !vis.visible; $('an-vis').classList.toggle('on', vis.visible); };
  function focus(p, dist = 14) { setMode('orbit'); const v = toV(...p); controls.target.copy(v); camera.position.set(v.x + dist * 0.5, v.y + dist, v.z + dist * 0.7); controls.update(); }
  document.querySelectorAll('#analysis .flag').forEach(el => el.onclick = () => { const f = A.flags[+el.dataset.i];
    if (f.pos) focus(f.pos); else if (f.room !== undefined) focus(A.room_centers[f.room], 20);
    if (f.code === 'sniper_lane' && !lanes.visible) $('an-lanes').click(); if (f.code === 'mandatory_choke' && !chokes.visible) $('an-chokes').click();
    if (['vertical_view', 'landmark', 'preview'].includes(f.code) && !vis.visible) $('an-vis').click(); });
  document.querySelectorAll('#analysis tr.r').forEach(el => el.onclick = () => focus(A.room_centers[+el.dataset.room], 20));
  // click a tile to inspect it
  const ray = new THREE.Raycaster(), mouse = new THREE.Vector2(); let down = null;
  renderer.domElement.addEventListener('pointerdown', e => down = [e.clientX, e.clientY]);
  renderer.domElement.addEventListener('pointerup', e => {
    if (!down || Math.hypot(e.clientX - down[0], e.clientY - down[1]) > 4 || !tiles.visible || window.__mode === 'fly') return;
    const r = renderer.domElement.getBoundingClientRect();
    mouse.set(((e.clientX - r.left) / r.width) * 2 - 1, -((e.clientY - r.top) / r.height) * 2 + 1);
    ray.setFromCamera(mouse, camera); const hit = ray.intersectObject(tiles)[0];
    const tip = $('tip'); if (!hit) { tip.style.display = 'none'; return; }
    const s = D[T[hit.instanceId][3]];
    tip.innerHTML = `<b>${esc(A.areas[s[fi.area]])}</b> · floor ${s[fi.z]}<br>` +
      `Exposure: <b>${s[fi.exposure]} m²</b> (${STATUS_LABEL[s[fi.cls]]})<br>Crouch cover: ${Math.round(s[fi.cover] * 100)}% of viewers lost<br>` +
      `Overlook: ${s[fi.overlook]} m² · longest line ${(s[fi.longest] / 32).toFixed(0)} m<br>` +
      `Height vs targets: ${s[fi.height_adv] > 0 ? '+' : ''}${s[fi.height_adv]} units · vertical lines: ${s[fi.vertical]}`;
    tip.style.display = 'block';
  });
  paint();
  window.__analysis = { setMode: m => { mode = m; paint(); }, focus, lanes, chokes, vis };
}

function resize() { const w = view.clientWidth, h = view.clientHeight; renderer.setSize(w, h, false); camera.aspect = w / h; camera.updateProjectionMatrix(); }
addEventListener('resize', resize); resize();
const clock = new THREE.Clock();
renderer.setAnimationLoop(() => {
  const dt = Math.min(clock.getDelta(), 0.05);
  if (mode === 'fly') {
    camera.rotation.set(pitch, yaw, 0, 'YXZ');
    const sp = (keys.ShiftLeft ? 12 : 4) * dt, f = new THREE.Vector3(), r = new THREE.Vector3();
    camera.getWorldDirection(f); r.crossVectors(f, camera.up).normalize();
    if (keys.KeyW) camera.position.addScaledVector(f, sp); if (keys.KeyS) camera.position.addScaledVector(f, -sp);
    if (keys.KeyD) camera.position.addScaledVector(r, sp); if (keys.KeyA) camera.position.addScaledVector(r, -sp);
    if (keys.Space) camera.position.y += sp; if (keys.KeyC) camera.position.y -= sp;
  } else controls.update();
  renderer.render(scene, camera);
});
</script>
</body>
</html>
"""


def _analysis_embed(level: Level, analysis: dict) -> dict:
    """Shape the analysis report for drawing: map every walkable cell to its sample."""
    from ..validate import _node_at, flood
    s = level.cell
    fields = analysis["samples"]["fields"]
    data = analysis["samples"]["data"]
    fx, fy, fz, fa = (fields.index(k) for k in ("x", "y", "z", "area"))
    stride = analysis["config"]["stride"]
    by_key = {}
    by_area = {}
    for i, row in enumerate(data):
        cx, cy = int(row[fx] // s), int(row[fy] // s)
        by_key[(cx, cy, row[fz])] = i
        by_area.setdefault(int(row[fa]), []).append(i)
    area_idx = {a: i for i, a in enumerate(analysis["areas"])}
    start = next(e for e in level.entities if e.kind == "player_start")
    tiles = []
    for (x, y, k) in sorted(flood(level, _node_at(level, start.pos), set())):
        f = level.spans[(x, y)][k][0]
        bx, by = x - x % stride, y - y % stride
        i = by_key.get((bx, by, f))
        if i is None:
            cands = by_area.get(area_idx.get(level.owner_of((x, y), level.spans[(x, y)][k])), [])
            cands = [j for j in cands if abs(data[j][fz] - f) <= 16] or cands
            if not cands:
                continue
            i = min(cands, key=lambda j: (data[j][fx] / s - x - .5) ** 2 + (data[j][fy] / s - y - .5) ** 2)
        tiles.append([(x + .5) * s, (y + .5) * s, f, i])
    centers = {}
    for r in level.rooms:
        cx, cy = r.center()
        centers[r.id] = [cx * s, cy * s, r.floor]
    return {"config": analysis["config"], "fields": fields, "data": data, "tiles": tiles,
            "areas": analysis["areas"], "lanes": analysis["lanes"], "chokepoints": analysis["chokepoints"],
            "rooms": analysis["rooms"], "level": analysis["level"], "flags": analysis["flags"],
            "selection": analysis.get("selection"), "room_centers": centers,
            "visual": analysis.get("visual")}


def to_html(level: Level, analysis: dict = None) -> str:
    glb = base64.b64encode(to_glb(level)).decode()
    stats = level.stats()
    report = getattr(level, "report", {}) or {}
    feats = sorted({f for r in level.rooms for f in r.features})
    counts = {}
    for e in level.entities:
        counts[e.kind] = counts.get(e.kind, 0) + 1
    rows = [
        ("Rooms", stats["rooms"]), ("Corridors", stats["corridors"]),
        ("Loops", sum(1 for e in level.graph.edges if not e.tree)),
        ("Keys", ", ".join(e.key for e in level.graph.edges if e.kind == "locked") or "none"),
        ("Storeys", stats["storeys"]),
        ("Vertical connectors", ", ".join(f"{v} {k}" for k, v in stats["connectors"].items() if v) or "-"),
        ("One-way drops", stats["drops"]),
        ("Windows / galleries", f'{stats["windows"]} / {stats["galleries"]}'),
        ("Merged halls (rooms)", f'{stats["halls"]} ({stats["hall_rooms"]})'),
        ("Room shapes", ", ".join(stats["shapes"])),
        ("Room features", ", ".join(feats) or "-"),
        ("45° wall cells", stats["diagonal_cells"]),
        ("Height range", f'{stats["z_range"][0] + 32} .. {stats["z_range"][1] - 32} units'),
        ("Room-over-room cells", stats["multi_span_cells"]),
        ("Enemies", counts.get("enemy", 0)),
        ("Pickups", sum(v for k, v in counts.items() if k in ("health", "megahealth", "ammo", "armor", "weapon"))),
        ("Validation", "passed" if report.get("ok") else "not run / failed"),
    ]
    stats_html = "".join(f"<tr><td>{html.escape(str(k))}</td><td>{html.escape(str(v))}</td></tr>" for k, v in rows)
    legend = "".join(f'<span><i style="background:{c}"></i>{k.replace("_", " ")}</span>'
                     for k, c in ENT_COLORS.items() if k != "light")
    return (TEMPLATE.replace("__GLB__", glb)
            .replace("__SEED__", str(level.seed))
            .replace("__SVG__", map_svg(level))
            .replace("__LEGEND__", legend)
            .replace("__STATS__", stats_html)
            .replace("__COLORS__", json.dumps(ENT_COLORS))
            .replace("__ANALYSIS__", json.dumps(_analysis_embed(level, analysis), separators=(",", ":"))
                     if analysis else "null"))
