"""Top-level pipeline: seed + config -> validated Level."""
from __future__ import annotations

import random

from .config import GenConfig
from .decorate import decorate
from .graph import build_graph
from .layout import LayoutError, build_layout
from .level import Level
from .validate import validate


class GenerationError(Exception):
    pass


def generate(cfg: GenConfig = None, **overrides) -> Level:
    cfg = cfg or GenConfig()
    for k, v in overrides.items():
        setattr(cfg, k, v)
    master = random.Random(cfg.seed)
    last = None
    for attempt in range(cfg.max_attempts):
        rng = random.Random(master.getrandbits(64))
        try:
            graph = build_graph(cfg, rng)
            level = build_layout(graph, cfg, rng, cfg.seed)
            decorate(level, rng)
        except LayoutError as e:
            last = str(e)
            continue
        report = validate(level)
        if report["ok"]:
            level.notes.append(f"attempts: {attempt + 1}")
            level.report = report
            return level
        last = "; ".join(report["errors"])
    raise GenerationError(f"seed {cfg.seed}: no valid level after {cfg.max_attempts} attempts ({last})")
