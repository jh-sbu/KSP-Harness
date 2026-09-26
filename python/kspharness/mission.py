"""Eve round trip: launch from KSC, capture into Eve orbit, return to Kerbin and recover the crew.

    ksp run eve_mission [phase=<first phase>] [craft="Eve Express"] [eve_orbit=650000] [kerbin_pe=30000]

Phases run in order; each one quicksaves to `eve_<phase>` first, so a failed phase can be retried
with `ksp load_game save=<save> file=eve_<phase> flight=true` and `phase=<phase>`.
"""

from __future__ import annotations

import math
import time

import numpy as np

from . import flight
from . import interplanetary as ip
from .client import KSP
from .flight import MissionError, log, milestone_shot

PHASES = ["wait_window", "launch", "eject", "cruise_out", "capture", "wait_return", "eject_home", "cruise_home",
          "reentry", "recover"]

KERBIN_PARK = 100000


def _kerbin_window(k: KSP, park_alt: float, eve_orbit: float) -> ip.Transfer:
    """Next Kerbin->Eve window, computed without a vessel (from the space center)."""
    now = k.ut()
    ke, ev = ip.body_elements(k, "Kerbin", now), ip.body_elements(k, "Eve", now)
    kb, eb = k.bodies(name="Kerbin")[0], k.bodies(name="Eve")[0]
    syn = 1 / abs(1 / ke.period - 1 / ev.period)
    # we launch due east into an equatorial parking orbit: its normal is the pole (orbit-frame z)
    return ip.porkchop(ke, ev, now + 3600, now + 1.05 * syn, r_park=kb["radius"] + park_alt, mu_origin=kb["mu"],
                       r_capture=eb["radius"] + eve_orbit, mu_target=eb["mu"], park_normal=np.array([0.0, 0.0, 1.0]))


def _save(k: KSP, name: str) -> None:
    try:
        k.call("save", file=name, force=True)
        log(f"saved {name}")
    except Exception as e:  # saving is a convenience; never abort the mission for it
        log(f"  save {name} failed: {e}")


def _check_crew(k: KSP, crew: list[str]) -> None:
    now = k.vessel()["crew"]
    if sorted(now) != sorted(crew):
        raise MissionError(f"crew changed: {crew} -> {now}")


