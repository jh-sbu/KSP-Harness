"""Craft designs built with kspharness.craft. ``ksp build <design> [save=<folder>]`` writes the .craft file."""

from __future__ import annotations

from .craft import Craft, Part


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


NO_STAGING_CHUTE = ["MODULE", "{", "\tname = ModuleParachute", "\tisEnabled = True", "\tstagingEnabled = False", "}"]
# Eve mains open fully at 3 km above the ground (default 1 km): more time to slow a heavy lander down
EVE_MAIN_CHUTE = ["MODULE", "{", "\tname = ModuleParachute", "\tisEnabled = True", "\tstagingEnabled = False",
                  "\tdeployAltitude = 3000", "}"]
# the uprighting chutes are a different part (Mk16-XL can't go radial), so they're told apart by their parent
UPRIGHT_CHUTE = EVE_MAIN_CHUTE
CROSSFEED_ON = ["MODULE", "{", "\tname = ModuleToggleCrossfeed", "\tisEnabled = True", "\tcrossfeedStatus = True", "}"]


def _eve_return_stage(c: Craft, hiad_top: bool = False, tank: str = "fuelTank.long"):
    """Mk1 pod with Kerbin heat shield and chute, on the Terrier stage that flies Eve orbit -> Kerbin.
    Returns the part below the Terrier stage's decoupler (stage 3: drop the ascent core, light the Terrier).

    hiad_top: a 10 m inflatable heat shield on the pod's top node. The lander enters Eve engines-first; the
    HIAD trails, sticking out past the cluster's shadow, and its drag behind the centre of mass keeps the tall
    vehicle pointed (a shuttlecock). The Kerbin chutes then go on the pod's sides."""
    pod = c.root("mk1pod.v2")
    if hiad_top:
        for ch in pod.radial("parachuteRadial", count=2, y=0.05, angle0=90):
            ch.extra += NO_STAGING_CHUTE
        # A decoupler and ~4 m of girder under the HIAD: inflated, its dish reaches 2.9 m below its attach node,
        # and at release anything inside it collides with it. The HIAD is mounted flipped by its "bottom" node
        # (its "top" node must stay free or it refuses to inflate).
        sep = pod.stack("Decoupler.0", my_node="top", its_node="bottom")
        g1 = sep.stack("trussPiece3x", my_node="top", its_node="bottom")
        g2 = g1.stack("trussPiece1x", my_node="top", its_node="bottom")
        g2.stack("InflatableHeatShield", my_node="top", its_node="bottom", flip=True)
    else:
        pod.stack("parachuteSingle", my_node="top", its_node="bottom", stage=0)
    shield = pod.stack("HeatShield1")
    sep = shield.stack("Decoupler.1", stage=1)
    wheel = sep.stack("advSasModule")
    batt = wheel.stack("batteryBank")
    probe = batt.stack("probeStackSmall")  # backup control (and control for uncrewed test articles)
    tank = probe.stack(tank)
    tank.radial("solarPanels5", count=4, y=0.3, angle0=45)
    terrier = tank.stack("liquidEngine3.v2", stage=3)
    return terrier.stack("Decoupler.1", stage=3)


def _booster_pair(core, angle0: float, dec_stage: int, eng_stage: int, tank: str = "Rockomax32.BW",
                  engine: str = "SSME", count: int = 2, fin: str | None = "tailfin"):
    """Asparagus boosters: TT-70 decouplers (crossfeed on) with a tank, engine and nose cone each."""
    decs = core.radial("radialDecoupler2", count=count, y=0.0, angle0=angle0, stage=dec_stage)
    for d in decs:
        d.extra += CROSSFEED_ON
        d.autostrut = "Heaviest"
    tanks = Part.sym_side(decs, tank, depth=0.72)
    for t in tanks:
        t.autostrut = "Heaviest"
    for e in Part.sym_stack(tanks, engine, stage=eng_stage):
        e.autostrut = "Heaviest"
    Part.sym_stack(tanks, "rocketNoseCone.v3", my_node="top", its_node="bottom")
    if fin:
        for i, t in enumerate(tanks):  # one fin on the outside of each booster, at the tail
            t.radial(fin, count=1, y=-1.4, angle0=angle0 + 360.0 * i / count)
    return tanks


