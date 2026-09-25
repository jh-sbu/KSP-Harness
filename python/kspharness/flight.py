"""High-level flight routines built on the plugin's primitives.

Each routine takes a connected ``KSP`` client plus keyword arguments, blocks until it is
finished, prints progress lines, and raises ``MissionError`` if something goes wrong.
Run them from the shell with ``ksp run <routine> key=value ...``.
"""

from __future__ import annotations

import math
import sys
import time
from pathlib import Path

from .client import KSP, KSPError


class MissionError(RuntimeError):
    pass


def log(msg: str) -> None:
    print(time.strftime("[%H:%M:%S] ") + msg, flush=True)


class EventWatch:
    """Tracks game events since construction and raises on catastrophic ones."""

    FATAL = ("vessel.crash", "crew.killed")
    # Alerts that stop a routine by default: flight-safety problems that a routine can't explain.
    # (alert.dialog is informational; alert.flameout is expected during staging.)
    FATAL_ALERTS = ("alert.no_thrust", "alert.node_exec")

    DIALOG_CHECK_INTERVAL = 10.0

    def __init__(self, k: KSP):
        self.k = k
        self.seq = k.call("ping")["event_seq"]
        self._last_dialog_check = 0.0
        self._seen_dialogs: set[str] = set()

    def safe(self) -> None:
        """Put the vessel in a passive state before aborting a routine."""
        for cmd, kw in (("throttle", {"value": 0}), ("ap", {"mode": "off"}), ("autostage", {"on": False})):
            try:
                self.k.call(cmd, **kw)
            except Exception:
                pass

    def check_dialogs(self) -> list[dict]:
        """Log any newly opened dialog/window so stray UI never goes unnoticed."""
        self._last_dialog_check = time.time()
        ds = self.k.dialogs()
        for d in ds:
            key = f"{d['name']}|{d['title']}"
            if key not in self._seen_dialogs:
                self._seen_dialogs.add(key)
                log(f"  dialog open: {d['title']!r} buttons={d['buttons']}")
        self._seen_dialogs &= {f"{d['name']}|{d['title']}" for d in ds}
        return ds

    def poll(self, fatal: bool = True) -> list[dict]:
        if time.time() - self._last_dialog_check > self.DIALOG_CHECK_INTERVAL:
            self.check_dialogs()
        evs = self.k.events_since(self.seq)
        if evs:
            self.seq = evs[-1]["seq"]
        for e in evs:
            if e["type"].startswith("alert."):
                log(f"  ALERT {e['type']}: {e['msg']}")
            elif e["type"] == "screen.message":
                log(f"  on screen: {e['msg']}")
            elif e["type"] in ("vessel.staged", "autostage", "vessel.soi", "vessel.situation", "node.exec", "land",
                             "part.destroyed", "vessel.crash", "crew.killed", "autopilot.error", "science.received"):
                log(f"  event {e['type']}: {e['msg']}")
            if fatal and (e["type"] in self.FATAL or e["type"] in self.FATAL_ALERTS):
                self.safe()
                raise MissionError(f"{e['type']}: {e['msg']}")
        return evs


SHOT_DIR = Path(__file__).resolve().parents[2] / "logs" / "shots"


def milestone_shot(k: KSP, label: str) -> str | None:
    """Save a screenshot named after a mission milestone into logs/shots/ and log its path."""
    SHOT_DIR.mkdir(parents=True, exist_ok=True)
    path = SHOT_DIR / f"{time.strftime('%Y%m%d_%H%M%S')}_{label}.png"
    try:
        p = k.screenshot(str(path))
    except (TimeoutError, KSPError) as e:
        log(f"  screenshot failed: {e}")
        return None
    log(f"  screenshot: {p}")
    return p


def _body(k: KSP, name: str) -> dict:
    return k.bodies(name=name)[0]


def _last_engine_stage(k: KSP) -> int:
    """Lowest stage number that still has delta-v (i.e. the final propulsive stage)."""
    stages = [s for s in k.stages() if s["dv_vac"] > 1]
    return min((s["stage"] for s in stages), default=0)


# ---------------------------------------------------------------------------- routines


