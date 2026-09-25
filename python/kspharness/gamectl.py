"""Install the plugin and start/stop the game process."""

from __future__ import annotations

import os
import shutil
import signal
import subprocess
import time
from pathlib import Path

from .client import KSP

REPO = Path(__file__).resolve().parents[2]
KSP_DIR = Path(os.environ.get("KSP_DIR", Path.home() / ".local/share/Steam/steamapps/common/Kerbal Space Program"))
PLUGIN_DST = KSP_DIR / "GameData" / "KSPHarness" / "Plugins"
LOG_DIR = REPO / "logs"


def build_and_install() -> Path:
    """Build the C# plugin and copy it into GameData. Takes effect on next game start."""
    proj = REPO / "plugin" / "KSPHarness.csproj"
    subprocess.run(["dotnet", "build", "-c", "Release", str(proj), "-p:KSPDir=" + str(KSP_DIR)], check=True,
                   stdout=subprocess.DEVNULL if not os.environ.get("VERBOSE") else None)
    dll = REPO / "plugin" / "bin" / "Release" / "net472" / "KSPHarness.dll"
    PLUGIN_DST.mkdir(parents=True, exist_ok=True)
    shutil.copy2(dll, PLUGIN_DST / dll.name)
    return PLUGIN_DST / dll.name


def game_pids() -> list[int]:
    out = subprocess.run(["pgrep", "-f", "KSP.x86_64"], capture_output=True, text=True).stdout
    return [int(p) for p in out.split()]


def start(wait: bool = True, timeout: float = 600) -> str:
    """Launch KSP directly (no Steam launcher) and optionally wait until the harness answers at the main menu."""
    if game_pids():
        return "already running"
    LOG_DIR.mkdir(exist_ok=True)
    env = dict(os.environ)
    env["LC_ALL"] = "C"  # KSP misparses numbers under locales with decimal commas
    stdout = open(LOG_DIR / "ksp-stdout.log", "wb")
    subprocess.Popen([str(KSP_DIR / "KSP.x86_64")], cwd=str(KSP_DIR), env=env, stdout=stdout, stderr=subprocess.STDOUT,
                     start_new_session=True)
    if not wait:
        return "started"
    k = KSP()
    t0 = time.time()
    k.wait_for(lambda: k.call("ping")["scene"] == "MAINMENU", timeout, 2.0, "game to reach the main menu")
    return f"ready at main menu after {time.time() - t0:.0f}s"


def stop(timeout: float = 30) -> str:
    pids = game_pids()
    if not pids:
        return "not running"
    for p in pids:
        os.kill(p, signal.SIGTERM)
    deadline = time.time() + timeout
    while time.time() < deadline and game_pids():
        time.sleep(0.5)
    for p in game_pids():
        os.kill(p, signal.SIGKILL)
    return "stopped"


def ksp_log_tail(n: int = 50, grep: str | None = None) -> str:
    lines = (KSP_DIR / "KSP.log").read_text(errors="replace").splitlines()
    if grep:
        lines = [l for l in lines if grep.lower() in l.lower()]
    return "\n".join(lines[-n:])
