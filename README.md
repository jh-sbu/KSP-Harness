# KSP Harness

A harness that lets a program (or an AI agent) play Kerbal Space Program 1.12 autonomously.

It has two halves:

- **`plugin/`**: a KSP addon (C#, `KSPHarness.dll`) that runs inside the game. It listens on
  `127.0.0.1:50555` for newline-delimited JSON commands, executes them on Unity's main thread,
  and runs closed-loop control (attitude autopilot, maneuver-node executor, powered landing,
  autostaging) every physics tick. It also records game events and raises `alert.*` events
  for silent failures (no thrust while throttled up, engine flameouts, unexpected windows,
  aborted burns).
- **`python/kspharness/`**: a client library, the `bin/ksp` CLI, and high-level flight
  routines (ascent, node execution, Hohmann transfer, landing, re-entry).

## Setup

Requirements: KSP 1.12.x (native Linux build), .NET SDK (to build the plugin), Python 3.10+.

```sh
bin/ksp install      # build the plugin and copy it to <KSP>/GameData/KSPHarness
bin/ksp start        # launch KSP directly and wait for the main menu (~1 min)
bin/ksp stop
```

Set `KSP_DIR` if the game is not at `~/.local/share/Steam/steamapps/common/Kerbal Space Program`,
and `KSP_HARNESS_PORT` to change the port.

## Usage

Any plugin command: `bin/ksp <command> key=value ...` (values are parsed as JSON when possible).
`bin/ksp help` lists all commands, and `bin/ksp help <command>` shows the arguments for one.

```sh
bin/ksp new_game name=MyGame mode=sandbox        # or career / science
bin/ksp wait SPACECENTER
bin/ksp dialog_click button=thanks                # close the welcome popup
bin/ksp launch craft="Kerbal X"
bin/ksp wait FLIGHT
bin/ksp run ascend altitude=80000                 # gravity turn + circularize
bin/ksp run transfer target=Mun                   # Hohmann transfer + capture
bin/ksp run land                                  # deorbit + suicide burn
bin/ksp shot                                      # screenshot -> prints PNG path
bin/ksp watch until='land:' timeout=3600          # stream events; exit 3 on any alert
```

From Python:

```python
from kspharness import KSP
k = KSP()
k.vessel()["orbit"]["apoapsis"]
k.ap(mode="prograde")
k.plan_circularize(at="apoapsis"); k.node_exec()
```

### Command groups

| Area | Commands |
|---|---|
| Game / scenes | `status`, `saves`, `new_game`, `load_game`, `save`, `quicksave`, `quickload`, `scene`, `dialogs`, `dialog_click`, `pause`, `warp` |
| Craft | `crafts`, `craft_check`, `launch`, `recover`, `revert`, `part_search`, `part_info` (attach-node geometry for the craft generator) |
| Telemetry | `vessel`, `orbit`, `stages`, `bodies`, `vessels`, `parts`, `ap_status`, `events` |
| Control | `throttle`, `stage`, `action_group`, `sas_mode`, `controls`, `autothrottle`, `autostage` |
| Autopilot | `ap` (attitude hold: prograde, normal, pitch_heading, node, target…), `node_exec`, `land` |
| Maneuvers | `nodes`, `node_add`, `node_update`, `node_clear`, `plan_circularize`, `plan_apsis`, `plan_inclination`, `plan_hohmann`, `plan_return`, `node_approach` (encounter / closest approach to a body along the planned trajectory) |
| Parts | `part_event`, `part_action`, `part_field`, `parachutes` |
| Science / crew | `science`, `science_run`, `science_transmit`, `science_reset`, `eva`, `board`, `plant_flag`, `crew` |
| Career | `contracts`, `contract_accept`/`decline`/`cancel`, `tech`, `tech_research`, `facilities`, `facility_upgrade` |
| View | `screenshot`, `map`, `camera`, `switch_vessel`, `target` |
| Escape hatch | `eval` / `set`: reflection over any static member chain, e.g. `eval expr=FlightGlobals.ActiveVessel.orbit.ApA` |

## Craft design and interplanetary missions

`python/kspharness/craft.py` writes `.craft` files from a short Python description (stack and
radial-symmetry attachment, staging), using attach-node geometry read from the game. Designs live in
`python/kspharness/designs.py`; `bin/ksp build <design>` writes one into the current save's VAB folder.

`python/kspharness/interplanetary.py` does the interplanetary planning in Python (vectors in KSP's orbit
frame, built from game state vectors):

- Kepler propagation, a universal-variable Lambert solver, and a porkchop search over departure date and
  flight time. The ejection cost includes the plane change needed when the departure asymptote is out of
  the parking-orbit plane, and the earliest window within 25 m/s of the best is preferred.
- Ejection geometry: the burn on the parking orbit that leaves on a hyperbola with a given v-infinity
  vector. A differential correction reads the game's heliocentric patch at SOI exit and fixes the
  in-plane miss (a 2-D intercept at the planned arrival time).
- B-plane targeting for mid-course corrections: the minimum-norm burn that passes the target at the miss
  distance for a given periapsis, prograde and near-equatorial, with free arrival time. It avoids the
  near-180-degree Lambert singularity that makes Kerbin-Eve transfers ask for absurd plane changes.
  `course_correct` scans burn dates across the cruise and flies the cheapest.

`bin/ksp run eve_mission` flies the whole Eve round trip in the stock-parts `eve_express` design:
wait for the window at the KSC, launch, eject, correct course, capture into a 650 km circular Eve orbit
(above the 600 km limit for 100,000x warp), wait for the return window, eject, correct course, re-enter
and recover. Each phase saves `eve_<phase>` first; resume with `phase=<phase>`.

## Design notes

- All commands run on the main thread from a queue drained in `Update()`. Client threads block
  (up to 60 s) for the reply. Long operations (burns, landings) are started by a command and
  then polled via `ap_status` / `events`.
- The attitude controller is a custom rate-based PD loop: the pointing error maps to a desired
  angular rate, limited by available torque over the moment of inertia. It does not use stock SAS
  (pass `driver=sas` to use SAS instead).
- Maneuver planning uses KSP's own `Orbit` class. Node coordinates are verified against
  stock maneuver nodes by `debug_frames`.
- Plugin changes take effect only after a game restart (`bin/ksp install && bin/ksp restart`).
