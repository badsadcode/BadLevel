"""Weighted choices: the built-in option lists stay the baseline, recipes multiply them."""
from __future__ import annotations

import random
from collections import Counter
from typing import Dict, Sequence, TypeVar

T = TypeVar("T")


def weighted(rng: random.Random, options: Sequence[T], bias: Dict = None, key=lambda o: o) -> T:
    """Pick from ``options`` (duplicates = base weight) scaled by ``bias[key(option)]`` (default 1).
    If the bias zeroes everything out, fall back to the plain list."""
    counts = Counter(range(len(options)))
    uniq, weights = [], []
    seen = {}
    for i, o in enumerate(options):
        k = key(o)
        if k in seen:
            weights[seen[k]] += 1
            continue
        seen[k] = len(uniq)
        uniq.append(o)
        weights.append(1)
    bias = bias or {}
    w = [base * float(bias.get(key(o), bias.get(str(key(o)), 1.0))) for o, base in zip(uniq, weights)]
    if sum(w) <= 0:
        return rng.choice(list(options))
    return rng.choices(uniq, w)[0]
