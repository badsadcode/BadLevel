"""Combat-space analysis of a generated level.

Pipeline:
  1. sample standing positions (reachable floor, every ``stride`` cells, one per
     span, so a balcony and the floor under it are separate positions)
  2. line of sight between all position pairs within ``max_range``:
       eye (floor + eye_height)  ->  target chest (floor + target_height)
       eye                       ->  crouched target (floor + crouch_height)
  3. per-position metrics, firing lanes, per-room tactics, level pacing

Everything returned is plain JSON-serialisable data (world units, Z-up), so
the HTML preview, exporters, engine plugins and the generator itself can all
consume it. Interpretation (flags) lives in ``diagnostics``.
"""
from __future__ import annotations

from collections import defaultdict
from dataclasses import asdict, dataclass

import numpy as np

from ..level import Level
from ..validate import _node_at, flood
from .occupancy import Occupancy


@dataclass
class AnalysisConfig:
    stride: int = 2                # sample every N cells
    max_range: float = 64.0        # cells; longer sightlines are ignored
    eye_height: float = 46.0       # Quake: origin at floor+24, view offset +22
    target_height: float = 48.0    # upper body of a standing target (Quake box top = 56)
    crouch_height: float = 22.0    # centre of a crouched target
    lane_min: float = 1024.0       # units (32 m): minimum length of a "long firing lane"
    max_lanes: int = 16
    green_max: float = 80.0        # m^2 of floor that can see you
    yellow_max: float = 200.0
    ray_step: float = 8.0


FIELDS = ["x", "y", "z", "area", "exposure", "overlook", "cover", "longest",
          "height_adv", "vertical", "cls"]


def _samples(level: Level, cfg: AnalysisConfig):
    start = next(e for e in level.entities if e.kind == "player_start")
    reach = flood(level, _node_at(level, start.pos), set())
    nodes = sorted(n for n in reach if n[0] % cfg.stride == 0 and n[1] % cfg.stride == 0)
    s = level.cell
    xs, ys, zs, owners = [], [], [], []
    for (x, y, i) in nodes:
        xs.append((x + 0.5) * s)
        ys.append((y + 0.5) * s)
        zs.append(level.spans[(x, y)][i][0])
        owners.append(level.owner_of((x, y), level.spans[(x, y)][i]) or "?")
    return np.array(xs), np.array(ys), np.array(zs, dtype=float), owners, nodes


def _bridges(n_rooms, edges):
    """Edges (corridor ids) whose removal disconnects the room graph."""
    def connected(skip):
        adj = defaultdict(list)
        for cid, (a, b) in edges.items():
            if cid != skip:
                adj[a].append(b)
                adj[b].append(a)
        seen, stack = {0}, [0]
        while stack:
            for m in adj[stack.pop()]:
                if m not in seen:
                    seen.add(m)
                    stack.append(m)
        return seen
    full = len(connected(None))
    out, splits = set(), {}
    for cid in edges:
        part = connected(cid)
        if len(part) < full:
            out.add(cid)
            splits[cid] = part
    return out, splits


def _main_path(level: Level):
    g = level.graph
    start = next(n.id for n in g.nodes if n.kind == "start")
    exit_ = next(n.id for n in g.nodes if n.kind == "exit")
    prev = {start: None}
    frontier = [start]
    while frontier:
        nxt = []
        for n in frontier:
            for e in g.edges:
                if not e.tree or n not in (e.a, e.b):
                    continue
                m = e.b if e.a == n else e.a
                if m not in prev:
                    prev[m] = n
                    nxt.append(m)
        frontier = nxt
    path, n = [], exit_
    while n is not None:
        path.append(n)
        n = prev[n]
    return path[::-1]