def ascend(k: KSP, altitude: float = 80000, heading: float = 90, turn_end: float | None = None,
           turn_speed: float = 60, turn_exponent: float = 0.5, max_aoa: float = 5, circularize: bool = True,
           autostage_stop: int | None = None, launch: bool = True) -> dict:
    """Launch (if on the pad) and fly a gravity turn to a circular orbit at `altitude` metres."""
    v = k.vessel()
    body = _body(k, v["body"])
    atmo = body["atmosphere_depth"]
    if turn_end is None:
        turn_end = atmo * 0.75 if atmo else max(altitude * 0.5, 8000)
    stop = autostage_stop if autostage_stop is not None else _last_engine_stage(k)
    watch = EventWatch(k)
    k.autostage(on=True, stop_at=stop)
    k.ap(mode="pitch_heading", pitch=90, heading=heading)
    k.throttle(value=1)
    # Only stage to "launch" if nothing is ignited yet (on the pad). A landed lander with an active
    # engine must not stage: that would fire its decoupler.
    if launch and v["situation"] in ("PRELAUNCH", "LANDED", "SPLASHED") and v["max_thrust"] == 0:
        log(f"launch: target {altitude/1000:.0f} km, heading {heading}, turn end {turn_end/1000:.0f} km, autostage down to {stop}")
        k.stage()
    h0 = v["altitude"]
    last_report = 0.0
    while True:
        watch.poll()
        v = k.vessel()
        alt, ap, spd = v["altitude"], v["orbit"]["apoapsis"], v["surface_speed"]
        if spd < turn_speed:
            pitch = 90.0
        else:
            frac = min(1.0, max(0.0, (alt - h0) / (turn_end - h0)))
            pitch = 90.0 * (1 - frac ** turn_exponent)
        # don't stray far from the airflow while the air is thick
        vs, hs = v["vertical_speed"], v["horizontal_speed"]
        if spd > 30 and v["dynamic_pressure_kpa"] > 2:
            pro = math.degrees(math.atan2(vs, max(hs, 1e-3)))
            pitch = max(pro - max_aoa, min(pro + max_aoa, pitch))
        k.ap(mode="pitch_heading", pitch=round(pitch, 2), heading=heading)

        if ap >= altitude:
            k.throttle(value=0)
            if alt > atmo:
                break
        elif ap > altitude * 0.95:
            k.throttle(value=max(0.1, (altitude - ap) / (altitude * 0.05)))
        else:
            k.throttle(value=1)

        if time.time() - last_report > 5:
            last_report = time.time()
            log(f"alt {alt/1000:6.1f} km  ap {ap/1000:6.1f} km  speed {spd:6.0f}  pitch {pitch:5.1f}  q {v['dynamic_pressure_kpa']:5.1f} kPa  stage {v['stage']}")
        if v["situation"] in ("LANDED", "SPLASHED") and alt - h0 < 5 and spd < 1 and v["max_thrust"] == 0:
            raise MissionError("no thrust on the pad")
        time.sleep(0.2)
    milestone_shot(k, "ascent_coast")
    log(f"out of atmosphere: ap {v['orbit']['apoapsis']/1000:.1f} km, pe {v['orbit']['periapsis']/1000:.1f} km")
    if circularize:
        return circularize_orbit(k)
    return k.vessel()["orbit"]


def execute_node(k: KSP, tolerance: float = 0.1, warp: bool = True, timeout: float = 7 * 24 * 3600) -> dict:
    """Execute the next maneuver node with the in-game node executor and wait for completion."""
    nodes = k.nodes()
    if not nodes:
        raise MissionError("no maneuver node")
    n = nodes[0]
    log(f"executing node: dv {n['dv']:.1f} m/s in {n['in']:.0f}s (est. burn {n['burn_time_estimate']:.0f}s)")
    watch = EventWatch(k)
    k.node_exec(tolerance=tolerance, warp=warp)
    last, last_status = 0.0, ""
    t0 = time.time()
    best_dv, best_dv_time = float("inf"), time.time()
    while True:
        watch.poll()
        st = k.ap_status()
        # progress watchdog: while burning, remaining dv must keep shrinking
        if st["node_exec"].startswith("burning") and "dv_remaining" in st:
            if st["dv_remaining"] < best_dv - 0.05:
                best_dv, best_dv_time = st["dv_remaining"], time.time()
            elif time.time() - best_dv_time > 20:
                k.ap(mode="off")
                k.throttle(value=0)
                raise MissionError(f"burn stalled: dv remaining stuck at {st['dv_remaining']} for 20s")
        if not st["node_exec"].startswith(("aligning", "burning", "realigning", "starting", "waiting", "no thrust")):
            break
        if time.time() - last > 10 or (st["node_exec"].split(",")[0] != last_status):
            last = time.time()
            last_status = st["node_exec"].split(",")[0]
            log(f"  {st['node_exec']}  (pointing error {st['error_deg']})")
        if time.time() - t0 > timeout:
            raise MissionError("node execution timed out")
        time.sleep(0.5)
    if "aborted" in st["node_exec"]:
        raise MissionError(st["node_exec"])
    log(f"node done: {st['node_exec']}")
    milestone_shot(k, "node_done")
    o = k.vessel()["orbit"]
    log(f"orbit: ap {o['apoapsis']/1000:.1f} km, pe {o['periapsis']/1000:.1f} km, body {o['body']}")
    return o


