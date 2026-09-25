using System;
using System.Collections.Generic;
using System.Linq;
using KSP.UI.Screens;
using UnityEngine;

namespace KSPHarness
{
    /// <summary>
    /// Closed-loop control that runs inside the game every physics tick: attitude hold,
    /// maneuver-node execution, autostaging, and raw control overrides.
    /// </summary>
    public static class Autopilot
    {
        // ---- attitude target ----
        public static string AttMode = "off";
        public static double Pitch, Heading;
        public static double Roll = double.NaN;      // NaN => don't control roll (just damp it)
        public static Vector3d CustomDir;             // for mode "vector" (world frame, set at command time)
        public static string Driver = "custom";      // "custom" (our PD controller) or "sas" (stock SAS steering)
        public static double MaxRateDeg = 20;         // max commanded turn rate, deg/s
        public static double Tc = 0.25;               // controller time constant, s
        public static double RateGain = 1.5;          // 1/s, linear region of angle->rate law
        public static double SignPitch = -1, SignRoll = -1, SignYaw = -1;

        // ---- throttle override (null => leave the player's throttle alone) ----
        public static double? ThrottleCmd;

        // ---- autostage ----
        public static bool AutoStage;
        public static int AutoStageStop = 1;          // never activate stages numbered below this
        static float nextStageTime;

        // ---- raw input overrides ----
        public static readonly Dictionary<string, float> Raw = new Dictionary<string, float>();

        // ---- node executor ----
        public class NodeExecState
        {
            public bool Active;
            public double Tolerance = 0.1;
            public bool Warp = true;
            public bool RemoveWhenDone = true;
            public double LeadTime = 20;
            public string Status = "idle";
            public Vector3d InitialBurn;
            public Vector3d LastDir;
            public bool Warped;
            public bool Burning;
            public double BurnTime, HalfBurnTime, DvRemaining, StartUT;
            public double NoThrustSince = -1;
            public float WarpRequestedAt;
        }
        public static readonly NodeExecState Exec = new NodeExecState();

        // ---- diagnostics ----
        public static double LastErrorDeg = double.NaN, LastRollErrorDeg = double.NaN;
        public static Vector3 LastInput, LastOmega, LastAlpha;
        static Vector3 torqueCache;
        static float torqueCacheTime = -1;

        static Vessel hooked;

        public static bool Engaged => AttMode != "off" || ThrottleCmd.HasValue || Exec.Active || Land.Active || Raw.Count > 0;

        public static void OnVesselChange(Vessel v)
        {
            Hook(v);
        }

        static void Hook(Vessel v)
        {
            if (hooked == v) return;
            if (hooked != null) hooked.OnFlyByWire -= FlyByWire;
            hooked = v;
            if (v != null)
            {
                v.OnFlyByWire -= FlyByWire;
                v.OnFlyByWire += FlyByWire;
            }
        }

        public static void Reset()
        {
            AttMode = "off";
            ThrottleCmd = null;
            Exec.Active = false;
            Exec.Status = "idle";
            Land.Active = false;
            Raw.Clear();
        }

        /// <summary>Called every frame from Harness.Update.</summary>
        public static void Update()
        {
            DialogWatch();
            if (!HighLogic.LoadedSceneIsFlight) { if (hooked != null) Hook(null); return; }
            var v = FlightGlobals.ActiveVessel;
            if (v != hooked) Hook(v);
            if (v == null) return;
            if (AutoStage) TryAutoStage(v);
            Watchdog(v);
        }

        // ------------------------------------------------------------------ watchdog

        static float nextWatchdog, noThrustSince = -1;
        static bool noThrustAlerted;
        static readonly HashSet<uint> flamedOut = new HashSet<uint>();
        static readonly HashSet<string> knownDialogs = new HashSet<string>();

