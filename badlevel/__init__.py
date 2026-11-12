"""BadLevel - procedural Quake-style FPS level blockouts, engine agnostic."""
from .config import GenConfig
from .generator import GenerationError, generate

__version__ = "0.1.0"
__all__ = ["GenConfig", "generate", "GenerationError", "__version__"]
