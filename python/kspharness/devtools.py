"""Development shortcuts that use KSP's debug cheats. NOT part of any legitimate mission: they exist only to
test designs quickly (e.g. an Eve ascent without flying there first).

    ksp run dev_place craft="Eve Ascent Test" body=Eve lat=0.25 lon=171 save=ascent_test
"""

from __future__ import annotations

import time

from .client import KSP
from .flight import MissionError, log


def dev_place(k: KSP, craft: str, body: str = "Eve", lat: float = 0.25, lon: float = 171.0, alt: float = 100.0,
              save: str | None = None, ease: float = 0.2) -> dict:
    """Launch `craft` at the KSC and teleport it (debug cheat) to stand upright on `body` at lat/lon."""
    if k.scene() == "FLIGHT":
        k.call("scene", name="spacecenter", force=True)
        k.wait_scene("SPACECENTER")
    for d in k.dialogs():
        k.call("dialog_click", button=d["buttons"][0] if d["buttons"] else None)
    k.call("launch", craft=craft)
    k.wait_scene("FLIGHT")
    time.sleep(4)
    # clear wrecks of earlier tests (debris and abandoned test vessels) so we don't land on them
    for _ in range(3):
        names = [str(x) for x in k.eval(expr="FlightGlobals.Vessels")]
        doomed = [i for i, n in enumerate(names) if " Debris " in n]
        if not doomed:
            break
        for i in sorted(doomed, reverse=True):
            try:
                k.eval(expr=f"FlightGlobals.Vessels[{i}].Die()")
            except Exception as e:
                log(f"  could not remove vessel {i}: {e}")
    idx = next(i for i, b in enumerate(k.eval(expr="FlightGlobals.Bodies")) if str(b).startswith(body + " "))
    for attempt in range(4):
        seq = k.call("ping")["event_seq"]

        def broken() -> bool:
            return any(e["type"] in ("vessel.crash", "part.destroyed") and "Debris" not in str(e.get("vessel"))
                       for e in k.events_since(seq))
        k.eval(expr=f"FlightGlobals.fetch.SetVesselPosition({idx},{float(lat)},{float(lon)},{float(alt)},90.0,90.0,false,true,{ease})")
        try:
            k.wait_for(lambda: broken() or (k.vessel().get("situation") == "LANDED" and k.vessel()["surface_speed"] < 0.3),
                       timeout=120, interval=2, what="landed after teleport")
        except TimeoutError:
            pass
        ok = not broken() and k.vessel().get("situation") == "LANDED"
        if ok:
            break
        log(f"  placement attempt {attempt + 1} failed; reverting and retrying")
        k.call("revert", to="launch")
        time.sleep(6)
        k.wait_scene("FLIGHT")
        time.sleep(4)
    else:
        raise MissionError("could not place the vessel")
    time.sleep(3)
    v = k.vessel()
    log(f"placed {craft} on {v['body']} at {v['latitude']:.3f}, {v['longitude']:.3f}, {v['altitude']:.0f} m, {v['static_pressure_kpa']:.0f} kPa")
    if save:
        k.call("save", file=save, force=True)
    return {"situation": v["situation"], "altitude": v["altitude"]}


ROUTINES = {"dev_place": dev_place}


def dev_orbit(k: KSP, craft: str, body: str = "Eve", altitude: float = 100000, inc: float = 0.0,
              save: str | None = None, periapsis: float | None = None) -> dict:
    """Launch `craft` at the KSC and put it (debug cheat) in a circular orbit around `body`."""
    if k.scene() == "FLIGHT":
        k.call("scene", name="spacecenter", force=True)
        k.wait_scene("SPACECENTER")
    for d in k.dialogs():
        k.call("dialog_click", button=d["buttons"][0] if d["buttons"] else None)
    k.call("launch", craft=craft)
    k.wait_scene("FLIGHT")
    time.sleep(4)
    idx = next(i for i, b in enumerate(k.eval(expr="FlightGlobals.Bodies")) if str(b).startswith(body + " "))
    r = k.bodies(name=body)[0]["radius"]
    ra, rp = r + altitude, r + (periapsis if periapsis is not None else altitude)
    ecc, sma = (ra - rp) / (ra + rp), (ra + rp) / 2
    # start at apoapsis (mean anomaly pi)
    k.eval(expr=f"FlightGlobals.fetch.SetShipOrbit({idx},{ecc},{sma},{float(inc)},0.0,{3.14159 if ecc > 0 else 0.0},0.0,0.0)")
    k.wait_for(lambda: k.vessel().get("body") == body and k.vessel()["situation"] in ("ORBITING", "SUB_ORBITAL"),
               timeout=60, interval=2, what="orbit after teleport")
    time.sleep(3)
    o = k.vessel()["orbit"]
    log(f"placed {craft} in orbit of {body}: {o['apoapsis'] / 1000:.1f} x {o['periapsis'] / 1000:.1f} km")
    if save:
        k.call("save", file=save, force=True)
    return o


ROUTINES["dev_orbit"] = dev_orbit