def analyze(level: Level, cfg: AnalysisConfig = None) -> dict:
    cfg = cfg or AnalysisConfig()
    occ = Occupancy(level)
    xs, ys, zs, owners, nodes = _samples(level, cfg)
    n = len(xs)
    aps = (cfg.stride * level.cell / 32.0) ** 2  # m^2 represented by one sample

    # --- candidate pairs within range -----------------------------------
    cx, cy = xs / level.cell, ys / level.cell
    d2 = (cx[:, None] - cx[None, :]) ** 2 + (cy[:, None] - cy[None, :]) ** 2
    iu, ju = np.nonzero(np.triu(d2 <= cfg.max_range ** 2, k=1))

    def pts(idx, h):
        return np.stack([xs[idx], ys[idx], zs[idx] + h], axis=1)

    # directed visibility: viewer eye -> target chest, both directions
    vi = np.concatenate([iu, ju])
    vj = np.concatenate([ju, iu])
    stand = occ.visible(pts(vi, cfg.eye_height), pts(vj, cfg.target_height), cfg.ray_step)
    V = np.zeros((n, n), dtype=bool)
    V[vi[stand], vj[stand]] = True
    # crouching only matters where the standing target was visible
    ci, cj = vi[stand], vj[stand]
    crouch = occ.visible(pts(ci, cfg.eye_height), pts(cj, cfg.crouch_height), cfg.ray_step)
    Vc = np.zeros((n, n), dtype=bool)
    Vc[ci[crouch], cj[crouch]] = True

    M = V & V.T  # mutual: both can shoot each other
    P = np.stack([xs, ys, zs], axis=1)
    dist = np.sqrt(((P[:, None, :] - P[None, :, :]) ** 2).sum(axis=2))
    dz = zs[:, None] - zs[None, :]

    seen_by = V.sum(axis=0)
    sees = V.sum(axis=1)
    exposure = seen_by * aps
    overlook = sees * aps
    cover = np.where(seen_by > 0, 1.0 - Vc.sum(axis=0) / np.maximum(seen_by, 1), 0.0)
    longest = np.where(M.any(axis=1), np.where(M, dist, 0).max(axis=1), 0.0)
    height_adv = np.where(sees > 0, (np.where(V, dz, 0)).sum(axis=1) / np.maximum(sees, 1), 0.0)
    vertical = (V & (np.abs(dz) >= 64)).sum(axis=1)
    cls = np.where(exposure < cfg.green_max, 0, np.where(exposure < cfg.yellow_max, 1, 2))

    areas = sorted(set(owners))
    area_idx = {a: i for i, a in enumerate(areas)}
    owner_arr = np.array([area_idx[o] for o in owners])

    # --- long firing lanes ------------------------------------------------
    li, lj = np.nonzero(np.triu(M & (dist >= cfg.lane_min), k=1))
    order = np.argsort(-dist[li, lj])
    lanes = []

    def seg_dist(p, a, b):
        # 2D distance from sample p to the segment between samples a and b
        ax, ay, bx, by = xs[a], ys[a], xs[b], ys[b]
        vx, vy = bx - ax, by - ay
        t = np.clip(((xs[p] - ax) * vx + (ys[p] - ay) * vy) / max(vx * vx + vy * vy, 1e-9), 0, 1)
        return np.hypot(xs[p] - (ax + t * vx), ys[p] - (ay + t * vy))

    for k in order:
        a, b = li[k], lj[k]
        # a lane running alongside (or inside) an already listed, longer lane is the same lane
        if any(seg_dist(a, l["_a"], l["_b"]) <= 4 * level.cell and seg_dist(b, l["_a"], l["_b"]) <= 4 * level.cell
               for l in lanes):
            continue
        lanes.append({"_a": a, "_b": b, "a": P[a].tolist(), "b": P[b].tolist(),
                      "length": round(float(dist[a, b]), 1),
                      "areas": sorted({owners[a], owners[b]})})
        if len(lanes) >= cfg.max_lanes:
            break
    for l in lanes:
        del l["_a"], l["_b"]

    # --- room graph: bridges, cycles, chokepoints -------------------------
    edges = {c.id: (c.a, c.b) for c in level.corridors}
    bridges, splits = _bridges(len(level.rooms), edges)
    main = _main_path(level)
    start_room = level.room_by_node(main[0]).id
    exit_room = level.room_by_node(main[-1]).id
    s = level.cell
    chokepoints = []
    for c in level.corridors:
        if c.id not in bridges:
            continue
        part = splits[c.id]
        mandatory = (start_room in part) != (exit_room in part)
        mid = c.path[len(c.path) // 2]
        f = c.floors[len(c.path) // 2]
        chokepoints.append({
            "corridor": c.id, "rooms": [c.a, c.b],
            "pos": [(mid[0] + 0.5) * s, (mid[1] + 0.5) * s, f],
            "mandatory": bool(mandatory),
            "reason": "only route to the exit" if mandatory else "only route into this part of the level",
        })

    # --- per room ---------------------------------------------------------
    ent_by_room = defaultdict(list)
    for e in level.entities:
        c = (int(e.pos[0] // s), int(e.pos[1] // s))
        o = level.owner_at(c, e.pos[2] - 24) or level.owner_at(c, e.pos[2]) or ""
        if o.startswith("room:"):
            ent_by_room[int(o[5:])].append(e.kind)

    rooms = []
    for r in level.rooms:
        idx = np.nonzero(owner_arr == area_idx.get(f"room:{r.id}", -1))[0]
        k = len(idx)
        incident = [c for c in level.corridors if r.id in (c.a, c.b)]
        entry_samples = []
        for c in incident:
            end = c.path[0] if c.a == r.id else c.path[-1]
            ex, ey = (end[0] + 0.5) * s, (end[1] + 0.5) * s
            if k:
                j = idx[np.argmin((xs[idx] - ex) ** 2 + (ys[idx] - ey) ** 2)]
                entry_samples.append(int(j))
        flank_pairs = sum(1 for a in range(len(entry_samples)) for b in range(a + 1, len(entry_samples))
                          if not M[entry_samples[a], entry_samples[b]])
        if k:
            sub = V[np.ix_(idx, idx)]
            mutual = float(sub.sum() / max(1, k * (k - 1)))
            # a cover spot: crouching there hides you from at least half of the room
            seen_inside_crouched = Vc[np.ix_(idx, idx)].sum(axis=0)
            cover_spots = int((seen_inside_crouched < 0.5 * (k - 1)).sum())
            shares = [float((cls[idx] == c).mean()) for c in (0, 1, 2)]
            in_room_lanes = M[np.ix_(idx, idx)] * dist[np.ix_(idx, idx)]
            longest_lane = float(in_room_lanes.max())
            # high ground: sees the most of its own room while standing above it
            share_seen = sub.sum(axis=1) / max(1, k - 1)
            adv = np.array([(zs[i] - zs[idx][sub[t]]).mean() if sub[t].any() else 0.0
                            for t, i in enumerate(idx)])
            hg = np.nonzero(adv >= 32)[0]
            high_ground = None
            if hg.size:
                t = hg[np.argmax(share_seen[hg])]
                high_ground = {"pos": P[idx[t]].tolist(), "room_share_seen": round(float(share_seen[t]), 3),
                               "height_adv": round(float(adv[t]), 1)}
            levels = sorted({float(z) for z in zs[idx]})
        else:
            mutual, cover_spots, shares, longest_lane, high_ground, levels = 0.0, 0, [0, 0, 0], 0.0, None, []
        kinds = ent_by_room[r.id]
        rooms.append({
            "id": r.id, "kind": r.kind, "shape": r.shape, "features": list(r.features),
            "depth": level.graph.nodes[r.node].depth,
            "area_m2": round(len(list(r.cells())) * (level.cell / 32.0) ** 2, 1),
            "samples": int(k),
            "mutual_visibility": round(mutual, 3),
            "exposure_mean": round(float(exposure[idx].mean()), 1) if k else 0.0,
            "exposure_share": {"green": round(shares[0], 3), "yellow": round(shares[1], 3), "red": round(shares[2], 3)},
            "cover_spots": cover_spots,
            "height_levels": len(levels),
            "entrances": len(incident),
            "on_cycle": any(c.id not in bridges for c in incident),
            "flank_pairs": int(flank_pairs),
            "longest_lane": round(longest_lane, 1),
            "high_ground": high_ground,
            "enemies": kinds.count("enemy"),
            "pickups": sum(kinds.count(t) for t in ("health", "megahealth", "ammo", "armor", "weapon")),
            "on_main_path": r.node in main,
        })

    main = [nid for nid in main if not level.room_by_node(nid).is_connector]
    pacing = [{"room": level.room_by_node(nid).id, "kind": level.graph.nodes[nid].kind,
               "exposure_mean": rooms[level.room_by_node(nid).id]["exposure_mean"],
               "mutual_visibility": rooms[level.room_by_node(nid).id]["mutual_visibility"],
               "enemies": rooms[level.room_by_node(nid).id]["enemies"]} for nid in main]

    visual = _visual_connectivity(level, occ, cfg, vi, vj, V, xs, ys, zs, owners, areas, main, pts)

    data = np.stack([xs, ys, zs, owner_arr, np.round(exposure, 1), np.round(overlook, 1), np.round(cover, 3),
                     np.round(longest, 1), np.round(height_adv, 1), vertical, cls], axis=1)
    total = max(1, n)
    report = {
        "format": "badlevel-analysis",
        "version": 1,
        "seed": level.seed,
        "storeys": len({r.storey for r in level.rooms}),
        "config": asdict(cfg),
        "samples": {"fields": FIELDS, "area_per_sample_m2": aps, "count": int(n),
                    "data": [[round(float(v), 3) for v in row] for row in data]},
        "areas": areas,
        "lanes": lanes,
        "chokepoints": chokepoints,
        "visual": visual,
        "rooms": rooms,
        "level": {
            "positions": int(n),
            "rays_cast": int(len(vi) + len(ci)),
            "exposure_share": {"green": round(float((cls == 0).sum() / total), 3),
                               "yellow": round(float((cls == 1).sum() / total), 3),
                               "red": round(float((cls == 2).sum() / total), 3)},
            "exposure_mean": round(float(exposure.mean()), 1) if n else 0.0,
            "longest_lane": lanes[0]["length"] if lanes else 0.0,
            "vertical_sightline_share": round(float((vertical > 0).sum() / total), 3),
            "height_levels": len({float(z) for z in zs}),
            # independent cycles in the room graph (edges - nodes + 1)
            "loops": max(0, len(level.corridors) - len(level.rooms) + 1),
            "corridors_on_loops": sum(1 for c in level.corridors if c.id not in bridges),
            "mandatory_chokepoints": sum(1 for c in chokepoints if c["mandatory"]),
            "pacing": pacing,
            "visual_links": len(visual["links"]),
            "cross_storey_views": sum(1 for l in visual["links"] if l["cross_storey"]),
            "glass_views": sum(1 for l in visual["links"] if l["through_glass"]),
            "previewed_share": visual["previewed_share"],
        },
    }
    from .diagnostics import diagnose
    report["flags"] = diagnose(report)
    return report


def _area_graph(level: Level):
    """Physical connections between areas (rooms and corridors), for counting hops."""
    adj = defaultdict(set)
    for c in level.corridors:
        ca = f"corridor:{c.id}"
        for rid in (c.a, c.b):
            adj[ca].add(f"room:{rid}")
            adj[f"room:{rid}"].add(ca)
    return adj


def _hops(adj, a, b, limit=8):
    if a == b:
        return 0
    seen, frontier = {a}, [a]
    for d in range(1, limit + 1):
        nxt = []
        for n in frontier:
            for m in adj[n]:
                if m == b:
                    return d
                if m not in seen:
                    seen.add(m)
                    nxt.append(m)
        frontier = nxt
    return limit + 1


def _visual_connectivity(level, occ, cfg, vi, vj, V, xs, ys, zs, owners, areas, main, pts):
    """What can be *seen* from where, as opposed to walked to.

    links:     area pairs that see each other although they are 2+ physical hops apart
               (windows, galleries, long open views), with cross-storey and through-glass flags
    landmarks: rooms seen from many non-adjacent areas
    previews:  how many rooms ahead along the critical path each room can first be seen
    """
    n = len(xs)
    if level.glass:
        from .occupancy import Occupancy
        see_occ = Occupancy(level, glass_opaque=False)
        see = see_occ.visible(pts(vi, cfg.eye_height), pts(vj, cfg.target_height), cfg.ray_step)
        S = np.zeros((n, n), dtype=bool)
        S[vi[see], vj[see]] = True
    else:
        S = V
    owner_idx = np.array([areas.index(o) for o in owners])
    adj = _area_graph(level)
    pair = defaultdict(list)
    ii, jj = np.nonzero(S)
    for i, j in zip(ii, jj):
        a, b = owners[i], owners[j]
        if a != b and a < b:
            pair[(a, b)].append((i, j))
    floor_of = {}
    for a in areas:
        idx = np.nonzero(owner_idx == areas.index(a))[0]
        floor_of[a] = float(np.median(zs[idx])) if len(idx) else 0.0
    links = []
    for (a, b), lines in pair.items():
        hops = _hops(adj, a, b)
        if hops < 3:      # 2 hops = through a doorway or across a room: not a separate view
            continue
        i, j = max(lines, key=lambda ij: (zs[ij[0]] - zs[ij[1]]) ** 2 + (xs[ij[0]] - xs[ij[1]]) ** 2
                   + (ys[ij[0]] - ys[ij[1]]) ** 2)
        links.append({
            "a": a, "b": b, "sightlines": len(lines), "hops": int(hops),
            "cross_storey": abs(floor_of[a] - floor_of[b]) >= 128,
            "through_glass": bool(any(not V[x, y] for x, y in lines)),
            "from": [float(xs[i]), float(ys[i]), float(zs[i] + cfg.eye_height)],
            "to": [float(xs[j]), float(ys[j]), float(zs[j] + cfg.target_height)],
        })
    links.sort(key=lambda l: (-l["cross_storey"], -l["hops"], -l["sightlines"]))

    seen_from = defaultdict(set)
    for l in links:
        seen_from[l["b"]].add(l["a"])
        seen_from[l["a"]].add(l["b"])
    landmarks = sorted(({"area": a, "seen_from": len(v)} for a, v in seen_from.items() if a.startswith("room:")),
                       key=lambda d: -d["seen_from"])[:5]

    # previews along the critical path: first room from which each later room is visible
    main_rooms = [level.room_by_node(nid).id for nid in main]
    # the walk along the critical path: room, corridor, room, ... with the room index of each step
    walk = []
    for k, rid in enumerate(main_rooms):
        walk.append((f"room:{rid}", k))
        if k + 1 < len(main_rooms):
            nxt = main_rooms[k + 1]
            for c in level.corridors:
                if {c.a, c.b} == {rid, nxt}:
                    walk.append((f"corridor:{c.id}", k))
    visible = defaultdict(set)
    for (a, b), _ in pair.items():
        visible[a].add(b)
        visible[b].add(a)
    previews = []
    for k, rid in enumerate(main_rooms):
        target = f"room:{rid}"
        first = next((ri for area, ri in walk if ri < k and target in visible[area]), None)
        previews.append({"room": target, "steps_ahead": (k - first) if first is not None else 0})
    ahead = [p for p in previews[1:] if p["steps_ahead"] >= 2]
    return {
        "links": links[:40],
        "landmarks": landmarks,
        "previews": previews,
        "previewed_share": round(len(ahead) / max(1, len(previews) - 1), 3),
    }