        /// <summary>1 Hz anomaly detector: turns silent failure states into alert.* events.</summary>
        static void Watchdog(Vessel v)
        {
            if (Time.realtimeSinceStartup < nextWatchdog) return;
            nextWatchdog = Time.realtimeSinceStartup + 1f;
            try
            {
                if (!v.packed)
                {
                    // throttle requested but nothing is producing thrust
                    bool wantThrust = v.ctrlState.mainThrottle > 0.01f;
                    bool haveThrust = CurrentPropulsion(v).Thrust > 0;
                    if (wantThrust && !haveThrust)
                    {
                        if (noThrustSince < 0) noThrustSince = Time.time;
                        if (!noThrustAlerted && Time.time - noThrustSince > 3)
                        {
                            noThrustAlerted = true;
                            EventLog.Add("alert.no_thrust", "throttle " + v.ctrlState.mainThrottle.ToString("F2") + " but no engine is producing thrust (stage " + v.currentStage + (AutoStage ? ", autostage on, stop_at " + AutoStageStop : ", autostage off") + ")");
                        }
                    }
                    else { noThrustSince = -1; noThrustAlerted = false; }

                    foreach (var e in v.FindPartModulesImplementing<ModuleEngines>())
                    {
                        if (e.EngineIgnited && e.flameout)
                        {
                            if (flamedOut.Add(e.part.flightID))
                                EventLog.Add("alert.flameout", e.part.partInfo.title + " flamed out" + (string.IsNullOrEmpty(e.statusL2) ? "" : " (" + e.statusL2 + ")"));
                        }
                        else flamedOut.Remove(e.part.flightID);
                    }
                }

            }
            catch (Exception e) { EventLog.Add("harness.error", "watchdog: " + e.Message); }
        }

        static float nextDialogWatch;

        /// <summary>1 Hz, all scenes: report newly opened dialogs/windows as alert.dialog.</summary>
        static void DialogWatch()
        {
            if (Time.realtimeSinceStartup < nextDialogWatch) return;
            nextDialogWatch = Time.realtimeSinceStartup + 1f;
            try
            {
                var titles = CmdGame.OpenDialogTitles();
                foreach (var t in titles)
                    if (knownDialogs.Add(t)) EventLog.Add("alert.dialog", "window opened: " + t);
                knownDialogs.IntersectWith(titles);
            }
            catch (Exception e) { EventLog.Add("harness.error", "dialog watch: " + e.Message); }
        }

        static void TryAutoStage(Vessel v)
        {
            if (v.packed || !v.IsControllable) return;
            if (Time.time < nextStageTime) return;
            if (!StageManager.CanSeparate) return;
            if (v.currentStage - 1 < AutoStageStop) return;
            // ctrlState is the throttle actually applied after fly-by-wire (node executor / landing set it
            // there, not in FlightInputHandler.state).
            float thr = Math.Max(v.ctrlState.mainThrottle, FlightInputHandler.state.mainThrottle);
            if (ThrottleCmd.HasValue) thr = Math.Max(thr, (float)ThrottleCmd.Value);
            if (thr <= 0 && !(Exec.Active && Exec.Burning) && !(Land.Active && Land.Burning)) return;

            bool anyRunning = false, anyFlameout = false;
            foreach (var e in v.FindPartModulesImplementing<ModuleEngines>())
            {
                if (!e.EngineIgnited) continue;
                if (e.flameout || !e.isOperational) anyFlameout = true;
                else anyRunning = true;
            }
            if (anyFlameout || !anyRunning)
            {
                EventLog.Add("autostage", "autostaging from stage " + v.currentStage + (anyFlameout ? " (flameout)" : " (no running engines)"));
                StageManager.ActivateNextStage();
                nextStageTime = Time.time + 1.0f;
            }
        }

        // ------------------------------------------------------------------ geometry

        public static Vector3d Up(Vessel v) => (v.CoMD - v.mainBody.position).normalized;

        /// <summary>Orbit normal (angular momentum direction, "Normal+" on the navball) in world space.</summary>
        public static Vector3d OrbitNormal(Vessel v) => -Vector3d.Cross(v.CoMD - v.mainBody.position, v.obt_velocity).normalized;

        public static Vector3d RadialOut(Vessel v)
        {
            var p = v.obt_velocity.normalized;
            var r = v.CoMD - v.mainBody.position;
            return (r - p * Vector3d.Dot(r, p)).normalized;
        }