def circularize_orbit(k: KSP, at: str = "apoapsis") -> dict:
    """Plan and execute a circularization burn at apoapsis (or periapsis)."""
    k.node_clear()
    n = k.plan_circularize(at=at)
    log(f"circularize at {at}: {n['dv']:.1f} m/s")
    return execute_node(k)


def change_apsis(k: KSP, which: str = "periapsis", altitude: float = 0, at: str | None = None) -> dict:
    """Plan and execute a burn that sets the periapsis/apoapsis altitude."""
    k.node_clear()
    n = k.plan_apsis(which=which, altitude=altitude, at=at)
    log(f"set {which} to {altitude/1000:.1f} km: {n['dv']:.1f} m/s")
    return execute_node(k)


def warp_to_soi(k: KSP, margin: float = 60) -> dict:
    """Time-warp to the next sphere-of-influence change."""
    o = k.vessel()["orbit"]
    if "time_to_transition" not in o:
        raise MissionError("no SOI transition ahead")
    start_body = o["body"]
    ut = k.ut() + o["time_to_transition"]
    log(f"warping {o['time_to_transition']:.0f}s to leave {start_body}'s SOI")
    k.warp(to_ut=ut + margin)
    k.wait_for(lambda: k.vessel()["body"] != start_body and k.status()["warp"]["index"] == 0,
               timeout=600, interval=2, what="SOI change")
    o = k.vessel()["orbit"]
    log(f"now orbiting {o['body']}: pe {o['periapsis']/1000:.1f} km")
    return o


def transfer(k: KSP, target: str = "Mun", periapsis: float | None = None, orbit_altitude: float | None = None) -> dict:
    """Hohmann transfer to a moon, capture into orbit at the encounter periapsis."""
    k.node_clear()
    plan = k.plan_hohmann(target=target, periapsis=periapsis)
    if "warning" in plan:
        raise MissionError(plan["warning"])
    log(f"transfer to {target}: {plan['node']['dv']:.1f} m/s, encounter pe {plan['node'].get('encounter', {}).get('periapsis')}")
    execute_node(k)
    o = k.vessel()["orbit"]
    if o.get("next_body") != target:
        raise MissionError(f"no encounter with {target} after burn: {o}")
    warp_to_soi(k)
    # correct periapsis if it drifted
    o = k.vessel()["orbit"]
    body = _body(k, target)
    want = periapsis if periapsis is not None else (body["atmosphere_depth"] + body["radius"] * 0.1)
    if abs(o["periapsis"] - want) > max(2000, want * 0.1):
        k.node_clear()
        k.plan_apsis(which="periapsis", altitude=want, at="now")
        log(f"correcting periapsis {o['periapsis']/1000:.1f} -> {want/1000:.1f} km")
        execute_node(k)
    k.node_clear()
    k.plan_circularize(at="periapsis")
    execute_node(k)
    if orbit_altitude:
        change_apsis(k, "apoapsis", orbit_altitude, at="periapsis")
        circularize_orbit(k, at="apoapsis")
    return k.vessel()["orbit"]


def land(k: KSP, deorbit_periapsis: float | None = None, touch_speed: float = 1.5, margin: float = 0.6) -> dict:
    """Powered landing on an airless body: deorbit burn (if in orbit), coast, suicide burn."""
    v = k.vessel()
    body = _body(k, v["body"])
    if body["atmosphere"]:
        log("warning: body has an atmosphere; the landing autopilot ignores drag")
    watch = EventWatch(k)
    if v["orbit"]["periapsis"] > 0:
        pe = deorbit_periapsis if deorbit_periapsis is not None else -body["radius"] * 0.2
        change_apsis(k, "periapsis", pe, at="now")
    v = k.vessel()
    # warp down until fairly low
    if v["altitude"] > 15000 and v["vertical_speed"] < 0:
        log("coasting down")
        k.warp(index=3)
        k.wait_for(lambda: k.vessel()["altitude"] < max(15000, body["radius"] * 0.05), timeout=3600, interval=1,
                   what="descent to 15 km")
        k.warp(index=0)
    k.land(touch_speed=touch_speed, margin=margin)
    log("landing autopilot engaged")
    last = 0.0
    while True:
        watch.poll()
        st = k.ap_status()
        if st.get("land") == "landed" and st["mode"] == "off":
            break
        if time.time() - last > 3:
            last = time.time()
            v = k.vessel()
            log(f"  {st.get('land')}  radar {v['radar_altitude']:.0f} m  vs {v['vertical_speed']:.1f}  hs {v['horizontal_speed']:.1f}")
        time.sleep(0.3)
    v = k.vessel()
    milestone_shot(k, "landed")
    log(f"landed: {v['situation']} on {v['body']} at {v['latitude']:.3f}, {v['longitude']:.3f} ({v['biome']})")
    return {"situation": v["situation"], "body": v["body"], "lat": v["latitude"], "lon": v["longitude"]}


