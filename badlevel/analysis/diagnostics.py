"""Turn analysis metrics into human-readable flags.

Flags point at *potential* problems and notable features; they are not a fun
score. Severity:
  info     - worth knowing, often intentional (a sniper lane, a high-ground perch)
  warn     - likely weak spot, review it (an arena with one way in and no cover)
  problem  - almost certainly bad; the candidate gate rejects these

Variety is a goal: some rules fire when the level is too *uniform*, not just
when one room is bad. Thresholds live in RULES so they can be tuned.
"""
from __future__ import annotations

import statistics

RULES = {
    "fight_kinds": ("arena", "combat", "hub", "courtyard"),
    "single_approach_min_enemies": 3,
    "no_reposition_min_area": 60.0,
    "no_reposition_mutual": 0.9,
    "overexposed_red_share": 0.8,
    "dominant_share": 0.75,
    "dominant_height": 64.0,
    "sniper_lane": 1280.0,           # 40 m
    "flat_pacing_stdev": 25.0,        # m^2 of exposure across the critical path
    "flat_level_height_levels": 2,
    "monotone_corridor": 40,          # cells (= metres) of corridor where nothing changes
}


def _flag(severity, code, message, room=None, pos=None):
    f = {"severity": severity, "code": code, "message": message}
    if room is not None:
        f["room"] = room
    if pos is not None:
        f["pos"] = pos
    return f