        public static Vector3d PitchHeadingDir(Vessel v, double pitchDeg, double headingDeg)
        {
            double p = pitchDeg * Math.PI / 180, h = headingDeg * Math.PI / 180;
            Vector3d up = Up(v);
            Vector3d north = Vector3d.Exclude(up, v.north).normalized;
            Vector3d east = Vector3d.Exclude(up, v.east).normalized;
            return (up * Math.Sin(p) + (north * Math.Cos(h) + east * Math.Sin(h)) * Math.Cos(p)).normalized;
        }

        /// <summary>Resolve the current attitude target; returns false if there is none.</summary>
        public static bool TargetDirection(Vessel v, out Vector3d fwd, out Vector3d top)
        {
            top = Vector3d.zero;
            fwd = Vector3d.zero;
            string m = Exec.Active ? "node" : AttMode;
            switch (m)
            {
                case "off": return false;
                case "prograde": fwd = v.obt_velocity; break;
                case "retrograde": fwd = -v.obt_velocity; break;
                case "srf_prograde": fwd = v.srf_velocity; break;
                case "srf_retrograde": fwd = -v.srf_velocity; break;
                case "normal": fwd = OrbitNormal(v); break;
                case "antinormal": fwd = -OrbitNormal(v); break;
                case "radial_out": fwd = RadialOut(v); break;
                case "radial_in": fwd = -RadialOut(v); break;
                case "up": fwd = Up(v); break;
                case "down": fwd = -Up(v); break;
                case "vector": fwd = CustomDir; break;
                case "land": fwd = Land.Dir; break;
                case "pitch_heading": fwd = PitchHeadingDir(v, Pitch, Heading); break;
                case "target":
                case "anti_target":
                    if (v.targetObject == null) return false;
                    fwd = v.targetObject.GetTransform().position - v.CoMD;
                    if (m == "anti_target") fwd = -fwd;
                    break;
                case "target_prograde":
                case "target_retrograde":
                    if (v.targetObject == null) return false;
                    fwd = v.obt_velocity - v.targetObject.GetObtVelocity();
                    if (m == "target_retrograde") fwd = -fwd;
                    break;
                case "node":
                    {
                        var node = FirstNode(v);
                        if (node == null) return false;
                        var bv = node.GetBurnVector(v.orbit);
                        // Near the end of a burn the residual vector swings wildly; hold the last good heading.
                        if (bv.magnitude > 1.0 || Exec.LastDir == Vector3d.zero) Exec.LastDir = bv.normalized;
                        fwd = Exec.LastDir;
                        break;
                    }
                default: return false;
            }
            if (fwd.sqrMagnitude < 1e-9) return false;
            fwd = fwd.normalized;
            if (!double.IsNaN(Roll))
            {
                // Roll 0 = vessel's top (-ReferenceTransform.forward) pointing away from the planet.
                Vector3d up = Up(v);
                Vector3d t0 = up - fwd * Vector3d.Dot(up, fwd);
                if (t0.sqrMagnitude < 1e-4) t0 = -(Vector3d)PitchHeadingDir(v, 0, Heading);
                t0 = t0.normalized;
                top = (Vector3d)(Quaternion.AngleAxis((float)Roll, (Vector3)fwd) * (Vector3)t0);
            }
            return true;
        }

        public static ManeuverNode FirstNode(Vessel v)
        {
            if (v.patchedConicSolver == null || v.patchedConicSolver.maneuverNodes.Count == 0) return null;
            return v.patchedConicSolver.maneuverNodes[0];
        }

        /// <summary>Available torque (kN*m) about the vessel's local (pitch, roll, yaw) axes.</summary>
        public static Vector3 AvailableTorque(Vessel v)
        {
            if (Time.time - torqueCacheTime < 0.5f && torqueCacheTime >= 0) return torqueCache;
            Vector3 sum = Vector3.zero;
            foreach (var p in v.parts)
                foreach (var pm in p.Modules)
                {
                    if (!(pm is ITorqueProvider tp)) continue;
                    if (!pm.isEnabled) continue;
                    try
                    {
                        tp.GetPotentialTorque(out Vector3 pos, out Vector3 neg);
                        sum += new Vector3((Math.Abs(pos.x) + Math.Abs(neg.x)) / 2, (Math.Abs(pos.y) + Math.Abs(neg.y)) / 2, (Math.Abs(pos.z) + Math.Abs(neg.z)) / 2);
                    }
                    catch { }
                }
            torqueCache = sum;
            torqueCacheTime = Time.time;
            return sum;
        }

