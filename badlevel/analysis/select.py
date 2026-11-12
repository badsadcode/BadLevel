"""Candidate selection: generate a few layouts, reject clearly bad ones.

This is deliberately a *gate*, not an optimiser. Candidates are tried in
order and the first one without ``problem`` flags wins. Nothing is pushed
toward a target exposure or visibility number, so variety, pacing and
intentional vulnerability survive. Custom gates can be passed in.
"""
from __future__ import annotations

import copy
from typing import Callable, List, Optional, Tuple

from ..config import GenConfig
from ..generator import GenerationError, generate
from ..level import Level
from .analyze import AnalysisConfig, analyze


def default_gate(report: dict) -> List[str]:
    """Return reasons to reject (empty list = accept)."""
    return [f["message"] for f in report["flags"] if f["severity"] == "problem"]


def generate_selected(cfg: GenConfig, candidates: int = 4,
                      gate: Callable[[dict], List[str]] = default_gate,
                      analysis_cfg: Optional[AnalysisConfig] = None) -> Tuple[Level, dict]:
    tried = []
    best = None
    for variant in range(max(1, candidates)):
        c = copy.deepcopy(cfg)
        c.variant = variant
        try:
            level = generate(c)
        except GenerationError as e:
            tried.append({"variant": variant, "rejected": [str(e)]})
            continue
        report = analyze(level, analysis_cfg)
        reasons = gate(report)
        tried.append({"variant": variant, "rejected": reasons,
                      "warnings": sum(1 for f in report["flags"] if f["severity"] == "warn")})
        score = (len(reasons), tried[-1]["warnings"])
        if best is None or score < best[0]:
            best = (score, level, report)
        if not reasons:
            break
    if best is None:
        raise GenerationError(f"seed {cfg.seed}: no candidate could be generated")
    _, level, report = best
    report["selection"] = {"candidates": tried, "chosen_variant": level.config.variant,
                           "passed_gate": best[0][0] == 0}
    level.notes.append(f"variant {level.config.variant} chosen from {len(tried)} candidate(s)")
    return level, report
