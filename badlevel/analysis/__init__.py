"""Level analysis: line of sight, exposure, tactics, diagnostics.

Requires numpy (``pip install badlevel[analysis]``); the generator itself does not.
"""
from .analyze import AnalysisConfig, analyze
from .diagnostics import RULES, diagnose
from .select import default_gate, generate_selected

__all__ = ["AnalysisConfig", "analyze", "diagnose", "RULES", "default_gate", "generate_selected"]
