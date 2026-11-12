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
}
KIND_LABEL = {"start": "START", "exit": "EXIT", "arena": "ARENA", "hub": "hub", "combat": "fight",
              "supply": "supply", "key": "KEY", "secret": "secret"}


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
        color = {"locked": "#f97316", "secret": "#a855f7", "loop": "#93c5fd"}.get(cls, "#ffffff")
        dash = {"loop": "6 4", "secret": "2 3"}.get(cls, "")
        parts.append(f'<line x1="{ax * s}" y1="{(level.height - ay) * s}" x2="{bx * s}" y2="{(level.height - by) * s}" '
                     f'stroke="{color}" stroke-width="1.6" stroke-opacity=".8" stroke-dasharray="{dash}"/>')
    for r in level.rooms:
        cx, cy = r.center()
        label = KIND_LABEL.get(r.kind, r.kind)
        node = g.nodes[r.node]
        if node.key:
            label += f" ({node.key})"
        parts.append(f'<text x="{cx * s}" y="{(level.height - cy) * s - 4}" class="lbl">{html.escape(label)}</text>')
        parts.append(f'<text x="{cx * s}" y="{(level.height - cy) * s + 8}" class="sub">z{r.floor}</text>')
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
    </div>
    <div class="help" id="help">Drag to orbit · scroll to zoom · right-drag to pan</div>
  </div>
  <aside>
    <h1>Bad<span>Level</span> · seed __SEED__</h1>
    <div class="muted">Procedural Quake-style blockout · 1 cell = 32 units = 1 m in glTF</div>
    __SVG__
    <div class="muted">Top-down: colour = floor height, outlined cells = room-over-room. White lines = critical/tree links, orange = locked, dashed blue = loops, dotted purple = secret.</div>
    <div class="legend">__LEGEND__</div>
    <table>__STATS__</table>
  </aside>
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
  window.__view = { camera, controls, root }; window.__ready = true;
});

// --- fly mode -----------------------------------------------------------
let mode = 'orbit', yaw = 0, pitch = 0; const keys = {};
addEventListener('keydown', e => keys[e.code] = true); addEventListener('keyup', e => keys[e.code] = false);
renderer.domElement.addEventListener('click', () => { if (mode === 'fly') renderer.domElement.requestPointerLock(); });
addEventListener('mousemove', e => { if (mode === 'fly' && document.pointerLockElement === renderer.domElement) { yaw -= e.movementX * 0.0025; pitch = Math.max(-1.5, Math.min(1.5, pitch - e.movementY * 0.0025)); } });
const $ = id => document.getElementById(id);
function setMode(m) {
  mode = m; $('orbit').classList.toggle('on', m === 'orbit'); $('fly').classList.toggle('on', m === 'fly');
  controls.enabled = m === 'orbit';
  if (m === 'fly') {
    const p = window.__startPos; camera.position.set(p.x, p.y + 0.55, p.z); yaw = window.__startYaw || 0; pitch = 0;
    ceilings.forEach(c => c.visible = true); $('ceil').textContent = 'Ceilings: on';
    $('help').textContent = 'Click to look around · WASD move · Space/C up/down · Shift fast · Esc release';
  } else { $('help').textContent = 'Drag to orbit · scroll to zoom · right-drag to pan'; document.exitPointerLock(); }
}
$('orbit').onclick = () => setMode('orbit'); $('fly').onclick = () => setMode('fly');
$('ceil').onclick = () => { const v = !(ceilings[0] && ceilings[0].visible); ceilings.forEach(c => c.visible = v); $('ceil').textContent = 'Ceilings: ' + (v ? 'on' : 'off'); };
$('ents').onclick = () => { const v = !$('ents').classList.contains('on'); $('ents').classList.toggle('on', v); entityObjs.forEach(o => o.visible = v); };

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


def to_html(level: Level) -> str:
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
            .replace("__COLORS__", json.dumps(ENT_COLORS)))
