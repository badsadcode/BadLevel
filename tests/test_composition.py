"""Composition: macro structures change the silhouette; big spaces get a compressed approach."""
import pytest

from badlevel import GenConfig, generate
from badlevel.fingerprint import SILHOUETTE, fingerprint, nearest_centroid_accuracy, normalise
from badlevel.recipes import PRESETS, compile_recipe
from badlevel.validate import validate

COMPS = ("tree", "hub", "central", "linear")


@pytest.fixture(scope="module")
def composed():
    return {c: [generate(GenConfig(seed=s, composition=c, side_rooms=4, pacing_contrast=True,
                                   mega_rooms=1 if c == "central" else 0)) for s in range(300, 308)]
            for c in COMPS}


def test_all_compositions_generate_valid_levels(composed):
    for comp, levels in composed.items():
        for lv in levels:
            assert validate(lv)["ok"], (comp, validate(lv)["errors"])


def test_silhouettes_are_recognisable(composed):
    labels, prints = [], []
    for comp, levels in composed.items():
        for lv in levels:
            labels.append(comp)
            prints.append(fingerprint(lv))
    acc = nearest_centroid_accuracy(labels, normalise(prints, SILHOUETTE))
    assert acc >= 0.55, f"compositions recognisable from silhouette alone only {acc:.0%} (chance 25%)"
    avg = lambda c, f: sum(fingerprint(lv)[f] for lv in composed[c]) / len(composed[c])
    assert avg("linear", "elongation") > avg("tree", "elongation") + 0.3, "a spine is long and thin"
    assert avg("hub", "centrality") > avg("tree", "centrality") + 0.1, "a hub gathers the connections"
    assert avg("central", "centrality") > avg("tree", "centrality") + 0.1


def test_radial_levels_grow_from_their_centre(composed):
    for comp in ("hub", "central"):
        for lv in composed[comp]:
            c = lv.graph.center
            assert c is not None
            degree = sum(1 for k in lv.corridors if c in (k.a, k.b)) + \
                sum(1 for e in lv.graph.edges if e.merged and c in (e.a, e.b))
            assert degree >= 3, f"{comp}: the centre should be a crossroads"


def test_compression_before_release(composed):
    from badlevel.analysis import analyze
    lv = composed["central"][0]
    big = next(r for r in lv.rooms if r.kind == "megaroom")
    into = [c for c in lv.corridors if big.id in (c.a, c.b)]
    assert into and all(c.radius == 1 and c.height == 112 for c in into), "the way in is narrow and low"
    rep = analyze(lv)
    assert rep["transitions"] and rep["transitions"][0]["contrast"] >= 10
    assert any(f["code"] == "release" for f in rep["flags"])


def test_default_stays_a_tree():
    assert GenConfig().composition == "tree" and not GenConfig().pacing_contrast
    assert compile_recipe(PRESETS["compact"]).composition == "linear"
    assert compile_recipe(PRESETS["industrial"]).composition == "hub"
    assert compile_recipe(PRESETS["compound"]).composition == "central"
