# Eve landing and return: how others have done it vs. our strategy

Written 2026-09-28. Sources: the harness logs (`logs/eve_*.log`), the `EveLanderDev` save, the
`designs.py`/`flight.py` code as of commit f96b366, the stock part configs in `GameData`, and community
material (linked at the end). The KSP wiki and forum pages would not load directly (403 or 402), so
their content comes from search excerpts and mirrors. Numbers marked *est.* are my own estimates.

## 1. Summary

**Verdict: as it stood, the strategy was unlikely to produce a complete crewed Eve round trip.** Some
parts of it work: the interplanetary navigation, the idea of entering engines-first, dropping landing-only
mass before lift-off, and testing each phase from a cheat-placed save. But the approach has three
structural problems, and all three break with how successful players fly this mission:

1. **The lander is about 2–4× heavier than typical successful crewed Eve landers**, about 152 t on the
   surface. Stock designs flown from sea level with a Mk1 pod are 37–80 t. The extra mass comes from
   carrying the entire Kerbin-return stage down and back up, and from using Mastodon engines.
2. **The ascent was validated at a more favourable site than the landings achieved.** Every ascent test
   started from 3.5 km altitude (placed there by cheat). The three real landings came down at 2,075 m,
   732 m and 0 m (sea). Nothing controls where the lander lands, and ascent from sea level was never
   tested.
3. **The descent was unreliable, and the Kerbin launch and transfer of the full stack were never built.**
   Only 3 of about 31 descent runs landed intact; the last run (33) was lost. The lander was always
   teleported into Eve orbit. A ~155 t lander in low Eve orbit implies a Kerbin launcher in the
   thousands of tonnes (*est.*).

Players who succeed tend to follow a common pattern. They build a small ascent vehicle (Mk1 pod, Vector,
Aerospike or Mammoth engines, asparagus staging, TWR 1.2–1.5 at the landing site). They land it with a
heat shield and chutes on a chosen high-altitude site, ideally mountains up to 7.5 km. They usually leave
the Kerbin-return vehicle in Eve orbit and rendezvous with it after the ascent.

## 2. What we were doing

### Architecture (single vehicle, direct return)

`designs.eve_lander` (the craft), `flight.descend`, `flight.eve_ascent`:

| Element | Our design |
|---|---|
| Crew return | Mk1 pod + 1.25 m heat shield + chute on a Terrier/FL-T800 stage (probe core, reaction wheel, batteries, solar panels). It flies Eve orbit → Kerbin by itself. |
| Ascent 2nd stage | X200-32 + X200-16 tanks, one **Mastodon** (KE-1), 4 fins |
| Ascent 1st stage | core + 4 side X200-32 stacks, 5 **Mastodons**, all firing together and dropped together (no asparagus crossfeed) |
| Entry | engines-first, 10 m HIAD on a girder above the pod (a "shuttlecock"), 2.5 m heat shields as feet under each engine, active retrograde hold |
| Landing | 12 Mk2-R mains on the side tanks, 8 "uprighting" chutes on radial decouplers, HIAD released at ~2.8 km, feet dropped below 400 m, touchdown under chutes |
| Landing site | wherever an equatorial deorbit to a 40 km periapsis ends up (no targeting) |
| Mass | 151.8 t on the surface after dropping the chutes (`eve_ascent_full5.log`), ~98 t of it propellant |
| Kerbin → Eve | not built for the lander; tests used `dev_orbit` (cheat) to start in a 100 km Eve orbit |

### What the tests showed

- **Ascent:** after fixes, 3 consecutive runs reached a ~100 km Eve orbit (`eve_ascent_full3–5`), each
  starting from **3.5 km** (the `lander_asc_surface` save is at 3,537 m). In run 5, the Terrier stage
  supplied about 430 m/s of the circularisation burn. That leaves it an estimated ~2.5 km/s (*est.*:
  wet ~6.7 t, dry ~2.7 t, Isp 345 s), enough for the ~1.5 km/s trip home with aerobraking at Kerbin.
