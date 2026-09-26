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

import numpy as np

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
           autostage_stop: int | None = None, launch: bool = True, min_tta: float = 30) -> dict:
    """Launch (if on the pad) and fly a gravity turn to a circular orbit at `altitude` metres."""
    v = k.vessel()
    body = _body(k, v["body"])
    atmo = body["atmosphere_depth"]
    if turn_end is None:
        turn_end = atmo * 0.75 if atmo else max(altitude * 0.5, 8000)
    stop = autostage_stop if autostage_stop is not None else _last_engine_stage(k)
    watch = EventWatch(k)
    k.ap(mode="pitch_heading", pitch=90, heading=heading)
    if v["situation"] in ("PRELAUNCH", "LANDED", "SPLASHED"):
        k.autostage(on=False)  # plugin state can survive a revert; don't let it race the launch
    # Only stage to "launch" if nothing is ignited yet (on the pad). A landed lander with an active
    # engine must not stage: that would fire its decoupler. Stage before enabling autostage, or
    # autostage (seeing full throttle and no running engine) races us to it.
    if launch and v["situation"] in ("PRELAUNCH", "LANDED", "SPLASHED") and v["max_thrust"] == 0:
        log(f"launch: target {altitude/1000:.0f} km, heading {heading}, turn end {turn_end/1000:.0f} km, autostage down to {stop}")
        k.throttle(value=1)
        k.stage()
        time.sleep(1.0)
    k.autostage(on=True, stop_at=stop)
    k.throttle(value=1)
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
        # Weak upper stages: above the dense air, pitch up as needed so apoapsis stays ahead of us
        # (time to apoapsis >= min_tta) instead of sagging back into the atmosphere.
        if alt > 20000 and v["dynamic_pressure_kpa"] < 2 and ap < altitude:
            tta = v["orbit"]["time_to_apoapsis"] if vs >= 0 and v["orbit"]["time_to_apoapsis"] is not None else 0.0
            if tta < min_tta:
                pitch = max(pitch, min(45.0, (min_tta - tta) * 1.5))
        k.ap(mode="pitch_heading", pitch=round(pitch, 2), heading=heading)

        # Apoapsis reached but still inside the atmosphere and falling back (a weak upper stage that
        # sagged): cutting the engine now means re-entering, so keep burning (pitched up by the
        # min_tta rule) until we're climbing again or out of the air.
        sagging = alt < atmo and vs < 0
        if ap >= altitude and not sagging:
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


def reenter(k: KSP, periapsis: float = 30000, stage_to_capsule: bool = True, separate_above: float = 100000) -> dict:
    """Return to the ground through an atmosphere with parachutes: lower periapsis, drop stages, arm chutes.

    The capsule is separated only `separate_above` metres above the atmosphere: the decoupler's push is a few
    m/s, which over an hour-long approach moves periapsis by tens of km (and the capsule can't correct it)."""
    v = k.vessel()
    body = _body(k, v["body"])
    if not body["atmosphere"]:
        raise MissionError("no atmosphere here; use land")
    atmo = body["atmosphere_depth"]
    watch = EventWatch(k)
    k.autostage(on=False)
    if v["orbit"]["periapsis"] > periapsis and v["orbit"]["eccentricity"] < 1:
        change_apsis(k, "periapsis", periapsis, at="now" if v["orbit"]["eccentricity"] < 0.01 else "apoapsis")
    v = k.vessel()
    if v["orbit"]["periapsis"] > atmo:
        raise MissionError(f"periapsis {v['orbit']['periapsis'] / 1000:.0f} km is above the atmosphere")
    k.ap(mode="retrograde")
    sep_alt = atmo + separate_above
    if v["altitude"] > sep_alt + 5000:
        _warp_until(k, k.ut() + _time_to_altitude(k, sep_alt) - 5, f"{sep_alt / 1000:.0f} km (capsule separation)")
    watch.poll()
    if stage_to_capsule:
        # drop everything below the parachute stage
        while True:
            v = k.vessel()
            if v["stage"] <= 1:
                break
            k.stage()
            time.sleep(1.5)
            watch.poll()
    v = k.vessel()
    log(f"capsule free at {v['altitude'] / 1000:.0f} km, periapsis {v['orbit']['periapsis'] / 1000:.1f} km; coasting to atmosphere")
    if v["altitude"] > atmo + 2000:
        _warp_until(k, k.ut() + max(0, _time_to_altitude(k, atmo + 1000)), "atmosphere interface")
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


