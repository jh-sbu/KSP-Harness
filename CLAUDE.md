# Playing KSP through this harness

See README.md for the command reference. Operating rules learned the hard way:

- **Never wait on the success marker alone.** Run long routines in the background with their exit
  code appended (`sh -c 'bin/ksp run X; echo "ROUTINE EXIT $?"' > logs/x.log`), and watch them with a
  monitor whose filter includes `ALERT|mission error|Traceback|ROUTINE EXIT`. For ad-hoc flying,
  `bin/ksp watch until=<regex>` exits 3 on any `alert.*` event.
- **Look at the screen at milestones.** Some UI windows are not visible to `dialogs` until someone
  teaches the harness about them. Routines save screenshots to `logs/shots/`; read the latest one
  after each milestone. If a window appears that `dialogs` doesn't list, add detection for it in
  `CmdGame.Dialogs()`.
- Quicksave before risky steps (`bin/ksp quicksave`). Restore with
  `bin/ksp load_game save=<name> file=quicksave flight=true`.
- Autostage: set `autostage on=true stop_at=<lowest stage to activate>` so chutes and the final
  decoupler are not fired. `ascend` sets this from the stage delta-v table.
- In sandbox mode, science values are empty (there is no R&D). That is expected.
- `eval` can call any static member chain with literal arguments. Use it to unblock yourself
  without a plugin rebuild. Plugin changes need `bin/ksp install && bin/ksp restart`.
- Monitor filters: `grep -E` has no lookahead (`(?!…)` makes grep exit 2 and the monitor dies silently).
  Filter with a second `grep -v` stage instead, and end monitor pipelines with `echo "MONITOR PIPELINE ENDED"`.
- Don't `pkill -f` a pattern that also appears in your own command line (it kills your own shell).
- Stock Kerbal X: the Mk16-XL chute lands the 3-seat pod at ~9 m/s, which destroys the 2.5 m heat shield
  (its crash tolerance). Crew survive, but routines raise MissionError on the part-destroyed crash event.
- Before long unattended sessions, turn off screen blanking / screen lock in the desktop's system settings.
  If the game hangs at startup (Player.log ends at "Desktop is ... Hz", 0% CPU), the display is probably
  powered off: on KDE/KWin (Wayland) a new game window never gets mapped while every output is off.
  Wake the screen and restart the game. Other desktops are untested.
- Harness autopilot state (autostage, attitude mode) is static and survives reverts and scene changes.
  `ascend` disables autostage before staging on the pad, and `Autopilot.Reset()` clears it.
- Interplanetary: never trust a fixed-time Lambert solution near a 180-degree transfer (Kerbin-Eve is one).
  The transfer plane is ill-defined and the "solution" demands huge out-of-plane burns. Eject in plane and
  fix the plane mid-course with B-plane targeting (`course_correct`).
- Jettison a capsule's transfer stage only shortly before the atmosphere (`reenter` does it 100 km above).
  The decoupler's few m/s push, applied an hour out on an arrival hyperbola, moved periapsis by 25 km.
- Warping to a far transfer window in a 100 km orbit ran much faster than 50x; always bound the search
  window and check the planned departure date before warping.

## Known open issues (as of 2026-09-25)

- Generic `*Dialog` detection is verified: it lists MissionRecoveryDialog ("Mission Summary") and
  FlightResultsDialog. The frame-time cost of the 1 Hz FindObjectsOfType<MonoBehaviour>() scan is still unmeasured.
- `reenter` corrects periapsis whenever it is above target, with no tolerance (spent a 0.2 m/s burn for 1.4 km).
- `land` brakes at partial throttle along the whole stopping profile: safe but ~150 m/s more than a
  tight suicide burn on the Mun.
- Node burn-time estimates only count the current stage's engines, so burns that stage midway start late.
- `reenter` doesn't check parachute state. It relies on stock "deploy when safe".