        // ------------------------------------------------------------------ control loop

        static void FlyByWire(FlightCtrlState s)
        {
            var v = hooked;
            if (v == null || v.packed) return;
            try
            {
                if (Exec.Active) RunNodeExec(v, s);
                else if (Land.Active) RunLand(v, s);

                if (TargetDirection(v, out var fwd, out var top))
                {
                    if (Driver == "sas") DriveSas(v, fwd);
                    else DriveCustom(v, s, fwd, top);
                }

                if (ThrottleCmd.HasValue) s.mainThrottle = Mathf.Clamp01((float)ThrottleCmd.Value);

                foreach (var kv in Raw)
                {
                    switch (kv.Key)
                    {
                        case "pitch": s.pitch = kv.Value; break;
                        case "yaw": s.yaw = kv.Value; break;
                        case "roll": s.roll = kv.Value; break;
                        case "x": s.X = kv.Value; break;
                        case "y": s.Y = kv.Value; break;
                        case "z": s.Z = kv.Value; break;
                        case "wheel_steer": s.wheelSteer = kv.Value; break;
                        case "wheel_throttle": s.wheelThrottle = kv.Value; break;
                    }
                }
            }
            catch (Exception e)
            {
                EventLog.Add("autopilot.error", e.Message);
                Reset();
            }
        }

        static void DriveSas(Vessel v, Vector3d fwd)
        {
            if (!v.ActionGroups[KSPActionGroup.SAS]) v.ActionGroups.SetGroup(KSPActionGroup.SAS, true);
            if (v.Autopilot.Mode != VesselAutopilot.AutopilotMode.StabilityAssist)
                v.Autopilot.SetMode(VesselAutopilot.AutopilotMode.StabilityAssist);
            v.Autopilot.SAS.SetTargetOrientation(fwd, false);
            LastErrorDeg = Vector3d.Angle(v.ReferenceTransform.up, fwd);
        }

        static void DriveCustom(Vessel v, FlightCtrlState s, Vector3d tgtFwd, Vector3d tgtTop)
        {
            if (v.ActionGroups[KSPActionGroup.SAS]) v.ActionGroups.SetGroup(KSPActionGroup.SAS, false);

            Transform t = v.ReferenceTransform;
            Vector3d fwd = t.up;
            Vector3d curTop = -t.forward;
            Quaternion inv = Quaternion.Inverse(t.rotation);
            Vector3 omegaWorld = v.rootPart.rb != null ? v.rootPart.rb.angularVelocity : Vector3.zero;
            Vector3 omega = inv * omegaWorld; // local: x=pitch axis, y=roll axis, z=yaw axis

            Vector3 torque = AvailableTorque(v);
            Vector3 moi = v.MOI;
            Vector3 alpha = new Vector3(
                moi.x > 1e-6f ? torque.x / moi.x : 0,
                moi.y > 1e-6f ? torque.y / moi.y : 0,
                moi.z > 1e-6f ? torque.z / moi.z : 0);
            LastAlpha = alpha;

            double maxRate = MaxRateDeg * Math.PI / 180;
            double angle = Vector3d.Angle(fwd, tgtFwd) * Math.PI / 180;
            LastErrorDeg = angle * 180 / Math.PI;
            Vector3d axis = Vector3d.Cross(fwd, tgtFwd);
            if (axis.sqrMagnitude < 1e-12)
                axis = angle > 1 ? (Vector3d)t.right : Vector3d.zero; // 180° off: pick any perpendicular axis
            axis = axis.normalized;

            double aPoint = Math.Max(1e-3, Math.Min(alpha.x, alpha.z));
            double rate = Math.Min(maxRate, Math.Min(Math.Sqrt(aPoint * angle), RateGain * angle));
            Vector3d wDes = axis * rate;

            if (tgtTop != Vector3d.zero && angle < 20 * Math.PI / 180)
            {
                Vector3d tp = (tgtTop - fwd * Vector3d.Dot(tgtTop, fwd)).normalized;
                double rollErr = Vector3d.Angle(curTop, tp) * Math.PI / 180;
                double sign = Math.Sign(Vector3d.Dot(Vector3d.Cross(curTop, tp), fwd));
                LastRollErrorDeg = rollErr * sign * 180 / Math.PI;
                double aRoll = Math.Max(1e-3, alpha.y);
                double rr = Math.Min(maxRate, Math.Min(Math.Sqrt(aRoll * rollErr), RateGain * rollErr));
                wDes += fwd * (rr * sign);
            }
            else LastRollErrorDeg = double.NaN;

            Vector3 wDesLocal = inv * (Vector3)wDes;
            Vector3 err = wDesLocal - omega;
            Vector3 u = new Vector3(
                Mathf.Clamp((float)(err.x / (Math.Max(alpha.x, 1e-3) * Tc)), -1, 1),
                Mathf.Clamp((float)(err.y / (Math.Max(alpha.y, 1e-3) * Tc)), -1, 1),
                Mathf.Clamp((float)(err.z / (Math.Max(alpha.z, 1e-3) * Tc)), -1, 1));
            LastInput = u;
            LastOmega = omega;

            s.pitch = (float)(SignPitch * u.x);
            s.roll = (float)(SignRoll * u.y);
            s.yaw = (float)(SignYaw * u.z);
        }

