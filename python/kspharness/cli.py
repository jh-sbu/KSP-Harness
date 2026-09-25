"""Command line interface.

    ksp <command> [key=value ...]     call any plugin command (values parsed as JSON when possible)
    ksp help [command]                list plugin commands
    ksp install | start | stop        manage plugin + game process
    ksp shot [path]                   screenshot, prints the PNG path
    ksp wait <SCENE>                  block until a scene is loaded
    ksp log [n] [grep]                tail KSP.log
    ksp run <routine> [key=value ...] run a high-level flight routine (see kspharness.flight)
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
        elif cmd == "help" and rest:
            emit(KSP().call("help", name=rest[0]))
        elif cmd == "run":
            if not rest:
                emit({name: (fn.__doc__ or "").strip().splitlines()[0] for name, fn in flight.ROUTINES.items()})
                return 0
            fn = flight.ROUTINES.get(rest[0])
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