def eve_ascent_test(k, pairs: int = 2, engine: str = "LiquidEngineKE-1", core_fin: str | None = "winglet3",
                    booster_fin: str | None = "winglet3") -> Craft:
    """Eve ascent vehicle only (no descent hardware), for ascent tests from the Eve surface.

    Stages: 6 all Vectors; 5 outer pair off; 4 inner pair off; 3 core off + Terrier; 1 drop Terrier stage; 0 chute."""
    c = Craft("Eve Ascent Test", k, description="Eve ascent vehicle test article: Vector asparagus + Terrier return stage.")
    below = _eve_return_stage(c)
    ad = below.stack("largeAdapter")
    core = ad.stack("Rockomax32.BW")
    core.stack(engine, stage=6)
    if core_fin:
        core.radial(core_fin, count=4, y=-1.2, angle0=45)
    if pairs >= 1:
        _booster_pair(core, 90, dec_stage=4, eng_stage=6, engine=engine, fin=booster_fin)
    if pairs >= 2:
        _booster_pair(core, 0, dec_stage=5, eng_stage=6, engine=engine, fin=booster_fin)
    return c


def eve_ascent_serial(k, upper_tank: str = "Rockomax64.BW", upper_engine: str = "LiquidEngineKE-1",
                      lower_tanks: tuple = ("Size3LargeTank",), fin: str | None = "winglet3") -> Craft:
    """Serial Eve ascent vehicle: Mammoth first stage (3.75 m), Mastodon second stage (2.5 m), Terrier stage.
    Every engine covers the whole tail of its tank (low drag); stages drop straight back in the wake.

    Stages: 5 Mammoth; 4 drop first stage + light the second; 3 drop second stage + light the Terrier."""
    c = Craft("Eve Ascent Serial", k, description="Serial Eve ascent vehicle test article.")
    below = _eve_return_stage(c)
    ad = below.stack("largeAdapter")
    up = ad.stack(upper_tank)
    eng2 = up.stack(upper_engine, stage=4)
    sep = eng2.stack("Decoupler.2", stage=4)
    ad3 = sep.stack("Size3To2Adapter.v2")
    t = ad3
    for name in lower_tanks:
        t = t.stack(name)
    t.stack("Size3EngineCluster", stage=5)
    if fin:
        t.radial(fin, count=4, y=-2.8, angle0=45)
    return c


def _cluster_stage(top, sides: int = 4, core_tank: str = "Rockomax32.BW", side_tank: str = "Rockomax32.BW",
                   engine: str = "LiquidEngineKE-1", stage: int = 5, fin: str | None = None, hiad: bool = False,
                   feet: str | None = None, wheels: int = 0, batteries: int = 0):
    """First stage: a core with `sides` identical stacks surface-attached to it (no decouplers, so no radial
    separation in the thick air). Each stack feeds its own engine; all run dry together and the whole cluster
    drops at once through the stack decoupler above the core. The side stacks make a wide landing base."""
    # reaction wheels to hold the vehicle against the HIAD's destabilizing drag during Eve entry; they sit on the
    # first stage, so they are dropped with it on the way back up
    for _ in range(batteries):  # power for the wheels through a long, actively held Eve entry and descent
        top = top.stack("batteryBankLarge")
        top.autostrut = "Heaviest"
    for _ in range(wheels):
        top = top.stack("asasmodule1-2")
        top.autostrut = "Heaviest"
    core = top.stack(core_tank)
    ce = core.stack(engine, stage=stage)
    if hiad:
        # 10 m inflatable heat shield under the core engine (its upward-facing node is called "bottom"). It is
        # inflated for Eve entry, shades the whole cluster, and doubles as the landing pad; it is decoupled
        # (omni-decoupler, not staged) at lift-off and stays on the ground.
        ce.stack("InflatableHeatShield", my_node="bottom", its_node="bottom",
                 my_node_y=-2.95 if engine == "LiquidEngineKE-1" else None)
    tanks = core.radial(side_tank, count=sides, y=0.0, angle0=0)
    for t in tanks:
        t.autostrut = "Heaviest"
    engines = Part.sym_stack(tanks, engine, stage=stage)
    for e in engines:
        e.autostrut = "Heaviest"
    if feet:
        # a heat shield under every engine: Eve entry protection, then landing feet; decoupled at lift-off
        # (the Mastodon's default "Full" variant moves its bottom node to -2.95 m; part_info reports -2.7)
        y = -2.95 if engine == "LiquidEngineKE-1" else None
        for e in [ce] + engines:
            e.stack(feet, my_node="bottom", its_node="top", my_node_y=y)
    cones = Part.sym_stack(tanks, "rocketNoseCone.v3", my_node="top", its_node="bottom")
    for cone, t in zip(cones, tanks):
        cone.outward = t.outward
    if fin:
        for i, t in enumerate(tanks):
            t.radial(fin, count=1, y=-1.4, angle0=360.0 * i / sides)
    return core, tanks, cones


