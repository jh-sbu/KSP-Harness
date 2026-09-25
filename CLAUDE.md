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

## Known open issues (as of 2026-09-25)

- Generic `*Dialog` window detection and the all-scenes `alert.dialog` watch are built but NOT yet verified
  in game (added after the last test run). Check that it lists the post-recovery "Mission Summary"
  (MissionRecoveryDialog), and check the frame-time cost of the 1 Hz FindObjectsOfType<MonoBehaviour>() scan.
- `reenter` corrects periapsis whenever it is above target, with no tolerance (spent a 0.2 m/s burn for 1.4 km).
- `land` brakes at partial throttle along the whole stopping profile: safe but ~150 m/s more than a
  tight suicide burn on the Mun.
- Node burn-time estimates only count the current stage's engines, so burns that stage midway start late.
- `reenter` doesn't check parachute state. It relies on stock "deploy when safe".