# ---------------------------------------------------------------------------- interplanetary


def _warp_until(k: KSP, ut: float, what: str) -> None:
    """Time-warp to `ut` and block until the game gets there (warp back at 1x)."""
    if ut - k.ut() < 5:
        return
    log(f"warping {ut - k.ut():.0f}s ({(ut - k.ut()) / 21600:.1f} days) until {what}")
    k.warp(to_ut=ut)
    deadline = time.time() + 3600
    idle_since = None
    while True:
        now, st = k.ut(), k.status()
        if now >= ut - 2 and st["warp"]["index"] == 0:
            return
        if st["warp"]["index"] == 0 and now < ut - 2:
            # the game dropped out of warp early (or never started): ask again rather than crawl at 1x
            idle_since = idle_since or time.time()
            if time.time() - idle_since > 5:
                log(f"  warp stopped {ut - now:.0f}s early; re-issuing")
                k.warp(to_ut=ut)
                idle_since = None
        else:
            idle_since = None
        if time.time() > deadline:
            raise MissionError(f"timed out warping until {what}")
        time.sleep(1)


def _porkchop_from_here(k: KSP, target: str, capture_alt: float, capture_weight: float,
                        t_from: float, t_to: float, **kw):
    from . import interplanetary as ip
    park = ip.vessel_elements(k)
    origin = park.parent
    now = k.ut()
    o_el, t_el = ip.body_elements(k, origin, now), ip.body_elements(k, target, now)
    tb = _body(k, target)
    r, v = park.state(now)
    h = np.cross(r, v)
    tr = ip.porkchop(o_el, t_el, t_from, t_to, r_park=park.a, mu_origin=park.mu,
                     r_capture=tb["radius"] + capture_alt, mu_target=tb["mu"], capture_weight=capture_weight,
                     park_normal=h / np.linalg.norm(h), **kw)
    return park, tr