def eve_ascent_cluster(k, sides: int = 4, upper_tank: str = "Rockomax32.BW", core_tank: str = "Rockomax32.BW",
                       side_tank: str = "Rockomax32.BW", fin: str | None = "winglet3") -> Craft:
    """Eve ascent vehicle: cluster first stage (Mastodons), Mastodon second stage, Terrier return stage.

    Stages: 5 cluster engines; 4 drop the cluster + light the second stage; 3 drop it + light the Terrier."""
    c = Craft("Eve Ascent Cluster", k, description="Eve ascent vehicle test article: Mastodon cluster, Mastodon upper stage.")
    below = _eve_return_stage(c)
    ad = below.stack("largeAdapter")
    up = ad.stack(upper_tank)
    eng2 = up.stack("LiquidEngineKE-1", stage=4)
    sep = eng2.stack("Decoupler.2", stage=4)
    _cluster_stage(sep, sides=sides, core_tank=core_tank, side_tank=side_tank, fin=fin)
    return c


def _stiffen(c: Craft, parts=None) -> None:
    """Rigid attachment and autostruts (to the heaviest part) everywhere: an Eve entry pulls ~5 g with the
    heat shields carrying the load into the engines and tanks behind them."""
    for p in parts if parts is not None else c.parts:
        p.rigid = True
        if p.autostrut == "Off" and p.srf_parent is None or p.name.startswith(("Rockomax", "LiquidEngine", "HeatShield")):
            p.autostrut = "Heaviest"


