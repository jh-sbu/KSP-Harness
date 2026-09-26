"""Craft designs built with kspharness.craft. ``ksp build <design> [save=<folder>]`` writes the .craft file."""

from __future__ import annotations

from .craft import Craft


def eve_express(k) -> Craft:
    """Crewed Eve orbit-and-return ship.

    Stages (fire order, top of list first):
      4  Mainsail          lower stage, Jumbo-64 + X200-32
      3  TD-25 + Poodle    upper stage to LKO, X200-32 + C7 adapter
      2  TD-12 + LV-N      transfer stage, 3 Mk1 LF fuselages (~6 km/s), Kerbin->Eve->Kerbin
      1  TD-12             drop the transfer stage before Kerbin entry
      0  Mk16 chute
    """
    c = Craft("Eve Express", k, description="Crewed Eve orbit and return. LV-N transfer stage, Mk1 pod with 1.25 m heat shield.")
    pod = c.root("mk1pod.v2")
    pod.stack("parachuteSingle", my_node="top", its_node="bottom", stage=0)
    shield = pod.stack("HeatShield1")
    sep = shield.stack("Decoupler.1", stage=1)
    wheel = sep.stack("advSasModule")
    t1 = wheel.stack("MK1Fuselage")
    t2 = t1.stack("MK1Fuselage")
    t3 = t2.stack("MK1Fuselage")
    t1.radial("solarPanels5", count=4, y=0.3, angle0=45)
    t1.radial("ksp.r.largeBatteryPack", count=2, y=-0.4, angle0=0)
    nerv = t3.stack("nuclearEngine", stage=2)
    d2 = nerv.stack("Decoupler.1", stage=2)
    adapter = d2.stack("adapterSize2-Size1")
    upper = adapter.stack("Rockomax32.BW")
    poodle = upper.stack("liquidEngine2-2.v2", stage=3)
    d3 = poodle.stack("Decoupler.2", stage=3)
    lower = d3.stack("Rockomax64.BW")
    lower2 = lower.stack("Rockomax32.BW")
    lower2.stack("liquidEngineMainsail.v2", stage=4)
    lower2.radial("tailfin", count=4, y=-1.2, angle0=0)
    return c


DESIGNS = {"eve_express": eve_express}
