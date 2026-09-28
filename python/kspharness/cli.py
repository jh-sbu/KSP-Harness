"""Command line interface.

    ksp <command> [key=value ...]     call any plugin command (values parsed as JSON when possible)
    ksp help [command]                list plugin commands
    ksp install | start | stop        manage plugin + game process
    ksp shot [path]                   screenshot, prints the PNG path
    ksp wait <SCENE>                  block until a scene is loaded
    ksp log [n] [grep]                tail KSP.log
    ksp watch [until=re] [fail=re]    stream events live; exit 0 on until, 3 on alert/failure, 4 on timeout
    ksp run <routine> [key=value ...] run a high-level flight routine (see kspharness.flight)
    ksp build <design> [save=<folder>] write a craft file from kspharness.designs into the save's VAB folder
"""

from __future__ import annotations

import json
import sys

from . import flight, gamectl
from .client import KSP, KSPConnectionError, KSPError


def parse_value(s: str):
    try:
        return json.loads(s)
    except json.JSONDecodeError:
        return s


def parse_kv(argv: list[str]) -> dict:
    out = {}
    for a in argv:
        if "=" not in a:
            raise SystemExit(f"argument '{a}' must be key=value")
        k, v = a.split("=", 1)
        out[k] = parse_value(v)
    return out


def emit(x) -> None:
    if isinstance(x, str):
        print(x)
    else:
        print(json.dumps(x, indent=1, ensure_ascii=False))


def watch(k: KSP, until: str | None = None, fail: str = r"^alert\.|^vessel\.crash|^crew\.killed|^autopilot\.error",
          timeout: float = 3600, since: int | None = None, quiet: str = r"^log\.|^scene\.requested") -> int:
    """Stream game events, one per line. Exit 0 when `until` (regex on "type: msg") matches,
    3 when `fail` matches, 4 on timeout. Designed to be run under a monitor so every event is seen live."""
    import re
    import time

    seq = k.call("ping")["event_seq"] if since is None else since
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            evs = k.events_since(seq)
        except KSPConnectionError:
            time.sleep(2)  # scene loads can briefly drop the connection
            continue
        for e in evs:
            seq = e["seq"]
            line = f"{e['type']}: {e['msg']}"
            if not re.search(quiet, e["type"]):
                print(f"{e['real_time']} {line}", flush=True)
            if fail and re.search(fail, line):
                print(f"WATCH FAIL: {line}", flush=True)
                return 3
            if until and re.search(until, line):
                return 0
        time.sleep(0.5)
    print("WATCH TIMEOUT", flush=True)
    return 4


def main(argv: list[str] | None = None) -> int:
    argv = sys.argv[1:] if argv is None else argv
    if not argv or argv[0] in ("-h", "--help"):
        print(__doc__)
        return 0
    cmd, rest = argv[0], argv[1:]
    try:
        if cmd == "install":
            emit(str(gamectl.build_and_install()))
        elif cmd == "start":
            emit(gamectl.start(wait="nowait" not in rest))
        elif cmd == "stop":
            emit(gamectl.stop())
        elif cmd == "restart":
            gamectl.stop()
            emit(gamectl.start())
        elif cmd == "log":
            n = int(rest[0]) if rest else 50
            emit(gamectl.ksp_log_tail(n, rest[1] if len(rest) > 1 else None))
        elif cmd == "shot":
            emit(KSP().screenshot(rest[0] if rest else None))
        elif cmd == "wait":
            KSP().wait_scene(rest[0])
            emit(f"{rest[0]} ready")
        elif cmd == "watch":
            return watch(KSP(), **parse_kv(rest))
        elif cmd == "build":
            from . import designs
            if not rest or rest[0] not in designs.DESIGNS:
                raise SystemExit(f"designs: {', '.join(designs.DESIGNS)}")
            k = KSP()
            kv = parse_kv(rest[1:])
            save = kv.get("save") or k.status()["game"]["save_folder"]
            c = designs.DESIGNS[rest[0]](k)
            path = c.save(gamectl.KSP_DIR / "saves" / save / "Ships" / "VAB" / f"{c.name}.craft")
            emit({"path": str(path), **c.summary()})
        elif cmd == "help" and rest:
            emit(KSP().call("help", name=rest[0]))
        elif cmd == "run":
            if not rest:
                emit({name: (fn.__doc__ or "").strip().splitlines()[0] for name, fn in flight.ROUTINES.items()})
                return 0
            from . import devtools, mission
            fn = {**flight.ROUTINES, **devtools.ROUTINES, "eve_mission": mission.eve_mission}.get(rest[0])
            if fn is None:
                raise SystemExit(f"unknown routine {rest[0]}; known: {', '.join(flight.ROUTINES)}")
            result = fn(KSP(), **parse_kv(rest[1:]))
            if result is not None:
                emit(result)
        else:
            emit(KSP().call(cmd, **parse_kv(rest)))
    except KSPConnectionError as e:
        print(f"connection error: {e}", file=sys.stderr)
        return 2
    except KSPError as e:
        print(f"error: {e}", file=sys.stderr)
        return 1
    except flight.MissionError as e:
        print(f"mission error: {e}", file=sys.stderr)
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
