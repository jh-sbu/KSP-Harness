using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace KSPHarness
{
    static class CmdFlight
    {
        public static Vessel RequireVessel()
        {
            if (!HighLogic.LoadedSceneIsFlight) throw new HarnessException("not in flight (scene is " + HighLogic.LoadedScene + ")");
            var v = FlightGlobals.ActiveVessel;
            if (v == null) throw new HarnessException("no active vessel");
            return v;
        }

        static double R(double x, int digits = 3) => Math.Round(x, digits);

        // ------------------------------------------------------------------ telemetry

        public static Dictionary<string, object> OrbitInfo(Orbit o)
        {
            double ut = Planetarium.GetUniversalTime();
            var d = new Dictionary<string, object>
            {
                ["body"] = o.referenceBody.bodyName,
                ["apoapsis"] = R(o.ApA, 1),
                ["periapsis"] = R(o.PeA, 1),
                ["sma"] = R(o.semiMajorAxis, 1),
                ["eccentricity"] = R(o.eccentricity, 6),
                ["inclination"] = R(o.inclination, 4),
                ["lan"] = R(o.LAN, 4),
                ["arg_periapsis"] = R(o.argumentOfPeriapsis, 4),
                ["period"] = R(o.period, 1),
                ["time_to_apoapsis"] = o.eccentricity < 1 ? (object)R(o.timeToAp, 1) : null,
                ["time_to_periapsis"] = R(o.timeToPe, 1),
                ["true_anomaly_deg"] = R(o.trueAnomaly * 180 / Math.PI, 3),
                ["transition"] = o.patchEndTransition.ToString(),
            };
            if (o.patchEndTransition != Orbit.PatchTransitionType.FINAL && o.patchEndTransition != Orbit.PatchTransitionType.INITIAL)
            {
                d["time_to_transition"] = R(o.EndUT - ut, 1);
                if (o.nextPatch != null && o.nextPatch.activePatch)
                {
                    d["next_body"] = o.nextPatch.referenceBody.bodyName;
                    d["next_periapsis"] = R(o.nextPatch.PeA, 1);
                }
            }
            return d;
        }

        /// <summary>Navball-style attitude of the vessel: pitch, heading, roll (degrees).</summary>
        public static Dictionary<string, object> Attitude(Vessel v)
        {
            var t = v.ReferenceTransform;
            Vector3d up = Autopilot.Up(v);
            Vector3d north = Vector3d.Exclude(up, v.north).normalized;
            Vector3d east = Vector3d.Exclude(up, v.east).normalized;
            Vector3d fwd = t.up;
            double pitch = 90 - Vector3d.Angle(fwd, up);
            Vector3d hz = Vector3d.Exclude(up, fwd);
            double heading = Math.Atan2(Vector3d.Dot(hz, east), Vector3d.Dot(hz, north)) * 180 / Math.PI;
            if (heading < 0) heading += 360;
            Vector3d top = -t.forward;
            Vector3d refTop = Vector3d.Exclude(fwd, up);
            double roll = double.NaN;
            if (refTop.sqrMagnitude > 1e-6)
            {
                refTop = refTop.normalized;
                roll = Vector3d.Angle(refTop, Vector3d.Exclude(fwd, top)) * Math.Sign(Vector3d.Dot(Vector3d.Cross(refTop, top), fwd));
            }
            return new Dictionary<string, object> { ["pitch"] = R(pitch, 2), ["heading"] = R(heading, 2), ["roll"] = R(roll, 2) };
        }

        public static Dictionary<string, object> Summary(Vessel v)
        {
            return new Dictionary<string, object>
            {
                ["name"] = Harness.L(v.vesselName),
                ["situation"] = v.situation.ToString(),
                ["body"] = v.mainBody.bodyName,
                ["altitude"] = R(v.altitude, 1),
                ["apoapsis"] = R(v.orbit.ApA, 1),
                ["periapsis"] = R(v.orbit.PeA, 1),
                ["surface_speed"] = R(v.srfSpeed, 1),
                ["orbital_speed"] = R(v.obt_speed, 1),
                ["stage"] = v.currentStage,
            };
        }

        static Dictionary<string, object> Resources(IEnumerable<Part> parts)
        {
            var tot = new Dictionary<string, double[]>();
            foreach (var p in parts)
                foreach (var r in p.Resources)
                {
                    if (!tot.TryGetValue(r.resourceName, out var a)) tot[r.resourceName] = a = new double[2];
                    a[0] += r.amount;
                    a[1] += r.maxAmount;
                }
            return tot.ToDictionary(kv => kv.Key, kv => (object)new Dictionary<string, object> { ["amount"] = R(kv.Value[0], 2), ["max"] = R(kv.Value[1], 2) });
        }

        [Cmd("vessel", "Full telemetry for the active vessel.")]
        static object VesselInfo(Args a)
        {
            var v = RequireVessel();
            var body = v.mainBody;
            double r = (v.CoMD - body.position).magnitude;
            double gLocal = body.gravParameter / (r * r);
            var prop = Autopilot.CurrentPropulsion(v);
            double curThrust = v.FindPartModulesImplementing<ModuleEngines>().Sum(e => (double)e.finalThrust);
            var d = new Dictionary<string, object>
            {
                ["name"] = Harness.L(v.vesselName),
                ["id"] = v.id.ToString(),
                ["type"] = v.vesselType.ToString(),
                ["situation"] = v.situation.ToString(),
                ["body"] = body.bodyName,
                ["ut"] = R(Planetarium.GetUniversalTime(), 2),
                ["met"] = R(v.missionTime, 1),
                ["controllable"] = v.IsControllable,
                ["altitude"] = R(v.altitude, 1),
                ["radar_altitude"] = R(v.radarAltitude, 1),
                ["terrain_height"] = R(v.terrainAltitude, 1),
                ["latitude"] = R(v.latitude, 5),
                ["longitude"] = R(v.longitude, 5),
                ["biome"] = ScienceUtil.GetExperimentBiome(body, v.latitude, v.longitude),
                ["orbital_speed"] = R(v.obt_speed, 2),
                ["surface_speed"] = R(v.srfSpeed, 2),
                ["vertical_speed"] = R(v.verticalSpeed, 2),
                ["horizontal_speed"] = R(v.horizontalSrfSpeed, 2),
                ["mach"] = R(v.mach, 3),
                ["dynamic_pressure_kpa"] = R(v.dynamicPressurekPa, 3),
                ["static_pressure_kpa"] = R(v.staticPressurekPa, 3),
                ["g_force"] = R(v.geeForce, 2),
                ["local_gravity"] = R(gLocal, 3),
                ["mass"] = R(v.totalMass, 3),
                ["thrust"] = R(curThrust, 2),
                ["max_thrust"] = R(prop.Thrust, 2),
                ["isp"] = R(prop.Isp, 1),
                ["twr_max"] = R(prop.Thrust / Math.Max(v.totalMass * gLocal, 1e-9), 3),
                ["attitude"] = Attitude(v),
                ["orbit"] = OrbitInfo(v.orbit),
                ["control"] = new Dictionary<string, object>
                {
                    ["throttle"] = R(v.ctrlState.mainThrottle, 3),
                    ["sas"] = v.ActionGroups[KSPActionGroup.SAS],
                    ["sas_mode"] = v.Autopilot.Mode.ToString(),
                    ["rcs"] = v.ActionGroups[KSPActionGroup.RCS],
                    ["gear"] = v.ActionGroups[KSPActionGroup.Gear],
                    ["brakes"] = v.ActionGroups[KSPActionGroup.Brakes],
                    ["lights"] = v.ActionGroups[KSPActionGroup.Light],
                },
                ["stage"] = v.currentStage,
                ["resources"] = Resources(v.parts),
                ["crew"] = v.GetVesselCrew().Select(c => c.name).ToList(),
                ["part_count"] = v.parts.Count,
            };
            if (v.VesselDeltaV != null && v.VesselDeltaV.IsReady)
                d["delta_v"] = new Dictionary<string, object>
                {
                    ["total_vac"] = R(v.VesselDeltaV.TotalDeltaVVac, 1),
                    ["total_actual"] = R(v.VesselDeltaV.TotalDeltaVActual, 1),
                    ["current_stage_vac"] = R(v.VesselDeltaV.GetStage(v.currentStage)?.deltaVinVac ?? 0, 1),
                };
            if (v.targetObject != null)
            {
                var tp = v.targetObject.GetTransform().position;
                d["target"] = new Dictionary<string, object>
                {
                    ["name"] = v.targetObject.GetName(),
                    ["distance"] = R((tp - v.CoMD).magnitude, 1),
                    ["relative_speed"] = R((v.obt_velocity - v.targetObject.GetObtVelocity()).magnitude, 2),
                };
            }
            var node = Autopilot.FirstNode(v);
            if (node != null)
                d["next_node"] = new Dictionary<string, object> { ["in"] = R(node.UT - Planetarium.GetUniversalTime(), 1), ["dv"] = R(node.GetBurnVector(v.orbit).magnitude, 2) };
            return d;
        }

        [Cmd("orbit", "Orbit of the active vessel including future patches: {patches=3}")]
        static object OrbitCmd(Args a)
        {
            var v = RequireVessel();
            var res = new List<object>();
            var o = v.orbit;
            int n = (int)a.Num("patches", 3);
            while (o != null && res.Count < n)
            {
                var d = OrbitInfo(o);
                d["start_ut"] = o.StartUT;
                d["end_ut"] = o.EndUT;
                res.Add(d);
                if (o.patchEndTransition == Orbit.PatchTransitionType.FINAL || o.nextPatch == null || !o.nextPatch.activePatch) break;
                o = o.nextPatch;
            }
            return res;
        }

        [Cmd("stages", "Per-stage delta-v / TWR / burn time (stock delta-v calculator).")]
        static object Stages(Args a)
        {
            var v = RequireVessel();
            var dv = v.VesselDeltaV;
            if (dv == null || !dv.IsReady) throw new HarnessException("delta-v not calculated yet");
            return dv.OperatingStageInfo.Select(s => new Dictionary<string, object>
            {
                ["stage"] = s.stage,
                ["dv_vac"] = R(s.deltaVinVac, 1),
                ["dv_asl"] = R(s.deltaVatASL, 1),
                ["dv_actual"] = R(s.deltaVActual, 1),
                ["twr_actual"] = R(s.TWRActual, 2),
                ["twr_vac"] = R(s.TWRVac, 2),
                ["isp_vac"] = R(s.ispVac, 1),
                ["thrust_vac"] = R(s.thrustVac, 1),
                ["burn_time"] = R(s.stageBurnTime, 1),
                ["start_mass"] = R(s.startMass, 3),
                ["end_mass"] = R(s.endMass, 3),
            }).ToList();
        }

        // ------------------------------------------------------------------ basic controls

        [Cmd("throttle", "Set main throttle 0..1: {value}")]
        static object Throttle(Args a)
        {
            RequireVessel();
            float t = Mathf.Clamp01((float)a.ReqNum("value"));
            FlightInputHandler.state.mainThrottle = t;
            Autopilot.ThrottleCmd = null;
            return t;
        }

        [Cmd("stage", "Activate the next stage.")]
        static object Stage(Args a)
        {
            var v = RequireVessel();
            if (!StageManager.CanSeparate && !a.Bool("force")) throw new HarnessException("staging not possible right now");
            StageManager.ActivateNextStage();
            return new Dictionary<string, object> { ["stage"] = v.currentStage };
        }

        static readonly Dictionary<string, KSPActionGroup> groups = new Dictionary<string, KSPActionGroup>
        {
            ["sas"] = KSPActionGroup.SAS, ["rcs"] = KSPActionGroup.RCS, ["gear"] = KSPActionGroup.Gear,
            ["brakes"] = KSPActionGroup.Brakes, ["lights"] = KSPActionGroup.Light, ["abort"] = KSPActionGroup.Abort,
            ["stage"] = KSPActionGroup.Stage,
            ["1"] = KSPActionGroup.Custom01, ["2"] = KSPActionGroup.Custom02, ["3"] = KSPActionGroup.Custom03,
            ["4"] = KSPActionGroup.Custom04, ["5"] = KSPActionGroup.Custom05, ["6"] = KSPActionGroup.Custom06,
            ["7"] = KSPActionGroup.Custom07, ["8"] = KSPActionGroup.Custom08, ["9"] = KSPActionGroup.Custom09,
            ["10"] = KSPActionGroup.Custom10,
        };

        [Cmd("action_group", "Set/toggle an action group: {group=sas|rcs|gear|brakes|lights|abort|1..10, state?:bool (omit to toggle)}")]
        static object ActionGroup(Args a)
        {
            var v = RequireVessel();
            var g = a.ReqStr("group").ToLowerInvariant();
            if (!groups.TryGetValue(g, out var grp)) throw new HarnessException("unknown group " + g);
            if (a.Has("state")) v.ActionGroups.SetGroup(grp, a.Bool("state"));
            else v.ActionGroups.ToggleGroup(grp);
            if (grp == KSPActionGroup.SAS && v.ActionGroups[grp] && Autopilot.AttMode != "off" && Autopilot.Driver == "custom")
                return "warning: SAS will be turned off again while the harness autopilot is steering";
            return v.ActionGroups[grp];
        }

        [Cmd("sas_mode", "Set stock SAS mode (turns SAS on): {mode=StabilityAssist|Prograde|Retrograde|Normal|Antinormal|RadialIn|RadialOut|Target|AntiTarget|Maneuver}")]
        static object SasMode(Args a)
        {
            var v = RequireVessel();
            var mode = (VesselAutopilot.AutopilotMode)Enum.Parse(typeof(VesselAutopilot.AutopilotMode), a.ReqStr("mode"), true);
            Autopilot.AttMode = "off";
            v.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            if (!v.Autopilot.CanSetMode(mode)) throw new HarnessException("mode " + mode + " not available (pilot skill / probe core level?)");
            v.Autopilot.SetMode(mode);
            return mode.ToString();
        }

        // ------------------------------------------------------------------ harness autopilot

        [Cmd("ap", "Harness attitude autopilot: {mode=off|prograde|retrograde|srf_prograde|srf_retrograde|normal|antinormal|radial_out|radial_in|up|down|target|anti_target|target_prograde|target_retrograde|node|pitch_heading|vector, pitch?, heading?, roll?, vector?:[x,y,z] (in north/east/up frame), driver?=custom|sas, max_rate?}")]
        static object Ap(Args a)
        {
            var v = RequireVessel();
            var mode = a.Str("mode", "pitch_heading");
            if (a.Has("driver")) Autopilot.Driver = a.Str("driver");
            if (a.Has("max_rate")) Autopilot.MaxRateDeg = a.Num("max_rate");
            if (a.Has("tc")) Autopilot.Tc = a.Num("tc");
            if (a.Has("rate_gain")) Autopilot.RateGain = a.Num("rate_gain");
            if (a.Has("sign_pitch")) Autopilot.SignPitch = a.Num("sign_pitch");
            if (a.Has("sign_roll")) Autopilot.SignRoll = a.Num("sign_roll");
            if (a.Has("sign_yaw")) Autopilot.SignYaw = a.Num("sign_yaw");
            if (mode == "off")
            {
                Autopilot.AttMode = "off";
                Autopilot.Exec.Active = false;
                Autopilot.Land.Active = false;
                return "autopilot off";
            }
            if (a.Has("pitch")) Autopilot.Pitch = a.Num("pitch");
            if (a.Has("heading")) Autopilot.Heading = a.Num("heading");
            Autopilot.Roll = a.Has("roll") ? a.Num("roll") : double.NaN;
            if (mode == "vector")
            {
                var l = a.List("vector") ?? throw new HarnessException("vector mode needs vector:[north,east,up]");
                double n = Convert.ToDouble(l[0]), e = Convert.ToDouble(l[1]), u = Convert.ToDouble(l[2]);
                Vector3d up = Autopilot.Up(v);
                Autopilot.CustomDir = (Vector3d.Exclude(up, v.north).normalized * n + Vector3d.Exclude(up, v.east).normalized * e + up * u).normalized;
            }
            Autopilot.AttMode = mode;
            return ApStatus(null);
        }

        [Cmd("ap_status", "Harness autopilot / node executor status and controller diagnostics.")]
        public static object ApStatus(Args a)
        {
            var d = new Dictionary<string, object>
            {
                ["mode"] = Autopilot.AttMode,
                ["driver"] = Autopilot.Driver,
                ["error_deg"] = R(Autopilot.LastErrorDeg, 2),
                ["throttle_cmd"] = Autopilot.ThrottleCmd,
                ["autostage"] = Autopilot.AutoStage,
                ["autostage_stop"] = Autopilot.AutoStageStop,
                ["node_exec"] = Autopilot.Exec.Active ? Autopilot.Exec.Status : "inactive (" + Autopilot.Exec.Status + ")",
                ["raw"] = Autopilot.Raw.ToDictionary(kv => kv.Key, kv => (object)kv.Value),
            };
            if (Autopilot.AttMode == "pitch_heading") { d["pitch"] = Autopilot.Pitch; d["heading"] = Autopilot.Heading; }
            if (!double.IsNaN(Autopilot.Roll)) { d["roll"] = Autopilot.Roll; d["roll_error_deg"] = R(Autopilot.LastRollErrorDeg, 2); }
            if (a != null && a.Bool("debug"))
            {
                d["input"] = Autopilot.LastInput;
                d["omega"] = Autopilot.LastOmega;
                d["alpha"] = Autopilot.LastAlpha;
                if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null)
                {
                    d["torque"] = Autopilot.AvailableTorque(FlightGlobals.ActiveVessel);
                    d["moi"] = FlightGlobals.ActiveVessel.MOI;
                }
            }
            if (Autopilot.Land.Active || Autopilot.Land.Status != "idle") d["land"] = Autopilot.Land.Status;
            if (Autopilot.Exec.Active)
            {
                d["burn_time"] = R(Autopilot.Exec.BurnTime, 1);
                d["dv_remaining"] = R(Autopilot.Exec.DvRemaining, 2);
                d["burn_start_in"] = R(Autopilot.Exec.StartUT - Planetarium.GetUniversalTime(), 1);
            }
            return d;
        }

        [Cmd("autothrottle", "Hold throttle via the autopilot (overrides player throttle): {value} or {off:true}")]
        static object AutoThrottle(Args a)
        {
            RequireVessel();
            if (a.Bool("off")) { Autopilot.ThrottleCmd = null; return "off"; }
            Autopilot.ThrottleCmd = a.ReqNum("value");
            return Autopilot.ThrottleCmd;
        }

        [Cmd("autostage", "Automatic staging when engines flame out: {on=true, stop_at?=1 (lowest stage number to activate)}")]
        static object AutoStage(Args a)
        {
            Autopilot.AutoStage = a.Bool("on", true);
            if (a.Has("stop_at")) Autopilot.AutoStageStop = (int)a.Num("stop_at");
            return new Dictionary<string, object> { ["on"] = Autopilot.AutoStage, ["stop_at"] = Autopilot.AutoStageStop };
        }

        [Cmd("controls", "Raw control overrides held every tick: {pitch,yaw,roll,x,y,z,wheel_steer,wheel_throttle: -1..1}. {clear:true} releases all.")]
        static object Controls(Args a)
        {
            RequireVessel();
            if (a.Bool("clear")) Autopilot.Raw.Clear();
            foreach (var k in new[] { "pitch", "yaw", "roll", "x", "y", "z", "wheel_steer", "wheel_throttle" })
                if (a.Has(k)) Autopilot.Raw[k] = Mathf.Clamp((float)a.Num(k), -1, 1);
            return Autopilot.Raw.ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        }

        [Cmd("node_exec", "Execute the next maneuver node with the harness autopilot: {tolerance=0.1, warp=true, remove=true, lead=20}")]
        static object NodeExec(Args a)
        {
            var v = RequireVessel();
            if (Autopilot.FirstNode(v) == null) throw new HarnessException("no maneuver node");
            Autopilot.StartExec(a.Num("tolerance", 0.1), a.Bool("warp", true), a.Bool("remove", true), a.Num("lead", 20));
            return ApStatus(null);
        }

        // ------------------------------------------------------------------ parts

        public static Part FindPart(Vessel v, string key)
        {
            if (uint.TryParse(key, out uint pid))
            {
                var p = v.parts.FirstOrDefault(x => x.persistentId == pid || x.flightID == pid);
                if (p != null) return p;
            }
            return v.parts.FirstOrDefault(x => x.name.Equals(key, StringComparison.OrdinalIgnoreCase))
                ?? v.parts.FirstOrDefault(x => x.partInfo.title.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? v.parts.FirstOrDefault(x => x.name.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0)
                ?? throw new HarnessException("no part matching '" + key + "'");
        }

        static object FieldValue(BaseField f, object host)
        {
            try
            {
                var val = f.GetValue(host);
                if (val is float || val is double || val is int || val is bool || val is string) return val;
                return val?.ToString();
            }
            catch { return null; }
        }

        [Cmd("parts", "List parts with their modules' events/actions/fields: {filter?: substring of part/module name, modules?=true}")]
        static object Parts(Args a)
        {
            var v = RequireVessel();
            var filter = a.Str("filter");
            bool withModules = a.Bool("modules", true);
            var res = new List<object>();
            foreach (var p in v.parts)
            {
                bool partMatch = filter == null || p.name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || p.partInfo.title.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
                var mods = new List<object>();
                if (withModules)
                    foreach (PartModule m in p.Modules)
                    {
                        if (!partMatch && m.moduleName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                        var events = m.Events.Where(e => e.active && e.guiActive).Select(e => e.guiName + " [" + e.name + "]").ToList();
                        var actions = m.Actions.Select(x => x.guiName + " [" + x.name + "]").ToList();
                        var fields = new Dictionary<string, object>();
                        foreach (BaseField f in m.Fields)
                            if (f.guiActive) fields[f.name] = FieldValue(f, m);
                        if (events.Count == 0 && actions.Count == 0 && fields.Count == 0) continue;
                        mods.Add(new Dictionary<string, object> { ["module"] = m.moduleName, ["events"] = events, ["actions"] = actions, ["fields"] = fields });
                    }
                if (!partMatch && mods.Count == 0) continue;
                res.Add(new Dictionary<string, object>
                {
                    ["id"] = p.persistentId,
                    ["name"] = p.name,
                    ["title"] = p.partInfo.title,
                    ["stage"] = p.inverseStage,
                    ["resources"] = p.Resources.Cast<PartResource>().ToDictionary(r => r.resourceName, r => (object)R(r.amount, 2)),
                    ["temp_ratio"] = R(Math.Max(p.temperature / p.maxTemp, p.skinTemperature / p.skinMaxTemp), 3),
                    ["modules"] = mods,
                });
            }
            return res;
        }

        static IEnumerable<PartModule> ModulesOf(Part p, string module) =>
            p.Modules.Cast<PartModule>().Where(m => module == null || m.moduleName.Equals(module, StringComparison.OrdinalIgnoreCase));

        [Cmd("part_event", "Trigger a part's right-click event: {part (id/name/title), event (name or gui name), module?, all?:bool (every matching part)}")]
        static object PartEvent(Args a)
        {
            var v = RequireVessel();
            var evName = a.ReqStr("event");
            var module = a.Str("module");
            var targets = a.Bool("all")
                ? v.parts.Where(p => p.name.IndexOf(a.ReqStr("part"), StringComparison.OrdinalIgnoreCase) >= 0 || p.partInfo.title.IndexOf(a.ReqStr("part"), StringComparison.OrdinalIgnoreCase) >= 0).ToList()
                : new List<Part> { FindPart(v, a.ReqStr("part")) };
            var fired = new List<object>();
            foreach (var p in targets)
                foreach (var m in ModulesOf(p, module))
                {
                    var e = m.Events.FirstOrDefault(x => x.active && (x.name.Equals(evName, StringComparison.OrdinalIgnoreCase) || x.guiName.Equals(evName, StringComparison.OrdinalIgnoreCase)));
                    if (e == null) continue;
                    e.Invoke();
                    fired.Add(p.partInfo.title + "/" + m.moduleName + "/" + e.name);
                    break;
                }
            if (fired.Count == 0) throw new HarnessException("no active event '" + evName + "' found");
            return fired;
        }

        [Cmd("part_action", "Invoke a part action: {part, action (name or gui name), module?, state?=true}")]
        static object PartAction(Args a)
        {
            var v = RequireVessel();
            var p = FindPart(v, a.ReqStr("part"));
            var name = a.ReqStr("action");
            foreach (var m in ModulesOf(p, a.Str("module")))
            {
                var act = m.Actions.FirstOrDefault(x => x.name.Equals(name, StringComparison.OrdinalIgnoreCase) || x.guiName.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (act == null) continue;
                act.Invoke(new KSPActionParam(KSPActionGroup.None, a.Bool("state", true) ? KSPActionType.Activate : KSPActionType.Deactivate));
                return p.partInfo.title + "/" + m.moduleName + "/" + act.name;
            }
            throw new HarnessException("no action '" + name + "'");
        }

        [Cmd("part_field", "Set a part module field (e.g. thrustPercentage): {part, module, field, value}")]
        static object PartField(Args a)
        {
            var v = RequireVessel();
            var p = FindPart(v, a.ReqStr("part"));
            var fname = a.ReqStr("field");
            foreach (var m in ModulesOf(p, a.Str("module")))
            {
                var f = m.Fields[fname];
                if (f == null) continue;
                var ft = f.FieldInfo.FieldType;
                object val = a.Raw("value");
                if (ft == typeof(float)) val = (float)a.Num("value");
                else if (ft == typeof(double)) val = a.Num("value");
                else if (ft == typeof(int)) val = (int)a.Num("value");
                else if (ft == typeof(bool)) val = a.Bool("value");
                else val = a.Str("value");
                f.SetValue(val, m);
                // propagate to symmetry counterparts like the stock UI does
                foreach (var sp in p.symmetryCounterparts)
                    foreach (var sm in ModulesOf(sp, m.moduleName)) sm.Fields[fname]?.SetValue(val, sm);
                return FieldValue(f, m);
            }
            throw new HarnessException("no field '" + fname + "'");
        }

        [Cmd("parachutes", "Deploy (arm) all parachutes: {}")]
        static object Parachutes(Args a)
        {
            var v = RequireVessel();
            int n = 0;
            foreach (var c in v.FindPartModulesImplementing<ModuleParachute>())
            {
                if (c.deploymentState == ModuleParachute.deploymentStates.STOWED) { c.Deploy(); n++; }
            }
            return n + " parachutes armed";
        }
    }
}