        // ------------------------------------------------------------------ node executor

        public struct Propulsion { public double Thrust, Isp, Mass, Accel; }

        /// <summary>Max thrust (kN) and effective Isp of currently ignited engines.</summary>
        public static Propulsion CurrentPropulsion(Vessel v)
        {
            double thrust = 0, flow = 0;
            double atm = v.staticPressurekPa * PhysicsGlobals.KpaToAtmospheres;
            foreach (var e in v.FindPartModulesImplementing<ModuleEngines>())
            {
                if (!e.EngineIgnited || !e.isOperational || e.flameout) continue;
                double isp = e.atmosphereCurve.Evaluate((float)atm);
                double f = e.maxThrust * e.thrustPercentage / 100.0;
                if (isp <= 0 || f <= 0) continue;
                thrust += f;
                flow += f / (isp * 9.80665);
            }
            var m = v.totalMass;
            return new Propulsion
            {
                Thrust = thrust,
                Isp = flow > 0 ? thrust / flow / 9.80665 : 0,
                Mass = m,
                Accel = m > 0 ? thrust / m : 0,
            };
        }

        public static double BurnTime(Propulsion p, double dv)
        {
            if (p.Thrust <= 0) return double.PositiveInfinity;
            double ve = p.Isp * 9.80665;
            return p.Mass * ve / p.Thrust * (1 - Math.Exp(-dv / ve));
        }