- **Descent:** runs 3–33. Landings in 26 (2,075 m, Highlands), 27 (732 m, Midlands) and 32 (0 m,
  Explodium Sea). Failures, in order of appearance: structural failure or heat at 45–75 km (pod,
  adapter, engines or shields destroyed as the stack flipped or overheated), then tank and engine
  breakups under chute-opening loads, then 50–150 m/s impacts at the end (runs 25, 29, 31, 33). The crew
  was killed in 5 runs.
- **Interplanetary:** the Eve Express mission (orbit only, no landing) flew the transfer, capture,
  return and re-entry. Navigation is proven. Rendezvous, docking and landing-site targeting do not exist
  in the harness.

## 3. How successful missions have been flown

### 3.1 Architecture

Two patterns come up again and again:

- **Orbital rendezvous (the most common and most efficient).** The lander carries only the ascent
  vehicle (EAV) and a light crew capsule. A separate return vehicle (often nuclear, around 6 km/s for the
  whole round trip) stays in Eve orbit. After the ascent, the EAV rendezvouses and docks with it, or the
  pilot transfers by EVA once the craft are within ~1 km. [Steam: Eve surface return help], [KSP wiki:
  Returning From Eve] (via search excerpt)
- **Direct ascent with a small EAV.** If the ascent vehicle goes all the way home, its final stage is a
  Terrier with a light pod, and the whole vehicle is designed around minimum mass.

The reason is the ascent's mass multiplier. Eve surface to low orbit needs ~6–7 km/s of local delta-v
(often quoted as ~8 km/s rated in vacuum). So every kilogram on top of the EAV costs roughly 8–15 kg on
the surface, and that surface mass then has to be carried from Kerbin to Eve and slowed down.

### 3.2 Ascent vehicle

| Craft | Mass (lander/EAV) | Engines | Notes |
|---|---|---|---|
| Mephisto "Simple Eve Ascent Vehicle" | **78.8 t**, 50 parts | 3× Vector + Terrier | from sea level; asparagus side boosters; "start your gravity turn very very gently" |
| Mephisto "X-51a Thetis" (full lander) | **36.7 t**, 86 parts | Vector + Terrier (Sparks for deorbit) | heat shield + chutes (opened at ~3 km); throttle to 70% at 2 km and 20% at 5 km to manage heat and drag |
| JedTech "Eve Ascent Vehicle" | **44.4 t**, 75 parts | not listed | KSP 1.3.1 |
| **Ours** | **151.8 t** | 6× Mastodon + Terrier | tested only from 3.5 km |

Consistent community rules:

- **Engines:** Vector, Aerospike and Mammoth are the standard Eve sea-level engines, with Skipper,
  Mainsail and Twin-Boar mentioned less often. Vector plus four FL-T800s gives an Eve sea-level TWR of
  1.67, with 2,462 m/s of delta-v at sea level against 4,014 m/s in vacuum. That puts the Vector's Isp at
  5 atm at about 190 s.
- **TWR 1.2–1.5 at the landing altitude**, more is rarely useful. The vehicle should be skinny and
  low-drag with a Mk1 pod on top. Throttle back in the thick lower atmosphere to stay under the heat
  and drag limits.
- **Asparagus staging** so that empty tanks come off early.
- **Drop everything not needed for the ascent** (chutes, science, batteries on decouplers) before
  lift-off. We do this too.

Why the engine matters. The stock configs (`atmosphereCurve`) give these values:

