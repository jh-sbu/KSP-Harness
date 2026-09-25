"""Socket client for the KSPHarness in-game plugin (newline-delimited JSON over TCP)."""

from __future__ import annotations

import itertools
import json
import os
import socket
import time
from typing import Any, Callable

DEFAULT_PORT = int(os.environ.get("KSP_HARNESS_PORT", "50555"))


class KSPError(RuntimeError):
    """The game rejected a command (bad args, wrong scene, ...)."""


class KSPConnectionError(ConnectionError):
    """The game is not running / the plugin is not listening."""


class KSP:
    """Connection to the running game.

    Every plugin command is available as a method, e.g. ``k.vessel()``,
    ``k.throttle(value=1)``, or generically via ``k.call("ap", mode="prograde")``.
    """

    def __init__(self, host: str = "127.0.0.1", port: int = DEFAULT_PORT, timeout: float = 75.0):
        self.host, self.port, self.timeout = host, port, timeout
        self._sock: socket.socket | None = None
        self._file = None
        self._ids = itertools.count(1)

    # ------------------------------------------------------------------ transport

    def connect(self) -> None:
        self.close()
        try:
            s = socket.create_connection((self.host, self.port), timeout=5)
        except OSError as e:
            raise KSPConnectionError(f"cannot reach KSPHarness on {self.host}:{self.port} ({e}); is the game running?") from e
        s.settimeout(self.timeout)
        self._sock = s
        self._file = s.makefile("rwb")

    def close(self) -> None:
        if self._sock is not None:
            try:
                self._sock.close()
            except OSError:
                pass
        self._sock = self._file = None

    def call(self, cmd: str, **args: Any) -> Any:
        args = {k: v for k, v in args.items() if v is not None}
        msg = json.dumps({"id": next(self._ids), "cmd": cmd, "args": args}).encode() + b"\n"
        for attempt in (0, 1):
            try:
                if self._sock is None:
                    self.connect()
                self._file.write(msg)
                self._file.flush()
                line = self._file.readline()
                if not line:
                    raise ConnectionResetError("connection closed by game")
                break
            except (OSError, ConnectionError) as e:
                self.close()
                if attempt == 1 or isinstance(e, KSPConnectionError):
                    raise KSPConnectionError(str(e)) from e
        resp = json.loads(line)
        if not resp.get("ok"):
            raise KSPError(f"{cmd}: {resp.get('error')}")
        return resp.get("result")

    def __getattr__(self, name: str) -> Callable[..., Any]:
        if name.startswith("_"):
            raise AttributeError(name)
        return lambda **kw: self.call(name, **kw)

    # ------------------------------------------------------------------ helpers

    def alive(self) -> bool:
        try:
            self.call("ping")
            return True
        except (KSPConnectionError, KSPError):
            return False

    def wait_for(self, pred: Callable[[], Any], timeout: float = 120, interval: float = 0.5, what: str = "condition") -> Any:
        """Poll ``pred`` until it returns a truthy value. Connection errors are retried (scene loads)."""
        deadline = time.time() + timeout
        last_err = None
        while time.time() < deadline:
            try:
                r = pred()
                if r:
                    return r
            except (KSPConnectionError, KSPError) as e:
                last_err = e
            time.sleep(interval)
        raise TimeoutError(f"timed out after {timeout}s waiting for {what}" + (f" (last error: {last_err})" if last_err else ""))

    def scene(self) -> str:
        return self.call("ping")["scene"]

    def wait_scene(self, scene: str, timeout: float = 180) -> None:
        """Wait until the given scene (e.g. FLIGHT, SPACECENTER) is loaded and the game is responsive."""
        scene = scene.upper()

        def ready():
            p = self.call("ping")
            if p["scene"] != scene:
                return False
            if scene == "FLIGHT":
                try:
                    self.call("vessel")
                except KSPError:
                    return False
            return True

        self.wait_for(ready, timeout, 1.0, f"scene {scene}")
        time.sleep(1.0)

    def ut(self) -> float:
        return self.call("ping")["ut"]

    def events_since(self, seq: int, type: str | None = None) -> list[dict]:
        return self.call("events", since=seq, type=type)["events"]

    def screenshot(self, path: str | None = None, hide_ui: bool = False, timeout: float = 10) -> str:
        """Capture a screenshot and wait until the file is fully written. Returns the path."""
        p = self.call("screenshot", path=path, hide_ui=hide_ui)
        deadline = time.time() + timeout
        last = -1
        while time.time() < deadline:
            if os.path.exists(p):
                size = os.path.getsize(p)
                if size > 0 and size == last:
                    return p
                last = size
            time.sleep(0.25)
        raise TimeoutError(f"screenshot {p} not written (is the game window minimized?)")