def eve_lander(k, sides: int = 4, mains_per_side: int = 3, drogues_per_side: int = 0,
               upper_tank: str = "Rockomax32.BW", upper_extra: str | None = "Rockomax16.BW", hiad: bool = False,
               feet: str | None = "HeatShield2", wheels: int = 0,
               hiad_top: bool = True, batteries: int = 0, radial_batteries: int = 2, mains_on: str = "sides",
               uprighting_chutes: int = 8) -> Craft:
    """Eve lander and ascent vehicle: the cluster ascent vehicle plus the Eve descent parachutes, which sit on
    the cluster's side tanks (above the centre of mass, and dropped with the first stage on the way up).
    The final metres are flown on the cluster's engines."""
    c = Craft("Eve Lander", k, description="Eve lander / ascent vehicle / Kerbin return capsule.")
    below = _eve_return_stage(c, hiad_top=hiad_top)
    ad = below.stack("largeAdapter")
    up = ad.stack(upper_tank)
    low = up.stack(upper_extra) if upper_extra else up
    # fins at the bottom of the upper stage: flying alone after the cluster drops (still ~100 kPa) it flips
    # without them. At 45 degrees, between the side stacks.
    low.radial("winglet3", count=4, y=-0.5, angle0=45)
    eng2 = low.stack("LiquidEngineKE-1", stage=4)
    sep = eng2.stack("Decoupler.2", stage=4)
    core, tanks, cones = _cluster_stage(sep, sides=sides, fin="winglet3", hiad=hiad, feet=feet, wheels=wheels,
                                        batteries=batteries)
    # Chutes sit at the top edge of the side tanks, above the centre of mass (hung from points near the CoM the
    # lander settles tilted); the tank joints take the opening shock. They drop with the first stage.
    for i, (t, cone) in enumerate(zip(tanks, cones)):
        a = 360.0 * i / sides
        if mains_per_side and mains_on == "upper":
            # On the upper stage tank, far above the centre of mass: hung from chutes near the CoM level (the side
            # tanks) the lander settles ~43 degrees tilted. Each chute sits on a small radial decoupler so the
            # chutes can be dropped on the ground before lift-off instead of flying to orbit.
            decs = up.radial("radialDecoupler", count=mains_per_side, y=1.5, angle0=a - 30, arc=60) \
                if mains_per_side > 1 else up.radial("radialDecoupler", count=1, y=1.5, angle0=a)
            for d in decs:
                ch = d.side("parachuteRadial", depth=0.23)
                ch.extra += EVE_MAIN_CHUTE
        elif mains_per_side:
            mains = t.radial("parachuteRadial", count=mains_per_side, y=1.75, angle0=a - 30, arc=60) \
                if mains_per_side > 1 else t.radial("parachuteRadial", count=1, y=1.75, angle0=a)
            for ch in mains:
                ch.extra += EVE_MAIN_CHUTE
        if drogues_per_side:
            # (on the tank, not the nose cone: opening at ~450 m/s the drogues tore nose cones off)
            for ch in t.radial("radialDrogue", count=drogues_per_side, y=1.75, angle0=a + 180.0 / sides,
                               arc=360.0 if drogues_per_side == 1 else 90):
                ch.extra += NO_STAGING_CHUTE
        if radial_batteries:
            # power for the reaction wheels through the long, actively held entry and descent (radial, so they
            # add no joints to the load path; they drop with the first stage)
            t.radial("ksp.r.largeBatteryPack", count=radial_batteries, y=-0.6, angle0=a + 90, arc=180)
    if uprighting_chutes:
        # A few chutes high up on the upper stage tank, opened only once the lander is slow under the main chutes.
        # Hung only from the side-tank chutes (near the CoM) the lander settles ~43 degrees tilted; these pull it
        # upright. They sit on small radial decouplers and are dropped on the ground before lift-off.
        for d in up.radial("radialDecoupler", count=uprighting_chutes, y=1.5, angle0=45):
            ch = d.side("parachuteRadial", depth=0.23)
            ch.extra += UPRIGHT_CHUTE
    _stiffen(c)
    return c


def eve_lander_up(k) -> Craft:
    c = eve_lander(k, mains_on="upper")
    c.name = "Eve Lander U"
    return c


def eve_lander_asc(k) -> Craft:
    """Ascent test article: the lander as it stands on Eve after landing (no HIAD, no heat shields)."""
    c = eve_lander(k, hiad_top=False, feet=None)
    c.name = "Eve Lander Asc"
    return c


def eve_lander_hiad(k) -> Craft:
    c = eve_lander(k, hiad=True, feet=None)
    c.name = "Eve Lander H"
    return c


def capsule_test(k) -> Craft:
    c = Craft("Capsule Test", k)
    pod = c.root("mk1pod.v2")
    pod.stack("parachuteSingle", my_node="top", its_node="bottom")
    shield = pod.stack("HeatShield1")
    t = shield.stack("fuelTank")
    t.stack("liquidEngine3.v2", stage=1)
    return c


def hiad_squat_test(k) -> Craft:
    """Entry-stability test article: the lander's cluster with only a pod on top, on a HIAD."""
    c = Craft("HIAD Squat", k)
    pod = c.root("mk1pod.v2")
    probe = pod.stack("probeStackSmall")
    ad = probe.stack("largeAdapter")
    w1 = ad.stack("asasmodule1-2")
    w2 = w1.stack("asasmodule1-2")
    _cluster_stage(w2, sides=4, fin=None, hiad=True)
    return c


def eve_lander_noshield(k) -> Craft:
    c = eve_lander(k, hiad=False, wheels=0)
    c.name = "Eve Lander NS"
    return c


DESIGNS = {"eve_express": eve_express, "eve_ascent_test": eve_ascent_test, "eve_ascent_serial": eve_ascent_serial,
           "eve_ascent_cluster": eve_ascent_cluster, "eve_lander": eve_lander,
           "eve_lander_noshield": eve_lander_noshield, "eve_lander_hiad": eve_lander_hiad,
           "hiad_squat_test": hiad_squat_test, "eve_lander_up": eve_lander_up, "eve_lander_asc": eve_lander_asc, "capsule_test": capsule_test}