def reenter(k: KSP, periapsis: float = 30000, stage_to_capsule: bool = True) -> dict:
    """Return to the ground through an atmosphere with parachutes: lower periapsis, drop stages, arm chutes."""
    v = k.vessel()
    body = _body(k, v["body"])
    if not body["atmosphere"]:
        raise MissionError("no atmosphere here; use land")
    watch = EventWatch(k)
    k.autostage(on=False)
    if v["orbit"]["periapsis"] > periapsis:
        change_apsis(k, "periapsis", periapsis, at="now" if v["orbit"]["eccentricity"] < 0.01 else "apoapsis")
    k.ap(mode="retrograde")
    if stage_to_capsule:
        # drop everything below the parachute stage once the burn is done
        while True:
            v = k.vessel()
            if v["stage"] <= 1:
                break
            k.stage()
            time.sleep(1.5)
            watch.poll()
    log("coasting to atmosphere")
    v = k.vessel()
    if v["altitude"] > body["atmosphere_depth"]:
        k.warp(to_ut=k.ut() + max(0, _time_to_altitude(k, body["atmosphere_depth"])) - 30)
        k.wait_for(lambda: k.vessel()["altitude"] < body["atmosphere_depth"] + 5000 or k.status()["warp"]["index"] == 0,
                   timeout=3600, interval=2, what="approach to atmosphere")
    k.warp(index=0)
    k.ap(mode="srf_retrograde")
    k.parachutes()
    log("in atmosphere; holding retrograde, chutes armed")
    last = 0.0
    while True:
        watch.poll()
        v = k.vessel()
        if v["situation"] in ("LANDED", "SPLASHED"):
            break
        if time.time() - last > 5:
            last = time.time()
            log(f"  alt {v['altitude']/1000:.1f} km  speed {v['surface_speed']:.0f}  situation {v['situation']}")
        if v["altitude"] < 5000 and v["surface_speed"] < 300:
            k.parachutes()
        time.sleep(0.5)
    k.ap(mode="off")
    milestone_shot(k, "touchdown")
    log(f"down: {v['situation']} at {v['latitude']:.3f}, {v['longitude']:.3f}")
    return {"situation": v["situation"], "lat": v["latitude"], "lon": v["longitude"]}


def return_home(k: KSP, periapsis: float = 30000) -> dict:
    """From orbit around a moon: burn back to the parent planet and re-enter with parachutes."""
    v = k.vessel()
    k.node_clear()
    plan = k.plan_return(periapsis=periapsis)
    log(f"return burn: {plan['node']['dv']:.1f} m/s, parent periapsis error {plan['periapsis_error_m']} m")
    if plan["node"]["dv"] > v["delta_v"]["total_vac"]:
        raise MissionError(f"not enough delta-v: need {plan['node']['dv']:.0f}, have {v['delta_v']['total_vac']:.0f}")
    execute_node(k)
    warp_to_soi(k)
    return reenter(k, periapsis=periapsis)


def _time_to_altitude(k: KSP, alt: float) -> float:
    """Seconds until the vessel descends through `alt` (coarse: steps along the orbit via eval)."""
    v = k.vessel()
    o = v["orbit"]
    if o["periapsis"] > alt:
        return float("inf")
    # step forward in time using the plugin's orbit prediction
    ut0 = k.ut()
    lo, hi = 0.0, o["time_to_periapsis"]
    r_body = _body(k, v["body"])["radius"]
    for _ in range(40):
        mid = (lo + hi) / 2
        pos = k.eval(expr=f"FlightGlobals.ActiveVessel.orbit.getRelativePositionAtUT({ut0 + mid})")
        a = math.sqrt(sum(c * c for c in pos)) - r_body
        if a > alt:
            lo = mid
        else:
            hi = mid
    return lo


ROUTINES = {
    "ascend": ascend,
    "execute_node": execute_node,
    "circularize": circularize_orbit,
    "change_apsis": change_apsis,
    "warp_to_soi": warp_to_soi,
    "transfer": transfer,
    "land": land,
    "reenter": reenter,
    "return_home": return_home,
}