def diagnose(report: dict, rules: dict = None) -> list:
    R = dict(RULES, **(rules or {}))
    flags = []
    combat = report.get("purpose", "combat") == "combat"
    for r in report["rooms"]:
        if not combat:
            # exploration: rooms are judged as places to move through and look at, not fights
            if r["entrances"] == 1 and r["kind"] not in ("start", "exit", "key", "secret") and \
                    r["pickups"] == 0 and not r["high_ground"]:
                flags.append(_flag("info", "empty_dead_end", f'{r["kind"]} room {r["id"]}: dead end with nothing '
                                                             "to find or see.", room=r["id"]))
            continue
        name = f'{r["kind"]} room {r["id"]}'
        fight = r["kind"] in R["fight_kinds"] or r["enemies"] >= R["single_approach_min_enemies"]
        if fight and r["entrances"] == 1 and r["enemies"] >= R["single_approach_min_enemies"]:
            no_options = r["cover_spots"] == 0 and r["height_levels"] <= 1
            flags.append(_flag(
                "problem" if (no_options and r["kind"] == "arena") else "warn", "single_approach",
                f"{name}: only one way in with {r['enemies']} enemies"
                + (" and no cover or height to use" if no_options else "")
                + ". Consider a second entrance or a vantage point.", room=r["id"]))
        if (fight and r["area_m2"] >= R["no_reposition_min_area"] and r["mutual_visibility"] >= R["no_reposition_mutual"]
                and r["cover_spots"] == 0 and r["height_levels"] <= 1):
            flags.append(_flag("warn", "no_repositioning",
                               f"{name}: everyone sees everyone ({r['mutual_visibility']:.0%}), no cover, one floor "
                               f"height. Little reason to move. Add pillars, a platform or a pit.", room=r["id"]))
        if r["samples"] >= 6 and r["exposure_share"]["red"] >= R["overexposed_red_share"] and fight:
            flags.append(_flag("warn", "overexposed",
                               f"{name}: {r['exposure_share']['red']:.0%} of the floor is highly exposed.",
                               room=r["id"]))
        hg = r["high_ground"]
        if hg and hg["room_share_seen"] >= R["dominant_share"] and hg["height_adv"] >= R["dominant_height"]:
            flags.append(_flag("info", "dominant_high_ground",
                               f"{name}: one perch sees {hg['room_share_seen']:.0%} of the room from "
                               f"{hg['height_adv']:.0f} units up. Great if intentional, give it a cost or a counter.",
                               room=r["id"], pos=hg["pos"]))
        if fight and r["entrances"] >= 2 and r["flank_pairs"] > 0 and r["on_cycle"]:
            flags.append(_flag("info", "flank_route",
                               f"{name}: {r['flank_pairs']} entrance pair(s) out of each other's sight, on a loop. "
                               f"Flanking is possible.", room=r["id"]))
        if r["entrances"] == 1 and r["enemies"] == 0 and r["pickups"] == 0 and r["kind"] not in ("start", "exit", "key"):
            flags.append(_flag("info", "empty_dead_end", f"{name}: dead end with nothing in it.", room=r["id"]))

    snipers = [l for l in report["lanes"] if l["length"] >= R["sniper_lane"]] if combat else []
    for lane in snipers[:3]:
        flags.append(_flag("info", "sniper_lane",
                           f"{lane['length'] / 32:.0f} m uninterrupted firing lane ({', '.join(lane['areas'])}).",
                           pos=[(a + b) / 2 for a, b in zip(lane["a"], lane["b"])]))
    if len(snipers) > 3:
        flags.append(_flag("info", "sniper_lane", f"...and {len(snipers) - 3} more lanes over "
                                                  f"{R['sniper_lane'] / 32:.0f} m (see the lanes overlay)."))

    chokes = [c for c in report["chokepoints"] if c["mandatory"]]
    if chokes:
        flags.append(_flag("info", "mandatory_choke",
                           f"{len(chokes)} corridor(s) are the only route to the exit "
                           f"({', '.join(str(c['corridor']) for c in chokes)}). Every player passes there; "
                           "good spots for set-piece fights, or add loops to soften them."))

    lv = report["level"]
    expo = [p["exposure_mean"] for p in lv["pacing"] if p["kind"] not in ("start", "exit")]
    if len(expo) >= 3 and statistics.pstdev(expo) < R["flat_pacing_stdev"]:
        flags.append(_flag("warn", "flat_pacing",
                           "Critical path rooms all feel alike (exposure varies by only "
                           f"{statistics.pstdev(expo):.0f} m²). Mix tight and open spaces."))
    if lv["height_levels"] <= R["flat_level_height_levels"]:
        flags.append(_flag("warn", "flat_level", "Almost no height variation in the whole level."))
    if lv["loops"] == 0:
        flags.append(_flag("warn", "no_loops", "No loops: the level is a pure tree, lots of backtracking."))
    for c in report.get("corridors", []):
        if c["monotone_run"] >= R.get("monotone_corridor", 40):
            flags.append(_flag("warn", "monotone_corridor",
                               f"corridor {c['id']}: {c['monotone_run']} m with nothing changing (no turn, step, "
                               "window or beat). Fine for a deliberate sniper alley; otherwise add a beat."))
    vis = report.get("visual")
    if vis:
        storeys = report.get("storeys", 1)
        cross = [l for l in vis["links"] if l["cross_storey"]]
        if storeys > 1 and not cross:
            flags.append(_flag("warn", "no_vertical_landmarks",
                               "Nothing on another storey can be seen from anywhere. Add a window, gallery or atrium "
                               "so players see where they are going."))
        for l in cross[:2]:
            flags.append(_flag("info", "vertical_view",
                               f"{l['a']} looks onto {l['b']} on another storey ({l['hops']} rooms/corridors apart"
                               + (", through glass" if l["through_glass"] else "") + ").",
                               pos=[(x + y) / 2 for x, y in zip(l["from"], l["to"])]))
        if vis["landmarks"] and vis["landmarks"][0]["seen_from"] >= 3:
            lm = vis["landmarks"][0]
            flags.append(_flag("info", "landmark", f"{lm['area']} is a landmark: seen from {lm['seen_from']} areas "
                                                   "you can't walk to directly."))
        far = [p for p in vis["previews"] if p["steps_ahead"] >= 2]
        if far:
            best = max(far, key=lambda p: p["steps_ahead"])
            flags.append(_flag("info", "preview", f"{best['room']} can be seen {best['steps_ahead']} rooms before "
                                                  "you reach it."))
        elif len(vis["previews"]) > 3:
            flags.append(_flag("info" if combat else "warn", "no_previews", "No room on the critical path can be seen before you are "
                                                      "next to it."))
    order = {"problem": 0, "warn": 1, "info": 2}
    flags.sort(key=lambda f: order[f["severity"]])
    return flags
