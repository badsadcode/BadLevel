"""Distinctness evaluation: distinct between presets, varied inside each one."""
import json

from badlevel.cli import main as cli_main
from badlevel.evaluate import evaluate_compositions, evaluate_presets, render_html


def test_presets_are_distinct_and_varied():
    rep = evaluate_presets(range(1, 4))
    assert not rep["failures"]
    assert rep["accuracy"] >= 0.85
    assert all(v > 0.5 for v in rep["variety"].values()), "no preset makes the same level every time"
    assert rep["separation"] > 1.0, "presets differ more from each other than levels within a preset"
    assert set(rep["confusion"]) == set(rep["labels"])
    page = render_html(rep, "t", "i")
    assert "Which get confused" in page and "Variety inside each label" in page


def test_compositions_differ_in_silhouette():
    rep = evaluate_compositions(range(1, 5))
    assert rep["silhouette_accuracy"] >= 0.45, "chance is 25%"


def test_cli_evaluate(tmp_path):
    assert cli_main(["evaluate", "presets", "--seeds", "1-2", "--out", str(tmp_path)]) == 0
    rep = json.loads((tmp_path / "evaluation.json").read_text())
    assert rep["format"] == "badlevel-evaluation" and (tmp_path / "index.html").exists()
