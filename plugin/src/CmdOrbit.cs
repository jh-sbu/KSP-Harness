using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KSPHarness
{
    /// <summary>Maneuver nodes and maneuver planning. All vector math here is in KSP's
    /// orbit frame (Orbit.getRelativePositionAtUT etc.), not Unity world space.</summary>
    static class CmdOrbit
    {
        static double Now => Planetarium.GetUniversalTime();

        static PatchedConicSolver Solver(Vessel v)
        {
            if (v.patchedConicSolver == null)
            {
                v.AttachPatchedConicsSolver();
                if (v.patchedConicSolver == null) throw new HarnessException("no patched conic solver (upgrade tracking station / mission control?)");
            }
            return v.patchedConicSolver;
        }

        /// <summary>The trajectory patch that contains the given UT (following existing nodes).</summary>
        static Orbit PatchAt(Vessel v, double ut)
        {
            Orbit o = v.orbit;
            while (o.nextPatch != null && o.nextPatch.activePatch && ut > o.EndUT && o.patchEndTransition != Orbit.PatchTransitionType.FINAL)
                o = o.nextPatch;
            return o;
        }

        /// <summary>Convert an orbit-frame delta-v at UT into maneuver node coordinates (radial, normal, prograde).</summary>
        public static Vector3d ToNodeCoords(Orbit o, double ut, Vector3d dv)
        {
            Vector3d pos = o.getRelativePositionAtUT(ut);
            Vector3d vel = o.getOrbitalVelocityAtUT(ut);
            Vector3d pro = vel.normalized;
            Vector3d nrm = o.GetOrbitNormal().normalized;
            Vector3d rad = Vector3d.Exclude(pro, pos).normalized;
            return new Vector3d(Vector3d.Dot(dv, rad), Vector3d.Dot(dv, nrm), Vector3d.Dot(dv, pro));
        }

        static ManeuverNode AddNode(Vessel v, double ut, Vector3d nodeDv)
        {
            var solver = Solver(v);
            var node = solver.AddManeuverNode(ut);
            node.DeltaV = nodeDv;
            solver.UpdateFlightPlan();
            return node;
        }

        static void SetNode(Vessel v, ManeuverNode node, double ut, Vector3d nodeDv)
        {
            node.UT = ut;
            node.DeltaV = nodeDv;
            v.patchedConicSolver.UpdateFlightPlan();
        }

        static Dictionary<string, object> NodeInfo(Vessel v, ManeuverNode n, int idx)
        {
            var d = new Dictionary<string, object>
            {
                ["index"] = idx,
                ["ut"] = n.UT,
                ["in"] = Math.Round(n.UT - Now, 1),
                ["prograde"] = Math.Round(n.DeltaV.z, 3),
                ["normal"] = Math.Round(n.DeltaV.y, 3),
                ["radial"] = Math.Round(n.DeltaV.x, 3),
                ["dv"] = Math.Round(n.DeltaV.magnitude, 3),
            };
            var prop = Autopilot.CurrentPropulsion(v);
            d["burn_time_estimate"] = Math.Round(Autopilot.BurnTime(prop, n.DeltaV.magnitude), 1);
            if (n.nextPatch != null)
            {
                var after = CmdFlight.OrbitInfo(n.nextPatch);
                d["orbit_after"] = after;
                var enc = Encounter(n.nextPatch);
                if (enc != null) d["encounter"] = CmdFlight.OrbitInfo(enc);
            }
            return d;
        }

        /// <summary>First patch after `o` that is around a different body (i.e. an SOI encounter/escape), or null.</summary>
        static Orbit Encounter(Orbit o)
        {
            var start = o.referenceBody;
            int guard = 0;
            while (o != null && guard++ < 10)
            {
                if (o.referenceBody != start) return o;
                if (o.patchEndTransition == Orbit.PatchTransitionType.FINAL || o.nextPatch == null || !o.nextPatch.activePatch) return null;
                o = o.nextPatch;
            }
            return null;
        }

        [Cmd("nodes", "List maneuver nodes.")]
        static object Nodes(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var s = Solver(v);
            return s.maneuverNodes.Select((n, i) => NodeInfo(v, n, i)).ToList();
        }

        [Cmd("node_add", "Add a maneuver node: {ut | in (seconds from now), prograde=0, normal=0, radial=0}")]
        static object NodeAdd(Args a)
        {
            var v = CmdFlight.RequireVessel();
            double ut = a.Has("ut") ? a.Num("ut") : Now + a.ReqNum("in");
            var n = AddNode(v, ut, new Vector3d(a.Num("radial", 0), a.Num("normal", 0), a.Num("prograde", 0)));
            return NodeInfo(v, n, Solver(v).maneuverNodes.IndexOf(n));
        }

        [Cmd("node_update", "Modify a node: {index=0, ut?|in?, prograde?, normal?, radial?, add?:bool (increment instead of set)}")]
        static object NodeUpdate(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var s = Solver(v);
            int i = (int)a.Num("index", 0);
            if (i >= s.maneuverNodes.Count) throw new HarnessException("no node " + i);
            var n = s.maneuverNodes[i];
            bool add = a.Bool("add");
            var dv = n.DeltaV;
            if (a.Has("radial")) dv.x = (add ? dv.x : 0) + a.Num("radial");
            if (a.Has("normal")) dv.y = (add ? dv.y : 0) + a.Num("normal");
            if (a.Has("prograde")) dv.z = (add ? dv.z : 0) + a.Num("prograde");
            double ut = a.Has("ut") ? a.Num("ut") : a.Has("in") ? Now + a.Num("in") : n.UT;
            SetNode(v, n, ut, dv);
            return NodeInfo(v, n, i);
        }

        [Cmd("node_clear", "Remove all maneuver nodes (or {index}).")]
        static object NodeClear(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var s = Solver(v);
            if (a.Has("index")) s.maneuverNodes[(int)a.Num("index")].RemoveSelf();
            else while (s.maneuverNodes.Count > 0) s.maneuverNodes[s.maneuverNodes.Count - 1].RemoveSelf();
            return s.maneuverNodes.Count;
        }

        // ------------------------------------------------------------------ planners

        static double ResolveUT(Vessel v, Args a, string defAt)
        {
            if (a.Has("ut")) return a.Num("ut");
            if (a.Has("in")) return Now + a.Num("in");
            var o = v.orbit;
            switch (a.Str("at", defAt))
            {
                case "apoapsis":
                    if (o.eccentricity >= 1) throw new HarnessException("hyperbolic orbit has no apoapsis");
                    return Now + o.timeToAp;
                case "periapsis": return Now + o.timeToPe;
                case "now": return Now + 30;
                default: throw new HarnessException("'at' must be apoapsis|periapsis|now (or give ut/in)");
            }
        }

        [Cmd("plan_circularize", "Create a node circularizing the orbit: {at=apoapsis|periapsis|now, ut?, in?}")]
        static object PlanCircularize(Args a)
        {
            var v = CmdFlight.RequireVessel();
            double ut = ResolveUT(v, a, "apoapsis");
            var o = PatchAt(v, ut);
            Vector3d r = o.getRelativePositionAtUT(ut);
            Vector3d vel = o.getOrbitalVelocityAtUT(ut);
            Vector3d horiz = Vector3d.Exclude(r, vel).normalized;
            double vc = Math.Sqrt(o.referenceBody.gravParameter / r.magnitude);
            Vector3d dv = horiz * vc - vel;
            var n = AddNode(v, ut, ToNodeCoords(o, ut, dv));
            return NodeInfo(v, n, Solver(v).maneuverNodes.IndexOf(n));
        }

        static Orbit Propagate(Orbit o, double ut, Vector3d dv)
        {
            var res = new Orbit();
            res.UpdateFromStateVectors(o.getRelativePositionAtUT(ut), o.getOrbitalVelocityAtUT(ut) + dv, o.referenceBody, ut);
            return res;
        }

        /// <summary>Find x in [lo,hi] with f(x)=0 for monotone f, by bisection.</summary>
        static double Bisect(Func<double, double> f, double lo, double hi, int iters = 60)
        {
            double flo = f(lo);
            for (int i = 0; i < iters; i++)
            {
                double mid = (lo + hi) / 2, fm = f(mid);
                if (Math.Sign(fm) == Math.Sign(flo)) { lo = mid; flo = fm; } else hi = mid;
            }
            return (lo + hi) / 2;
        }

        [Cmd("plan_apsis", "Node to set the opposite apsis by a prograde/retrograde burn: {which=periapsis|apoapsis (the one to change), altitude, at=apoapsis|periapsis|now, ut?, in?}")]
        static object PlanApsis(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var which = a.Str("which", "periapsis");
            double alt = a.ReqNum("altitude");
            double ut = ResolveUT(v, a, which == "periapsis" ? "apoapsis" : "periapsis");
            var o = PatchAt(v, ut);
            Vector3d pro = o.getOrbitalVelocityAtUT(ut).normalized;
            double R = o.referenceBody.Radius;
            double rNow = o.getRelativePositionAtUT(ut).magnitude;
            double target = alt + R;
            // Pick the apsis that is "the other one" from the burn point, i.e. not at the current radius.
            Func<double, double> otherApsis = dv =>
            {
                var no = Propagate(o, ut, pro * dv);
                double pe = no.PeR, ap = no.eccentricity < 1 ? no.ApR : double.PositiveInfinity;
                return Math.Abs(pe - rNow) > Math.Abs(ap - rNow) ? pe : ap;
            };
            double dvMax = Math.Sqrt(2 * o.referenceBody.gravParameter / rNow) * 1.5;
            double sol = Bisect(dvx => otherApsis(dvx) - target, -o.getOrbitalVelocityAtUT(ut).magnitude * 0.999, dvMax);
            var n = AddNode(v, ut, new Vector3d(0, 0, sol));
            return NodeInfo(v, n, Solver(v).maneuverNodes.IndexOf(n));
        }

        [Cmd("plan_inclination", "Node for a plane change at the next ascending/descending node relative to the equator: {inclination=0}")]
        static object PlanInclination(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var o = v.orbit;
            double incTarget = a.Num("inclination", 0) * Math.PI / 180;
            // Equatorial nodes: where position is perpendicular to the body's pole; find next UT when z crosses 0 (orbit frame Z = pole).
            double t0 = Now + 60, period = o.eccentricity < 1 ? o.period : 3600;
            double best = double.NaN;
            double prevZ = o.getRelativePositionAtUT(t0).z;
            for (int i = 1; i <= 720; i++)
            {
                double t = t0 + period * i / 720.0;
                double z = o.getRelativePositionAtUT(t).z;
                if (Math.Sign(z) != Math.Sign(prevZ))
                {
                    double tl = t - period / 720.0;
                    best = Bisect(x => o.getRelativePositionAtUT(x).z, tl, t, 40);
                    break;
                }
                prevZ = z;
            }
            if (double.IsNaN(best)) throw new HarnessException("no equatorial crossing found");
            Vector3d r = o.getRelativePositionAtUT(best);
            Vector3d vel = o.getOrbitalVelocityAtUT(best);
            // Desired velocity: same magnitude/vertical component, horizontal part rotated about r so inclination matches.
            Vector3d up = r.normalized;
            Vector3d vVert = up * Vector3d.Dot(vel, up);
            Vector3d vHor = vel - vVert;
            Vector3d pole = new Vector3d(0, 0, 1);
            Vector3d east = Vector3d.Cross(pole, up).normalized;
            Vector3d north = Vector3d.Cross(up, east).normalized;
            double sign = Vector3d.Dot(vHor, east) >= 0 ? 1 : -1; // keep prograde/retrograde sense
            double northSign = Vector3d.Dot(vHor, north) >= 0 ? 1 : -1;
            Vector3d newHor = (east * sign * Math.Cos(incTarget) + north * northSign * Math.Sin(incTarget)) * vHor.magnitude;
            Vector3d dv = newHor + vVert - vel;
            var n = AddNode(v, best, ToNodeCoords(o, best, dv));
            return NodeInfo(v, n, Solver(v).maneuverNodes.IndexOf(n));
        }

        static ITargetable ResolveTarget(Vessel v, Args a)
        {
            var name = a.Str("target");
            if (name == null) return v.targetObject ?? throw new HarnessException("no target given or set");
            ITargetable t = FlightGlobals.Bodies.FirstOrDefault(b => b.bodyName.Equals(name, StringComparison.OrdinalIgnoreCase));
            return t ?? FlightGlobals.Vessels.FirstOrDefault(x => Harness.L(x.vesselName).Equals(name, StringComparison.OrdinalIgnoreCase)) ?? throw new HarnessException("unknown target " + name);
        }

        /// <summary>Minimum distance between two orbits around the same body within [t0,t1].</summary>
        static double MinDistance(Orbit a, Orbit b, double t0, double t1, out double tMin, int N = 200)
        {
            double best = double.PositiveInfinity; tMin = t0;
            for (int i = 0; i <= N; i++)
            {
                double t = t0 + (t1 - t0) * i / N;
                double d = (a.getRelativePositionAtUT(t) - b.getRelativePositionAtUT(t)).magnitude;
                if (d < best) { best = d; tMin = t; }
            }
            double step = (t1 - t0) / N;
            for (int k = 0; k < 30; k++)
            {
                step /= 2;
                foreach (var t in new[] { tMin - step, tMin + step })
                {
                    if (t < t0 || t > t1) continue;
                    double d = (a.getRelativePositionAtUT(t) - b.getRelativePositionAtUT(t)).magnitude;
                    if (d < best) { best = d; tMin = t; }
                }
            }
            return best;
        }

        [Cmd("plan_hohmann", "Node for a Hohmann transfer to a target orbiting the same body (moon or vessel): {target?=current target, periapsis?=desired periapsis altitude at a body target (default: 1/10 radius+atmo)}")]
        static object PlanHohmann(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var tgt = ResolveTarget(v, a);
            var o = v.orbit;
            var tOrb = tgt.GetOrbit();
            if (tOrb == null || tOrb.referenceBody != o.referenceBody) throw new HarnessException("target must orbit the same body as the vessel");
            if (o.eccentricity >= 1) throw new HarnessException("vessel must be in a closed orbit");
            double mu = o.referenceBody.gravParameter;
            var body = tgt as CelestialBody;

            // 1) coarse phasing search: burn prograde at t so that apoapsis reaches the target's orbit at arrival.
            double synodic = 1.0 / Math.Abs(1.0 / o.period - 1.0 / tOrb.period);
            double span = Math.Min(synodic, 20 * o.period) + o.period;
            double bestT = double.NaN, bestErr = double.PositiveInfinity, bestDv = 0;
            int N = 720;
            for (int i = 0; i < N; i++)
            {
                double t = Now + 120 + span * i / N;
                Vector3d r = o.getRelativePositionAtUT(t);
                double r1 = r.magnitude;
                double r2 = tOrb.getRelativePositionAtUT(t).magnitude; // approx
                double at = (r1 + r2) / 2;
                double tof = Math.PI * Math.Sqrt(at * at * at / mu);
                Vector3d arrive = -r.normalized;
                Vector3d tp = tOrb.getRelativePositionAtUT(t + tof).normalized;
                double err = Vector3d.Angle(arrive, tp);
                if (err < bestErr)
                {
                    bestErr = err; bestT = t;
                    bestDv = Math.Sqrt(mu * (2 / r1 - 1 / at)) - o.getOrbitalVelocityAtUT(t).magnitude;
                }
            }

            // 2) refine (ut, prograde dv) to minimize closest approach, then tune periapsis for bodies.
            double ut = bestT, dv = bestDv;
            Func<double, double, double> approach = (u, d) =>
            {
                var no = Propagate(o, u, o.getOrbitalVelocityAtUT(u).normalized * d);
                double tEnd = u + (no.eccentricity < 1 ? no.period * 0.75 : 1e6);
                return MinDistance(no, tOrb, u, tEnd, out _);
            };
            double sUt = o.period / 36, sDv = 10;
            double cur = approach(ut, dv);
            for (int k = 0; k < 200 && (sUt > 0.05 || sDv > 0.005); k++)
            {
                bool improved = false;
                foreach (var cand in new[] { (ut + sUt, dv), (ut - sUt, dv), (ut, dv + sDv), (ut, dv - sDv) })
                {
                    double c = approach(cand.Item1, cand.Item2);
                    if (c < cur) { cur = c; ut = cand.Item1; dv = cand.Item2; improved = true; }
                }
                if (!improved) { sUt /= 2; sDv /= 2; }
            }

            var node = AddNode(v, ut, new Vector3d(0, 0, dv));
            var result = new Dictionary<string, object> { ["closest_approach_m"] = Math.Round(cur) };

            if (body != null)
            {
                double desiredPe = a.Has("periapsis") ? a.Num("periapsis") : (body.atmosphere ? body.atmosphereDepth : 0) + body.Radius * 0.1;
                // fine-tune prograde dv against the patched conic solver's actual encounter periapsis
                Func<double, double> peErr = d =>
                {
                    SetNode(v, node, ut, new Vector3d(0, 0, d));
                    var enc = Encounter(node.nextPatch);
                    if (enc == null || enc.referenceBody != body) return double.NaN;
                    return enc.PeA - desiredPe;
                };
                double e0 = peErr(dv);
                if (!double.IsNaN(e0))
                {
                    double best = dv, bestAbs = Math.Abs(e0);
                    double step = 0.5;
                    for (int k = 0; k < 60 && step > 0.001; k++)
                    {
                        bool imp = false;
                        foreach (var d in new[] { best + step, best - step })
                        {
                            double e = peErr(d);
                            if (!double.IsNaN(e) && Math.Abs(e) < bestAbs) { bestAbs = Math.Abs(e); best = d; imp = true; }
                        }
                        if (!imp) step /= 2;
                    }
                    SetNode(v, node, ut, new Vector3d(0, 0, best));
                    result["periapsis_error_m"] = Math.Round(bestAbs);
                }
                else
                {
                    SetNode(v, node, ut, new Vector3d(0, 0, dv));
                    result["warning"] = "no SOI encounter found by patched conics; check plane alignment (plan_inclination) or adjust manually";
                }
            }
            result["node"] = NodeInfo(v, node, Solver(v).maneuverNodes.IndexOf(node));
            return result;
        }

        [Cmd("node_approach", "How the planned trajectory meets a body: {target, index?=last node (or current orbit if no nodes)}. Returns the encounter (periapsis, UT, inclination) if any, and the closest approach to the target along the patch that orbits the target's parent.")]
        static object NodeApproach(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var body = ResolveTarget(v, a) as CelestialBody ?? throw new HarnessException("target must be a body");
            var s = Solver(v);
            s.UpdateFlightPlan();
            Orbit o = v.orbit;
            if (s.maneuverNodes.Count > 0)
            {
                int i = a.Has("index") ? (int)a.Num("index") : s.maneuverNodes.Count - 1;
                o = s.maneuverNodes[i].nextPatch ?? throw new HarnessException("node has no trajectory");
            }
            var res = new Dictionary<string, object> { ["target"] = body.bodyName };
            var parent = body.referenceBody;
            int guard = 0;
            for (; o != null && guard++ < 12; o = o.nextPatch)
            {
                if (o.referenceBody == body)
                {
                    res["encounter"] = true;
                    res["periapsis"] = Math.Round(o.PeA, 1);
                    res["periapsis_ut"] = Math.Round(PeUT(o), 1);
                    res["inclination"] = Math.Round(o.inclination, 3);
                    res["eccentricity"] = Math.Round(o.eccentricity, 5);
                    res["soi_entry_ut"] = Math.Round(o.StartUT, 1);
                    break;
                }
                if (o.referenceBody == parent && !res.ContainsKey("closest_approach"))
                {
                    double t0 = Math.Max(o.StartUT, Now);
                    double t1 = o.patchEndTransition == Orbit.PatchTransitionType.FINAL || o.EndUT <= t0
                        ? t0 + (o.eccentricity < 1 ? o.period : 1e7) : o.EndUT;
                    double d = MinDistance(o, body.orbit, t0, t1, out double tMin, 500);
                    res["closest_approach"] = Math.Round(d);
                    res["closest_approach_ut"] = Math.Round(tMin, 1);
                    // relative velocity at closest approach (for B-plane style corrections)
                    res["relative_speed"] = Math.Round((o.getOrbitalVelocityAtUT(tMin) - body.orbit.getOrbitalVelocityAtUT(tMin)).magnitude, 2);
                }
                if (o.patchEndTransition == Orbit.PatchTransitionType.FINAL || o.nextPatch == null || !o.nextPatch.activePatch) break;
            }
            if (!res.ContainsKey("encounter")) res["encounter"] = false;
            return res;
        }

        /// <summary>UT of the next periapsis passage on this patch (for hyperbolic patches, the only one).</summary>
        static double PeUT(Orbit o)
        {
            double t0 = Math.Max(o.StartUT, Now);
            double t1 = o.patchEndTransition == Orbit.PatchTransitionType.FINAL || o.EndUT <= t0
                ? t0 + (o.eccentricity < 1 ? o.period : 1e7) : o.EndUT;
            // coarse scan then golden-section refine on radius
            int N = 400; double best = double.PositiveInfinity, tb = t0;
            for (int i = 0; i <= N; i++)
            {
                double t = t0 + (t1 - t0) * i / N, r = o.getRelativePositionAtUT(t).magnitude;
                if (r < best) { best = r; tb = t; }
            }
            double lo = Math.Max(t0, tb - (t1 - t0) / N), hi = Math.Min(t1, tb + (t1 - t0) / N);
            for (int k = 0; k < 80; k++)
            {
                double m1 = lo + (hi - lo) * 0.382, m2 = lo + (hi - lo) * 0.618;
                if (o.getRelativePositionAtUT(m1).magnitude < o.getRelativePositionAtUT(m2).magnitude) hi = m2; else lo = m1;
            }
            return (lo + hi) / 2;
        }

        [Cmd("plan_return","From orbit around a moon, node to return to the parent body with a given periapsis: {periapsis=30000}")]
        static object PlanReturn(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var o = v.orbit;
            var moon = o.referenceBody;
            var parent = moon.referenceBody;
            if (parent == null || parent == moon) throw new HarnessException("not orbiting a moon");
            if (o.eccentricity >= 1) throw new HarnessException("need a closed orbit around " + moon.bodyName);
            double desired = a.Num("periapsis", parent.atmosphere ? parent.atmosphereDepth * 0.43 : 30000);
            var node = AddNode(v, Now + 300, Vector3d.zero);
            Func<double, double, double> eval = (ut, dv) =>
            {
                SetNode(v, node, ut, new Vector3d(0, 0, dv));
                var enc = Encounter(node.nextPatch);
                if (enc == null || enc.referenceBody != parent) return double.PositiveInfinity;
                return Math.Abs(enc.PeA - desired);
            };
            double escape = Math.Sqrt(2 * moon.gravParameter / o.semiMajorAxis) - Math.Sqrt(moon.gravParameter / o.semiMajorAxis);
            double bestUt = double.NaN, bestDv = 0, best = double.PositiveInfinity;
            for (int i = 0; i < 72; i++)
            {
                double ut = Now + 120 + o.period * i / 72.0;
                foreach (var f in new[] { 1.05, 1.15, 1.3, 1.5 })
                {
                    double c = eval(ut, escape * f);
                    if (c < best) { best = c; bestUt = ut; bestDv = escape * f; }
                }
            }
            if (double.IsInfinity(best)) { node.RemoveSelf(); throw new HarnessException("no escape trajectory found"); }
            double sUt = o.period / 72, sDv = escape * 0.05;
            for (int k = 0; k < 200 && (sUt > 0.05 || sDv > 0.005); k++)
            {
                bool imp = false;
                foreach (var cand in new[] { (bestUt + sUt, bestDv), (bestUt - sUt, bestDv), (bestUt, bestDv + sDv), (bestUt, bestDv - sDv) })
                {
                    double c = eval(cand.Item1, cand.Item2);
                    if (c < best) { best = c; bestUt = cand.Item1; bestDv = cand.Item2; imp = true; }
                }
                if (!imp) { sUt /= 2; sDv /= 2; }
            }
            SetNode(v, node, bestUt, new Vector3d(0, 0, bestDv));
            return new Dictionary<string, object> { ["periapsis_error_m"] = Math.Round(best), ["node"] = NodeInfo(v, node, Solver(v).maneuverNodes.IndexOf(node)) };
        }

        [Cmd("debug_frames", "Diagnostic: compare harness direction conventions against a probe maneuver node.")]
        static object DebugFrames(Args a)
        {
            var v = CmdFlight.RequireVessel();
            double ut = Now + 1;
            var res = new Dictionary<string, object>();
            var s = Solver(v);
            foreach (var (label, dv) in new[] { ("prograde", new Vector3d(0, 0, 10)), ("normal", new Vector3d(0, 10, 0)), ("radial", new Vector3d(10, 0, 0)) })
            {
                var n = AddNode(v, ut, dv);
                Vector3d burn = n.GetBurnVector(v.orbit).normalized;
                n.RemoveSelf();
                Vector3d mine = label == "prograde" ? v.obt_velocity.normalized : label == "normal" ? Autopilot.OrbitNormal(v) : Autopilot.RadialOut(v);
                res[label + "_dot_harness_dir"] = Math.Round(Vector3d.Dot(burn, mine), 4);
            }
            res["normal_dot_north"] = Math.Round(Vector3d.Dot(Autopilot.OrbitNormal(v), v.north), 4);
            res["vessel_fwd_dot_up"] = Math.Round(Vector3d.Dot(v.ReferenceTransform.up, Autopilot.Up(v)), 4);
            return res;
        }
    }
}