        static void RunNodeExec(Vessel v, FlightCtrlState s)
        {
            var node = FirstNode(v);
            if (node == null)
            {
                FinishExec(v, s, "done (no node)");
                return;
            }
            var bv = node.GetBurnVector(v.orbit);
            double dv = bv.magnitude;
            Exec.DvRemaining = dv;
            if (Exec.InitialBurn == Vector3d.zero) Exec.InitialBurn = bv;

            var prop = CurrentPropulsion(v);
            double ut = Planetarium.GetUniversalTime();
            if (!Exec.Burning)
            {
                Exec.BurnTime = BurnTime(prop, dv);
                Exec.HalfBurnTime = BurnTime(prop, dv / 2);
                Exec.StartUT = node.UT - Exec.HalfBurnTime;
            }

            if (Exec.Burning && (dv < Exec.Tolerance || Vector3d.Dot(bv, Exec.InitialBurn) < 0))
            {
                FinishExec(v, s, "done");
                if (Exec.RemoveWhenDone) node.RemoveSelf();
                return;
            }

            bool aligned = LastErrorDeg < 3 && v.angularVelocity.magnitude < 0.05;

            if (ut < Exec.StartUT)
            {
                s.mainThrottle = 0;
                if (prop.Thrust <= 0)
                {
                    Exec.Status = "waiting (no active engines; stage or enable autostage)";
                }
                else Exec.Status = "aligning/waiting, burn in " + (Exec.StartUT - ut).ToString("F0") + "s";
                if (Exec.Warp && aligned && !Exec.Warped && Exec.StartUT - ut > Exec.LeadTime + 10)
                {
                    TimeWarp.fetch.WarpTo(Exec.StartUT - Exec.LeadTime);
                    Exec.Warped = true;
                    Exec.WarpRequestedAt = Time.realtimeSinceStartup;
                }
                if (Exec.Warped && TimeWarp.CurrentRateIndex == 0 && Time.realtimeSinceStartup - Exec.WarpRequestedAt > 3)
                    Exec.Status += " (time warp refused by game, e.g. too close to the surface; waiting at 1x)";
                return;
            }

            if (TimeWarp.CurrentRateIndex > 0) TimeWarp.SetRate(0, true, false);

            // Burning far off schedule changes the trajectory (a late transfer burn can become an escape),
            // so refuse rather than blindly finishing the node.
            if (!Exec.Burning && ut > Exec.StartUT + Math.Max(30, Exec.BurnTime))
            {
                FinishExec(v, s, "aborted: missed burn window by " + (ut - Exec.StartUT).ToString("F0") + "s; replan the node");
                return;
            }

            if (prop.Thrust <= 0)
            {
                if (Exec.NoThrustSince < 0) Exec.NoThrustSince = ut;
                if (ut - Exec.NoThrustSince > 10)
                {
                    FinishExec(v, s, "aborted: no thrust for 10s with " + dv.ToString("F1") + " m/s left (out of fuel or staging needed); replan the node");
                    return;
                }
                s.mainThrottle = 0;
                Exec.Status = "no thrust";
                // autostage (if enabled) will kick in once throttle is requested
                if (AutoStage) s.mainThrottle = 1;
                return;
            }
            Exec.NoThrustSince = -1;

            if (LastErrorDeg > (Exec.Burning ? 15 : 5))
            {
                s.mainThrottle = 0;
                Exec.Status = "realigning (error " + LastErrorDeg.ToString("F1") + " deg)";
                return;
            }

            Exec.Burning = true;
            double thr = dv / Math.Max(prop.Accel, 1e-6) / 1.0; // ramp down over the last ~second of burn
            s.mainThrottle = Mathf.Clamp((float)thr, 0.02f, 1f);
            ThrottleCmd = null;
            Exec.Status = "burning, dv remaining " + dv.ToString("F1");
        }

        static void FinishExec(Vessel v, FlightCtrlState s, string status)
        {
            s.mainThrottle = 0;
            FlightInputHandler.state.mainThrottle = 0;
            Exec.Active = false;
            Exec.Status = status;
            Exec.LastDir = Vector3d.zero;
            EventLog.Add(status.StartsWith("aborted") ? "alert.node_exec" : "node.exec", status + ", residual dv " + Exec.DvRemaining.ToString("F2"));
            AttMode = "off";
        }

        // ------------------------------------------------------------------ landing

        public class LandState
        {
            public bool Active;
            public double TouchSpeed = 1.5;     // m/s at touchdown
            public double Margin = 0.6;         // plan decelerations at this fraction of max
            public double HeightOffset;         // CoM height above the lowest point of the vessel
            public bool Gear = true;
            public string Status = "idle";
            public Vector3d Dir;
            public bool Burning;
            public float LandedTime = -1;
        }
        public static readonly LandState Land = new LandState();

        public static void StartLand(Vessel v, double touch, double margin, bool gear, double offset)
        {
            Land.Active = true;
            Land.TouchSpeed = touch;
            Land.Margin = margin;
            Land.Gear = gear;
            Land.Burning = false;
            Land.LandedTime = -1;
            if (double.IsNaN(offset))
            {
                Vector3 down = -v.ReferenceTransform.up;
                double lowest = 0;
                foreach (var p in v.parts)
                {
                    double d = Vector3.Dot(p.transform.position - v.CoM, down);
                    if (d > lowest) lowest = d;
                }
                offset = lowest + 1.0;
            }
            Land.HeightOffset = offset;
            Land.Status = "starting";
            Exec.Active = false;
            ThrottleCmd = null;
            AttMode = "land";
        }

