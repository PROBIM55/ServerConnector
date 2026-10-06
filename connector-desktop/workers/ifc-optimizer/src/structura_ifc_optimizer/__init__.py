"""Structura IFC Optimizer engine."""

from .analysis import analyze_ifc
from .pipeline import optimize_ifc

__all__ = ["analyze_ifc", "optimize_ifc"]
__version__ = "0.1.2"
