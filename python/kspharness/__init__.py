"""Python side of the KSP harness: talk to the KSPHarness plugin running inside Kerbal Space Program."""

from .client import KSP, KSPConnectionError, KSPError

__all__ = ["KSP", "KSPError", "KSPConnectionError"]