def plan_interplanetary(k: KSP, target: str = "Eve", capture_alt: float = 100000, capture_weight: float = 1.0,
                        search_from: float | None = None, search_days: float | None = None, wait: bool = True,
                        max_inc: float = 30.0) -> dict:
    """Find the next transfer window to `target` (porkchop over departure date and flight time), warp
    to it if it's far away, and place the ejection node from the current (near-circular) parking orbit.

    capture_weight=0 ignores the arrival burn (use for aerobraking / direct entry arrivals)."""
    from . import interplanetary as ip
    k.node_clear()
    v = k.vessel()
    origin = v["body"]
    ob, tb = _body(k, origin), _body(k, target)
    if ob["parent"] != tb["parent"]:
        raise MissionError(f"{origin} and {target} don't orbit the same body")
    T1, T2 = ob["orbit"]["period"], tb["orbit"]["period"]
    synodic = 1 / abs(1 / T1 - 1 / T2)
    now = k.ut()
    t_from = search_from if search_from is not None else now + 1800
    t_to = t_from + (search_days * 21600 if search_days else synodic * 1.05)
    park, tr = _porkchop_from_here(k, target, capture_alt, capture_weight, t_from, t_to)
    log(f"window: depart in {(tr.depart_ut - now) / 21600:.1f} d, flight {(tr.arrive_ut - tr.depart_ut) / 21600:.1f} d, "
        f"eject {tr.dv_depart:.0f} m/s (vinf {np.linalg.norm(tr.vinf_dep):.0f}), arrive vinf {np.linalg.norm(tr.vinf_arr):.0f} "
        f"(capture {tr.dv_arrive:.0f} m/s)")
    if wait and tr.depart_ut - k.ut() > 3 * park.period:
        _warp_until(k, tr.depart_ut - 2 * park.period, "the transfer window")
        # re-plan precisely from the actual parking orbit, in a narrow window
        park, tr = _porkchop_from_here(k, target, capture_alt, capture_weight, k.ut() + 600,
                                       tr.depart_ut + 3 * park.period, n_dep=60, n_tof=40,
                                       tof_range=(0.8 * (tr.arrive_ut - tr.depart_ut), 1.2 * (tr.arrive_ut - tr.depart_ut)))
        log(f"re-planned: depart in {(tr.depart_ut - k.ut()):.0f}s, eject {tr.dv_depart:.0f} m/s")
    now = k.ut()
    vinf = np.array(tr.vinf_dep, float)
    t_el = ip.body_elements(k, target, now)
    tb = _body(k, target)
    # Differential correction. Place the ejection for the target v_inf, read the heliocentric state at SOI
    # exit from the game, B-plane-target from there (arrival near the planned date only), and fold the
    # required velocity change back into v_inf. Damped, burn time kept near the first solution, and the
    # best iteration is the one flown.
    t0, span = max(now + 300, tr.depart_ut - park.period / 2), park.period
    gain, best, prev_err = 1.0, None, None
    # Only the part of the correction in the heliocentric transfer plane is applied here (timing and energy).
    # Near-180-degree transfers make out-of-plane errors very expensive to fix at departure; the mid-course
    # correction fixes the plane at the cheapest point of the cruise instead (a broken-plane transfer).
    for it in range(8):
        k.node_clear()
        ej = ip.plan_ejection(park, vinf, t0=t0, span=span)
        k.node_add(ut=ej.ut, prograde=ej.prograde, normal=ej.normal, radial=ej.radial)
        appr = k.node_approach(target=target)
        sun = "FlightGlobals.ActiveVessel.patchedConicSolver.maneuverNodes[0].nextPatch.nextPatch"
        if k.eval(expr=sun + ".referenceBody.bodyName") != t_el.parent:
            raise MissionError("ejection node does not escape into the target's parent orbit")
        t_s = float(k.eval(expr=sun + ".StartUT")) + 1.0
        r_s, v_s = ip.game_state(k, sun, t_s)
        err, info = ip.plan_inplane_intercept(r_s, v_s, t_s, t_el.mu, t_el, tr.arrive_ut)
        e = float(np.linalg.norm(err)) if info["miss_m"] < 1e6 else float("inf")
        log(f"ejection node (iteration {it}) in {ej.ut - now:.0f}s: {ej.dv:.1f} m/s (pro {ej.prograde:.1f}, "
            f"nrm {ej.normal:.1f}, rad {ej.radial:.1f}); {_fmt_approach(appr)}; in-plane correction still needed {e:.1f} m/s")
        if best is None or e < best[0]:
            best = (e, ej, vinf.copy(), err)
        elif np.isfinite(e) or prev_err is not None:
            gain *= 0.5  # this step made things worse: retry from the best point with a shorter step
        if e < 0.5 or (appr.get("encounter") and e < 3) or not np.isfinite(best[0]) or gain < 0.1:
            break
        prev_err = e
        vinf = best[2] + gain * best[3]
        # after the first solution, keep the burn on the same pass
        t0, span = best[1].ut - park.period / 4, park.period / 2
    e, ej, vinf, _ = best
    k.node_clear()
    k.node_add(ut=ej.ut, prograde=ej.prograde, normal=ej.normal, radial=ej.radial)
    if ej.dv > 1.3 * tr.dv_depart + 100:
        raise MissionError(f"ejection needs {ej.dv:.0f} m/s, far above the planned {tr.dv_depart:.0f}; not flying it")
    appr = k.node_approach(target=target)
    log(f"ejection planned: {k.nodes()[0]['dv']:.1f} m/s -> {_fmt_approach(appr)} (plane is fixed mid-course)")
    return {"transfer": {"depart_ut": tr.depart_ut, "arrive_ut": tr.arrive_ut, "dv_depart": tr.dv_depart,
                         "dv_arrive": tr.dv_arrive}, "node": k.nodes()[0], "approach": appr}