| Engine | Vacuum Isp | Isp reaches 0 at | Isp at ~3 atm (3.5 km) *est.* | Isp at 5 atm (sea level) *est.* |
|---|---|---|---|---|
| Vector (SSME) / Mammoth | 315 | 12 atm | ~240 | ~190 (agrees with the community's 2,462 / 4,014 figure) |
| **Mastodon (KE-1)** | 305 | **9 atm** | ~218 | **~150** |
| Aerospike | 340 | 20 atm (explicit key 5 atm → 230) | ~260 | 230 |
| Rhino | 340 | 5 atm | ~100 | **0** |

In KSP, thrust scales with Isp, so at Eve sea level the Mastodon loses about half its vacuum thrust *and*
its fuel efficiency. We picked the Mastodon because it has the most thrust in its size class. But at sea
level its Isp is ~20% below the Vector's, and that loss compounds over the whole first stage.

### 3.3 Landing site

- The highest point on Eve is **7,540 m** (near 25° S, 158.5° W). Players repeatedly say that landing
  high "can drastically reduce the delta-v required" and that anything below ~5 km makes the ascent much
  harder.
- The standard advice is to **target highlands** and still budget for a lower landing, "since you cannot
  guarantee that you will land on the highest point".

### 3.4 Entry and descent

- Players report the same failure we hit: **a large stack flips on a 10 m HIAD**. Their fixes: keep the
  centre of mass low and ahead of the centre of pressure, add reaction wheels, add fins, or put a second
  HIAD at the other end to act as a drag stabiliser. The last one is essentially our shuttlecock HIAD.
- **Enter steeply rather than shallowly.** A low periapsis slows the craft faster and burns less
  ablator, because heat has less time to soak into the parts.
- **Chutes:** because the atmosphere is so thick, Eve needs roughly half the chute area Kerbin would for
  the same mass. The wiki tutorial uses 24 Mk2-R chutes on 12 radial decouplers, opened in stages
  (drogue, then half the mains, then the rest at 500 m) to spread the opening loads.
- **Mining to lighten the lander** (landing with empty tanks, a drill and an ISRU converter) is possible
  in the wiki tutorial. It is not an option in our sandbox without those parts in the design.

## 4. Comparison

| Topic | Community practice | Ours | Assessment |
|---|---|---|---|
| Return architecture | rendezvous with an orbiter, or a minimal direct EAV | full Terrier return stage (~6.7 t: pod, heat shield, wheel, batteries, panels) carried down and up | **Main mass driver.** Each ton on top costs ~10 t on the surface. |
| EAV mass | 37–80 t | 152 t | 2–4× heavier, which inflates descent loads, chute count and Kerbin launch size |
| First-stage engines | Vector / Mammoth / Aerospike | Mastodon | worst of the "workable" engines at 5 atm (Isp ~150 vs ~190) |
| Staging | asparagus, stages dropped early | cluster, all stacks dropped together; stage 2 ignites at 18 km, still at 106 kPa | simpler and more robust, but carries empty tanks longer |
| Landing site | chosen highlands or mountains; ascent budgeted for the worst case | uncontrolled; 0–2 km achieved | **ascent tests (3.5 km) don't cover where we actually land** |
| Entry | heat shield + chutes, shielded area wider than the craft, stable CoM/CoP | engines-first behind a 10 m HIAD on top + feet shields, active attitude hold | a creative, community-endorsed idea, but 3/31 landings shows the craft is still at the edge of controllability |
| Chutes | staged openings, many radial chutes on decouplers | 12 mains opened together at 220 m/s + 8 uprighting | the opening shock caused several breakups; staged openings would help |
| Testing | test engines and ascent at the real site before crewing the mission | cheat-placed phase tests (good) | good method, but test conditions were more favourable than the mission's |
| Kerbin launch and transfer | often split into several launches with docking in LKO | not built | a single-launch ~2,500–4,000 t launcher (*est.*) is a separate, unsolved project |

## 5. Would it have worked?

Taking each phase in turn:

1. **Kerbin launch and transfer of a ~155 t lander into low Eve orbit.** Not attempted. It is feasible in
   principle but a large project. The lander's entry stability also rules out using it for aerocapture,
   so the capture into a 100 km orbit has to be a propulsive burn.
2. **Entry and landing.** About 10% success so far (3/31; 3/10 in the latest design generation), and the
   final run failed. With a real crew and no quickload, this phase alone would probably end the mission.
3. **Ascent.** Probably works from ~3.5 km. From the altitudes we actually reached (0–2 km), the
   Mastodons lose a lot of Isp (roughly 218 → 150 s from 3.5 km down to sea level, *est.*), and
   lift-off TWR falls to ~1.3 (*est.*: 5 × 1350 kN × 150/305 ≈ 3,300 kN against 152 t × 16.7 m/s²
   ≈ 2,540 kN). I estimate the extra cost at several hundred to ~1,000 m/s. The Terrier's ~2.5 km/s
   spare probably covers it, but at the expense of the ~1.5 km/s return budget. Only a sea-level test
   can settle this, and none was run.
4. **Eve orbit → Kerbin.** Very likely fine. The navigation code is proven and the Terrier stage has the
   delta-v.

The probability of all four phases succeeding is low mainly because of phase 2, and phase 1 is a
significant unbuilt dependency. The design choices behind phase 2's difficulty (a heavy, tall stack; one
big chute event) come from phase 3's mass. The root cause is architectural.