def eve_mission(k: KSP, phase: str = "wait_window", craft: str = "Eve Express", eve_orbit: float = 650000,
                kerbin_pe: float = 30000, launch_lead_days: float = 0.25, stop_after: str | None = None) -> dict:
    """Fly the Eve round trip, starting at `phase` (see PHASES)."""
    if phase not in PHASES:
        raise MissionError(f"unknown phase {phase}; phases: {PHASES}")
    t_start = time.time()
    record: dict = {}
    crew: list[str] = []
    for ph in PHASES[PHASES.index(phase):]:
        log(f"===== phase {ph} =====")
        if k.scene() == "FLIGHT" and ph not in ("recover",):
            _save(k, f"eve_{ph}")
        if ph == "wait_window":
            if k.scene() != "SPACECENTER":
                raise MissionError("wait_window runs at the space center")
            tr = _kerbin_window(k, KERBIN_PARK, eve_orbit)
            log(f"Kerbin->Eve window: depart UT {tr.depart_ut:.0f} (in {(tr.depart_ut - k.ut()) / 21600:.1f} d), "
                f"{tr.dv_depart:.0f} + {tr.dv_arrive:.0f} m/s, flight {(tr.arrive_ut - tr.depart_ut) / 21600:.0f} d")
            target_ut = tr.depart_ut - launch_lead_days * 21600
            if target_ut > k.ut() + 60:
                flight._warp_until(k, target_ut, "launch date")
        elif ph == "launch":
            if k.scene() != "FLIGHT":
                k.call("launch", craft=craft)
                k.wait_scene("FLIGHT")
                time.sleep(5)
            crew = k.vessel()["crew"]
            record["crew"] = crew
            log(f"crew: {crew}")
            flight.ascend(k, altitude=KERBIN_PARK)
            # separate the launcher and light the transfer stage
            v = k.vessel()
            while v["stage"] > 2:
                k.stage()
                time.sleep(2)
                v = k.vessel()
            log(f"in orbit on the transfer stage: {v['delta_v']['current_stage_vac']:.0f} m/s available")
        elif ph == "eject":
            # we launched for a specific window: search only around it
            res = flight.plan_interplanetary(k, target="Eve", capture_alt=eve_orbit, search_days=30)
            record["outbound_plan"] = res["transfer"]
            flight.execute_node(k, tolerance=0.05)
            appr = k.node_approach(target="Eve")
            log(f"after ejection burn: {flight._fmt_approach(appr)}")
        elif ph == "cruise_out":
            if k.vessel()["body"] == "Kerbin":
                flight.warp_to_soi(k)  # leave Kerbin
            a = flight.course_correct(k, target="Eve", periapsis=eve_orbit, max_inc=30, inc_weight=1.0)
            record["outbound_correction"] = a
            # a second, small trim halfway there
            t_half = (k.ut() + a["soi_entry_ut"]) / 2
            flight._warp_until(k, t_half, "mid-cruise trim")
            flight.course_correct(k, target="Eve", periapsis=eve_orbit, max_inc=30, inc_weight=1.0)
            flight.warp_to_soi(k)  # enter Eve's SOI
            milestone_shot(k, "eve_soi")
        elif ph == "capture":
            o = flight.capture(k, altitude=eve_orbit)
            if o["body"] != "Eve" or o["eccentricity"] >= 1:
                raise MissionError(f"capture failed: {o}")
            record["eve_orbit"] = o
            milestone_shot(k, "eve_orbit")
            log(f"IN EVE ORBIT: ap {o['apoapsis'] / 1000:.0f} km, pe {o['periapsis'] / 1000:.0f} km, inc {o['inclination']:.1f}")
        elif ph == "wait_return":
            pass  # plan_interplanetary (next phase) warps to the window itself
        elif ph == "eject_home":
            res = flight.plan_interplanetary(k, target="Kerbin", capture_alt=kerbin_pe, capture_weight=0.5)
            record["return_plan"] = res["transfer"]
            flight.execute_node(k, tolerance=0.05)
            log(f"after ejection burn: {flight._fmt_approach(k.node_approach(target='Kerbin'))}")
        elif ph == "cruise_home":
            if k.vessel()["body"] == "Eve":
                flight.warp_to_soi(k)  # leave Eve
            a = flight.course_correct(k, target="Kerbin", periapsis=kerbin_pe, max_inc=60, inc_weight=1.0)
            t_half = (k.ut() + a["soi_entry_ut"]) / 2
            flight._warp_until(k, t_half, "mid-cruise trim")
            flight.course_correct(k, target="Kerbin", periapsis=kerbin_pe, max_inc=60, inc_weight=1.0)
            flight.warp_to_soi(k)  # enter Kerbin's SOI
            # final periapsis trim well outside the atmosphere
            o = k.vessel()["orbit"]
            if abs(o["periapsis"] - kerbin_pe) > 3000:
                flight.course_correct(k, target="Kerbin", periapsis=kerbin_pe, lead=120)
            milestone_shot(k, "kerbin_approach")
        elif ph == "reentry":
            r = flight.reenter(k, periapsis=kerbin_pe + 5000)
            record["touchdown"] = r
        elif ph == "recover":
            v = k.vessel()
            record["final_crew"] = v["crew"]
            log(f"recovering {v['name']} ({v['situation']}) with crew {v['crew']}")
            k.call("recover")
            try:  # screenshot the Mission Summary, not the loading screen
                k.wait_for(lambda: any(d["name"] == "MissionRecoveryDialog" for d in k.dialogs()), timeout=60, interval=1,
                           what="the mission summary")
                time.sleep(1)
            except Exception:
                pass
            milestone_shot(k, "recovered")
        if stop_after == ph:
            log(f"stopping after {ph} as requested")
            break
    log(f"mission sequence finished in {(time.time() - t_start) / 60:.1f} min real time")
    return record