def _fmt_approach(a: dict) -> str:
    if a.get("encounter"):
        return f"ENCOUNTER pe {a['periapsis'] / 1000:.0f} km, inc {a['inclination']:.1f}"
    if "closest_approach" in a:
        return f"closest {a['closest_approach'] / 1e6:.2f} Mm"
    return "no approach data"


def _approach_cost(a: dict, soi: float, periapsis: float, inc_weight: float, max_inc: float) -> float:
    if a.get("encounter"):
        c = ((a["periapsis"] - periapsis) / 20000.0) ** 2
        inc = a["inclination"]
        if inc > max_inc:
            c += inc_weight * ((inc - max_inc) / 5.0) ** 2
        return c
    if "closest_approach" not in a:
        return 1e9
    return 1000 + a["closest_approach"] / soi * 100


def refine_node(k: KSP, target: str, periapsis: float, index: int = -1, vary_ut: bool = False,
                inc_weight: float = 0.0, max_inc: float = 180.0, iters: int = 120) -> dict:
    """Adjust the last node (prograde/normal/radial, optionally time) with the game's patched conics so the
    trajectory meets `target` with the given periapsis (and inclination at most max_inc if inc_weight > 0)."""
    from . import interplanetary as ip
    nodes = k.nodes()
    i = len(nodes) - 1 if index < 0 else index
    n0 = nodes[i]
    soi = _body(k, target)["soi"]
    base = np.array([n0["ut"], n0["prograde"], n0["normal"], n0["radial"]])
    evals = [0]

    def f(x):
        evals[0] += 1
        ut = base[0] + (x[3] if vary_ut else 0.0)
        k.node_update(index=i, ut=ut, prograde=base[1] + x[0], normal=base[2] + x[1], radial=base[3] + x[2])
        return _approach_cost(k.node_approach(target=target, index=i), soi, periapsis, inc_weight, max_inc)

    dim = 4 if vary_ut else 3
    step = [2.0, 2.0, 2.0] + ([30.0] if vary_ut else [])
    best_x, best = np.zeros(dim), f(np.zeros(dim))
    for rnd in range(4):
        x, c = ip.nelder_mead(f, best_x, step, iters=iters)
        if c < best:
            best_x, best = x, c
        if best < 0.05:
            break
        step = [s * 0.3 for s in step]
    f(best_x)
    a = k.node_approach(target=target, index=i)
    n = k.nodes()[i]
    log(f"refined node ({evals[0]} evals): dv {n['dv']:.1f} m/s -> {_fmt_approach(a)}")
    return a