## 6. Recommendations

In order of expected payoff:

1. **Shrink the lander.** Redesign the EAV around 40–80 t: a Mk1 pod on a Terrier final stage, Vector
   (or Aerospike and Vector) asparagus lower stages, and TWR 1.3–1.5 at *sea level* (budget for the
   worst case). This fixes the descent loads, chute count and Kerbin launcher size at the same time.
2. **Decide between rendezvous and direct ascent.** Rendezvous is the community's efficient choice. It
   would need new harness capabilities: target selection, phasing, a closest-approach burn, and
   docking or an EVA transfer. If we stay with direct ascent, cut the return stage down to the
   essentials: the pod, its Kerbin heat shield and chute, the Terrier and its tank. The advanced reaction
   wheel, battery bank, probe core and solar panels should all be justified against their mass.
3. **Target the landing site.** Add a deorbit-targeting routine (choose the deorbit burn point and
   periapsis to hit a latitude and longitude, then correct with lift and drag or brief burns) and aim for
   highlands at ≥3 km. Until that works, **test the ascent from sea level**
   (`dev_place ... alt=` over ocean/lowland).
4. **Descent:** open the chutes in stages (a drogue, then half the mains, then the rest) to spread the
   load. With a smaller lander, go back to the simpler "heat shield at the bottom, chutes on top"
   layout that most successful players use.
5. **Split the Kerbin side:** launch the lander and a transfer/return orbiter separately and dock in
   LKO, or accept a single large launch only once the lander mass is known.

## Sources

- [Tutorial: Returning From Eve – KSP Wiki](https://wiki.kerbalspaceprogram.com/wiki/Tutorial:Returning_From_Eve)
- [Tutorial: How to get to Eve – KSP Wiki](https://wiki.kerbalspaceprogram.com/wiki/Tutorial:How_to_get_to_Eve)
- [Eve – KSP Wiki](https://wiki.kerbalspaceprogram.com/wiki/Eve)
- [Eve surface return help – Steam discussions](https://steamcommunity.com/app/220200/discussions/0/348292787751669545/)
- [What are good engines for Eve's atmosphere? – Steam discussions](https://steamcommunity.com/app/220200/discussions/0/135508833654638038/)
- [Eve manned mission – Steam discussions](https://steamcommunity.com/app/220200/discussions/0/3317353727662440222/)
- [Problem with Eve entry with 10 m heat shield – Steam discussions](https://steamcommunity.com/app/220200/discussions/0/348293292498501853/)
- [Large Eve lander keeps flipping while using 10m heat shield – KSP Forums](https://forum.kerbalspaceprogram.com/topic/145618-large-eve-lander-keeps-flipping-while-using-10m-heat-shield/)
- [Highest Mountain on Eve – KSP Forums](https://forum.kerbalspaceprogram.com/topic/179152-highest-mountain-on-eve/)
- [Lightest Eve Launcher challenge – KSP Forums](https://forum.kerbalspaceprogram.com/topic/190115-lightest-eve-launcher/)
- [ESLA Mk.1 – Eve Sea-Level Ascender – KSP Forums](https://forum.kerbalspaceprogram.com/topic/111816-esla-mk1-eve-sea-level-ascender/)
- [Delta V to ascent Eve – KSP Forums](https://forum.kerbalspaceprogram.com/topic/180825-delta-v-to-ascent-eve/)
- [Simple Eve Ascent Vehicle – KerbalX](https://kerbalx.com/Mephisto/Simple-Eve-Ascent-Vehicle)
- [X-51a Thetis (Eve Lander) – KerbalX](https://kerbalx.com/Mephisto/X-51a-Thetis-Eve-Lander)
- [Eve Ascent Vehicle (JedTech) – KerbalX](https://kerbalx.com/JedTech/Eve-Ascent-Vehicle)
- [Eve Optimized Engines mod (context on stock engine limits)](https://github.com/OhioBob/Eve-Optimized-Engines/releases/tag/3.0.2)