        static void RunLand(Vessel v, FlightCtrlState s)
        {
            var body = v.mainBody;
            double r = (v.CoMD - body.position).magnitude;
            double g = body.gravParameter / (r * r);
            Vector3d up = Up(v);
            double h = Math.Max(0.1, Math.Min(v.radarAltitude, v.altitude) - Land.HeightOffset);
            if (body.ocean && v.altitude < v.radarAltitude) h = Math.Max(0.1, v.altitude - Land.HeightOffset);
            Vector3d vel = v.srf_velocity;
            double speed = vel.magnitude;
            double vs = Vector3d.Dot(vel, up);
            Vector3d horiz = vel - up * vs;

            if (v.LandedOrSplashed && speed < 2)
            {
                if (Land.LandedTime < 0) Land.LandedTime = Time.time;
                s.mainThrottle = 0;
                Land.Dir = up;
                Land.Status = "landed";
                if (Time.time - Land.LandedTime > 3)
                {
                    Land.Active = false;
                    AttMode = "off";
                    FlightInputHandler.state.mainThrottle = 0;
                    EventLog.Add("land", "landed on " + body.bodyName + " at " + v.latitude.ToString("F3") + ", " + v.longitude.ToString("F3"));
                }
                return;
            }
            Land.LandedTime = -1;

            if (Land.Gear && h < 500 && !v.ActionGroups[KSPActionGroup.Gear]) v.ActionGroups.SetGroup(KSPActionGroup.Gear, true);

            // Attitude: surface retrograde while moving fast; near the end, stand up and null horizontal drift.
            if (speed > 8 && vs < 0) Land.Dir = -vel.normalized;
            else Land.Dir = (up - horiz * 0.15).normalized;
            if (Vector3d.Angle(Land.Dir, up) > 60 && speed <= 8) Land.Dir = up;

            var prop = CurrentPropulsion(v);
            if (prop.Thrust <= 0)
            {
                Land.Status = "no thrust!";
                s.mainThrottle = AutoStage ? 1 : 0;
                return;
            }
            double aMax = prop.Accel;
            double cosErr = Math.Max(0.1, Math.Cos(LastErrorDeg * Math.PI / 180));

            double vt = Land.TouchSpeed;
            double thr;
            if (vs >= 0 && h > 50)
            {
                thr = 0; // still climbing / hovering high: coast
                Land.Status = "coasting (ascending), h=" + h.ToString("F0");
            }
            else if (h < 4 || speed < vt + 1.5 && h < 30)
            {
                // terminal: hold a gentle descent rate
                double target = -Math.Min(vt, Math.Max(0.5, h * 0.5));
                thr = (g + 2.0 * (target - vs)) / (aMax * cosErr);
                Land.Burning = true;
                Land.Status = "touchdown, h=" + h.ToString("F1") + " vs=" + vs.ToString("F1");
            }
            else
            {
                // decel needed to reach touch speed at the ground, plus gravity along the flight path
                double aReq = (speed * speed - vt * vt) / (2 * h);
                double gAlong = g * Math.Max(0, -vs) / Math.Max(speed, 0.1);
                double need = (aReq + gAlong) / (aMax * cosErr);
                if (!Land.Burning && need < Land.Margin)
                {
                    thr = 0;
                    Land.Status = "falling, burn at " + (Land.Margin * 100).ToString("F0") + "% (now " + (need * 100).ToString("F0") + "%), h=" + h.ToString("F0");
                }
                else
                {
                    Land.Burning = true;
                    thr = need;
                    Land.Status = "braking, h=" + h.ToString("F0") + " speed=" + speed.ToString("F1");
                }
            }
            if (LastErrorDeg > 45 && !(h < 10)) thr = Math.Min(thr, 0.05);
            s.mainThrottle = Mathf.Clamp01((float)thr);
        }

        public static void StartExec(double tol, bool warp, bool remove, double lead)
        {
            Exec.Active = true;
            Exec.Tolerance = tol;
            Exec.Warp = warp;
            Exec.RemoveWhenDone = remove;
            Exec.LeadTime = lead;
            Exec.InitialBurn = Vector3d.zero;
            Exec.LastDir = Vector3d.zero;
            Exec.Warped = false;
            Exec.Burning = false;
            Exec.NoThrustSince = -1;
            Exec.Status = "starting";
            ThrottleCmd = null;
        }
    }
}