def course_correct(k: KSP, target: str = "Eve", periapsis: float = 100000, lead: float = 600,
                   max_inc: float = 180.0, inc_weight: float = 0.0, execute: bool = True,
                   scan_days: tuple | None = None, prograde: bool = True) -> dict:
    """Mid-course correction toward `target`.

    Outside the target's SOI: B-plane targeting (minimum-norm dv so the trajectory passes the target at the
    miss distance for `periapsis`, prograde and as equatorial as possible), tried at a few burn dates; the
    cheapest is flown. Then a short refinement against the game's patched conics (periapsis and inclination).
    Inside the target's SOI: only the game refinement."""
    from . import interplanetary as ip
    k.node_clear()
    appr = k.node_approach(target=target)
    log(f"course correction toward {target}: currently {_fmt_approach(appr)}")
    v = k.vessel()
    if v["body"] != target and not (appr.get("encounter") and abs(appr["periapsis"] - periapsis) < 0.02 * periapsis
                                    and appr["inclination"] <= max_inc):
        tb = _body(k, target)
        now = k.ut()
        t_arr = appr.get("soi_entry_ut") or appr.get("closest_approach_ut") or now + 100 * 21600
        el = ip.vessel_elements(k, now)
        te = ip.body_elements(k, target, now)
        best = None
        if scan_days is None:  # the whole cruise, so a plane change can happen where it is cheapest
            span = (t_arr - now) / 21600
            scan_days = [0.0, 1, 3] + [d for d in np.arange(6, span - 3, max(4.0, span / 24))]
        for d in scan_days:
            ut = now + max(lead, d * 21600)
            if ut > t_arr - 3 * 21600:
                continue
            r1, v1 = el.state(ut)
            dv, info = ip.plan_bplane(r1, v1, ut, el.mu, te, (max(ut + 3600, t_arr - 50 * 21600), t_arr + 50 * 21600),
                                      tb["radius"] + periapsis, tb["mu"], prograde=prograde)
            ok = info["miss_error_m"] < 20000
            if ok and (best is None or np.linalg.norm(dv) < np.linalg.norm(best[1]) - 5):
                best = (ut, dv)
        if best is None:
            raise MissionError("B-plane targeting did not converge at any burn date")
        log(f"  scanned {len(scan_days)} burn dates: cheapest {np.linalg.norm(best[1]):.1f} m/s in {(best[0] - now) / 21600:.1f} d")
        ut = best[0]
        if ut - k.ut() > lead + 1800:
            _warp_until(k, ut - lead, "course correction burn")
            el = ip.vessel_elements(k)
            te = ip.body_elements(k, target)
            ut = max(ut, k.ut() + lead)
        r1, v1 = el.state(ut)
        dv, info = ip.plan_bplane(r1, v1, ut, el.mu, te, (max(ut + 3600, t_arr - 50 * 21600), t_arr + 50 * 21600),
                                  tb["radius"] + periapsis, tb["mu"], prograde=prograde)
        rad, nrm, pro = ip.node_coords(r1, v1, dv)
        k.node_add(ut=ut, prograde=pro, normal=nrm, radial=rad)
        log(f"  B-plane correction {np.linalg.norm(dv):.1f} m/s in {(ut - k.ut()):.0f}s -> {_fmt_approach(k.node_approach(target=target))}")
    else:
        k.node_add(ut=k.ut() + lead, prograde=0, normal=0, radial=0)
    a = refine_node(k, target, periapsis, inc_weight=inc_weight, max_inc=max_inc)
    n = k.nodes()[0]
    if not a.get("encounter"):
        raise MissionError(f"course correction found no encounter: {a}")
    if execute and n["dv"] > 0.3:
        execute_node(k, tolerance=0.05)
        a = k.node_approach(target=target)
        log(f"after correction: {_fmt_approach(a)}")
    else:
        k.node_clear()
    return a


def capture(k: KSP, altitude: float | None = None, apoapsis: float | None = None) -> dict:
    """Arriving on a hyperbolic trajectory: burn at periapsis into a closed orbit (circular at periapsis
    altitude by default, or with the given apoapsis)."""
    v = k.vessel()
    o = v["orbit"]
    body = _body(k, v["body"])
    if altitude is not None and abs(o["periapsis"] - altitude) > max(5000, 0.05 * altitude):
        log(f"correcting arrival periapsis {o['periapsis'] / 1000:.0f} -> {altitude / 1000:.0f} km")
        course_correct(k, target=v["body"], periapsis=altitude, lead=120)
    k.node_clear()
    if apoapsis:
        n = k.plan_apsis(which="apoapsis", altitude=apoapsis, at="periapsis")
    else:
        n = k.plan_circularize(at="periapsis")
    log(f"capture burn at {body['name']} periapsis: {n['dv']:.0f} m/s")
    return execute_node(k)


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
    "plan_interplanetary": plan_interplanetary,
    "course_correct": course_correct,
    "capture": capture,
}
