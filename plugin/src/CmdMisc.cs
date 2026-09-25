using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace KSPHarness
{
    static class CmdMisc
    {
        // ------------------------------------------------------------------ screenshots / view

        [Cmd("screenshot", "Capture the game window to a PNG: {path? (default <KSP>/Screenshots/harness_<time>.png), hide_ui?:bool, scale?=1}. Returns the path; the file appears after the frame renders.")]
        static object Screenshot(Args a)
        {
            var path = a.Str("path") ?? Path.Combine(Path.Combine(KSPUtil.ApplicationRootPath, "Screenshots"), "harness_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            if (File.Exists(path)) File.Delete(path);
            bool hide = a.Bool("hide_ui");
            if (hide) GameEvents.onHideUI.Fire();
            ScreenCapture.CaptureScreenshot(path, (int)a.Num("scale", 1));
            if (hide)
            {
                int frame = Time.frameCount;
                Harness.Instance.Defer(() => Time.frameCount > frame + 2, () => GameEvents.onShowUI.Fire());
            }
            return Path.GetFullPath(path);
        }

        [Cmd("map", "Toggle map view: {on=true}")]
        static object Map(Args a)
        {
            CmdFlight.RequireVessel();
            if (a.Bool("on", true)) MapView.EnterMapView(); else MapView.ExitMapView();
            return a.Bool("on", true);
        }

        [Cmd("camera", "Flight camera: {distance?, pitch?, heading? (degrees), mode?=AUTO|FREE|ORBITAL|CHASE|LOCKED}")]
        static object Camera(Args a)
        {
            CmdFlight.RequireVessel();
            var fc = FlightCamera.fetch;
            if (a.Has("mode")) fc.setModeImmediate((FlightCamera.Modes)Enum.Parse(typeof(FlightCamera.Modes), a.Str("mode"), true));
            if (a.Has("distance")) fc.SetDistance((float)a.Num("distance"));
            if (a.Has("pitch")) fc.camPitch = (float)(a.Num("pitch") * Math.PI / 180);
            if (a.Has("heading")) fc.camHdg = (float)(a.Num("heading") * Math.PI / 180);
            return new Dictionary<string, object> { ["mode"] = fc.mode.ToString(), ["distance"] = fc.Distance, ["pitch"] = fc.camPitch * 180 / Math.PI, ["heading"] = fc.camHdg * 180 / Math.PI };
        }

        // ------------------------------------------------------------------ landing

        [Cmd("land", "Powered landing autopilot (suicide burn with margin): {touch_speed=1.5, margin=0.6, gear=true, height_offset?} or {off:true}. Use from a descending trajectory.")]
        static object Land(Args a)
        {
            var v = CmdFlight.RequireVessel();
            if (a.Bool("off"))
            {
                Autopilot.Land.Active = false;
                Autopilot.AttMode = "off";
                return "landing autopilot off";
            }
            Autopilot.StartLand(v, a.Num("touch_speed", 1.5), a.Num("margin", 0.6), a.Bool("gear", true), a.Num("height_offset", double.NaN));
            return new Dictionary<string, object> { ["status"] = Autopilot.Land.Status, ["height_offset"] = Autopilot.Land.HeightOffset };
        }

        // ------------------------------------------------------------------ science

        static string Situation(Vessel v) => ScienceUtil.GetExperimentSituation(v).ToString();

        [Cmd("science", "List science experiments on the vessel with value in the current situation, plus stored data.")]
        static object Science(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var sit = ScienceUtil.GetExperimentSituation(v);
            var biome = ScienceUtil.GetExperimentBiome(v.mainBody, v.latitude, v.longitude);
            var res = new List<object>();
            foreach (var m in v.FindPartModulesImplementing<ModuleScienceExperiment>())
            {
                var exp = ResearchAndDevelopment.GetExperiment(m.experimentID);
                var d = new Dictionary<string, object>
                {
                    ["part"] = m.part.persistentId,
                    ["part_title"] = m.part.partInfo.title,
                    ["experiment"] = m.experimentID,
                    ["title"] = exp?.experimentTitle,
                    ["deployed"] = m.Deployed,
                    ["inoperable"] = m.Inoperable,
                    ["rerunnable"] = m.rerunnable,
                    ["data"] = m.GetData().Select(x => x.title + " (" + x.dataAmount.ToString("F1") + " Mits)").ToList(),
                };
                if (exp != null)
                {
                    bool available = exp.IsAvailableWhile(sit, v.mainBody);
                    d["available_here"] = available;
                    if (available && ResearchAndDevelopment.Instance != null)
                    {
                        string b = exp.BiomeIsRelevantWhile(sit) ? biome : "";
                        var subj = ResearchAndDevelopment.GetExperimentSubject(exp, sit, v.mainBody, b, ScienceUtil.GetBiomedisplayName(v.mainBody, b));
                        d["subject"] = subj.title;
                        d["value_recovered"] = Math.Round(ResearchAndDevelopment.GetScienceValue(exp.baseValue * exp.dataScale, subj, 1f), 2);
                        d["value_transmitted"] = Math.Round(ResearchAndDevelopment.GetScienceValue(exp.baseValue * exp.dataScale, subj, m.xmitDataScalar), 2);
                    }
                }
                res.Add(d);
            }
            return new Dictionary<string, object> { ["situation"] = sit.ToString(), ["biome"] = biome, ["experiments"] = res };
        }

        [Cmd("science_run", "Run experiments without the results dialog: {part? (id/name; default all available), experiment?}")]
        static object ScienceRun(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var sit = ScienceUtil.GetExperimentSituation(v);
            var ran = new List<object>();
            var gather = typeof(ModuleScienceExperiment).GetMethod("gatherData", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Part only = a.Has("part") ? CmdFlight.FindPart(v, a.Str("part")) : null;
            var expFilter = a.Str("experiment");
            var seen = new HashSet<string>();
            foreach (var m in v.FindPartModulesImplementing<ModuleScienceExperiment>())
            {
                if (only != null && m.part != only) continue;
                if (expFilter != null && m.experimentID != expFilter) continue;
                if (m.Inoperable || m.Deployed || m.GetScienceCount() > 0) continue;
                var exp = ResearchAndDevelopment.GetExperiment(m.experimentID);
                if (exp == null || !exp.IsAvailableWhile(sit, v.mainBody)) continue;
                if (only == null && !seen.Add(m.experimentID)) continue; // one of each type unless a part is named
                if (gather != null)
                {
                    var p = gather.GetParameters();
                    var co = (IEnumerator)gather.Invoke(m, p.Length == 1 ? new object[] { true } : new object[0]);
                    m.StartCoroutine(co);
                }
                else m.DeployExperiment();
                ran.Add(m.part.partInfo.title + ": " + m.experimentID);
            }
            // gatherData still pops the results window in 1.12; close it once it appears (data stays stored).
            if (ran.Count > 0 && !a.Bool("keep_dialog"))
            {
                float until = Time.realtimeSinceStartup + 5f;
                Harness.Instance.Defer(
                    () => KSP.UI.Screens.Flight.Dialogs.ExperimentsResultDialog.Instance != null || Time.realtimeSinceStartup > until,
                    () => KSP.UI.Screens.Flight.Dialogs.ExperimentsResultDialog.Instance?.Dismiss());
            }
            return ran;
        }

        static IEnumerable<IScienceDataContainer> Containers(Vessel v) =>
            v.parts.SelectMany(p => p.Modules.OfType<IScienceDataContainer>());

        [Cmd("science_transmit", "Transmit all stored science data via the vessel's antennas: {experiment?}")]
        static object ScienceTransmit(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var tx = v.FindPartModulesImplementing<ModuleDataTransmitter>().FirstOrDefault(t => t.CanTransmit());
            if (tx == null) throw new HarnessException("no usable antenna / no connection");
            var queue = new List<ScienceData>();
            foreach (var c in Containers(v))
                foreach (var d in c.GetData())
                {
                    if (a.Has("experiment") && !d.subjectID.StartsWith(a.Str("experiment"))) continue;
                    queue.Add(d);
                    c.DumpData(d);
                }
            if (queue.Count == 0) return "no data to transmit";
            tx.TransmitData(queue);
            return queue.Select(d => d.title).ToList();
        }

        [Cmd("science_reset", "Reset (clear) experiments so they can be rerun: {part?}")]
        static object ScienceReset(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var list = new List<object>();
            foreach (var m in v.FindPartModulesImplementing<ModuleScienceExperiment>())
            {
                if (a.Has("part") && m.part != CmdFlight.FindPart(v, a.Str("part"))) continue;
                if (!m.Deployed && m.GetScienceCount() == 0) continue;
                if (!m.rerunnable && !m.resettable) continue;
                m.ResetExperiment();
                list.Add(m.part.partInfo.title);
            }
            return list;
        }

        // ------------------------------------------------------------------ EVA / crew

        [Cmd("eva", "Send a kerbal on EVA: {kerbal? (default first crew)}")]
        static object Eva(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var crew = v.GetVesselCrew();
            var k = a.Has("kerbal") ? crew.FirstOrDefault(c => c.name.IndexOf(a.Str("kerbal"), StringComparison.OrdinalIgnoreCase) >= 0) : crew.FirstOrDefault();
            if (k == null) throw new HarnessException("no such crew aboard");
            if (!FlightEVA.fetch.spawnEVA(k, k.seat.part, k.seat.part.airlock)) throw new HarnessException("EVA failed (hatch blocked?)");
            return "EVA: " + k.name;
        }

        static KerbalEVA RequireEva()
        {
            var v = CmdFlight.RequireVessel();
            return v.GetComponent<KerbalEVA>() ?? throw new HarnessException("active vessel is not a kerbal on EVA");
        }

        static readonly FieldInfo[] evaEvents = typeof(KerbalEVA).GetFields(BindingFlags.Instance | BindingFlags.Public)
            .Where(f => f.FieldType == typeof(KFSMEvent)).ToArray();

        [Cmd("eva_state", "State of the EVA kerbal: FSM state, ladder, flags carried, available FSM events.")]
        static object EvaState(Args a)
        {
            var eva = RequireEva();
            return new Dictionary<string, object>
            {
                ["state"] = eva.fsm.currentStateName,
                ["on_ladder"] = eva.OnALadder,
                ["flags"] = eva.flagItems,
                ["jetpack"] = eva.JetpackDeployed,
                ["situation"] = eva.vessel.situation.ToString(),
                ["events"] = evaEvents.Select(f => f.Name).ToList(),
            };
        }

        [Cmd("eva_event", "Run a kerbal FSM event, e.g. On_ladderLetGo, On_jump_start, On_packToggle: {event}")]
        static object EvaEvent(Args a)
        {
            var eva = RequireEva();
            var name = a.ReqStr("event");
            var f = evaEvents.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new HarnessException("no FSM event '" + name + "' (see eva_state)");
            string before = eva.fsm.currentStateName;
            eva.fsm.RunEvent((KFSMEvent)f.GetValue(eva));
            return new Dictionary<string, object> { ["before"] = before, ["after"] = eva.fsm.currentStateName };
        }

        [Cmd("plant_flag", "Plant a flag (active vessel must be a kerbal standing on the surface). Emits flag.planted when done.")]
        static object PlantFlag(Args a)
        {
            var eva = RequireEva();
            if (eva.OnALadder) throw new HarnessException("kerbal is on a ladder; run eva_event event=On_ladderLetGo first and wait until landed");
            if (!eva.vessel.LandedOrSplashed) throw new HarnessException("kerbal is not on the ground (" + eva.vessel.situation + ")");
            if (eva.flagItems <= 0) throw new HarnessException("kerbal carries no flags");
            string before = eva.fsm.currentStateName;
            eva.PlantFlag();
            string after = eva.fsm.currentStateName;
            if (after == before) throw new HarnessException("flag planting did not start (state stays " + before + ")");
            return new Dictionary<string, object> { ["state"] = after, ["note"] = "watch for the flag.planted event" };
        }

        [Cmd("board", "Board the nearest vessel with a free seat (active vessel must be a kerbal on EVA).")]
        static object Board(Args a)
        {
            var v = CmdFlight.RequireVessel();
            var eva = v.GetComponent<KerbalEVA>() ?? throw new HarnessException("active vessel is not a kerbal on EVA");
            Part best = null; double bestD = 50;
            foreach (var ov in FlightGlobals.VesselsLoaded)
            {
                if (ov == v) continue;
                foreach (var p in ov.parts)
                {
                    if (p.CrewCapacity <= p.protoModuleCrew.Count) continue;
                    double d = (p.transform.position - v.transform.position).magnitude;
                    if (d < bestD) { bestD = d; best = p; }
                }
            }
            if (best == null) throw new HarnessException("no vessel with free seat within 50 m");
            eva.BoardPart(best);
            return "boarding " + Harness.L(best.vessel.vesselName);
        }

        // ------------------------------------------------------------------ reflection escape hatch

        static readonly Dictionary<string, Type> typeCache = new Dictionary<string, Type>();

        static Type FindType(string name)
        {
            if (typeCache.TryGetValue(name, out var t)) return t;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types.Where(x => x != null).ToArray(); }
                t = types.FirstOrDefault(x => x.FullName == name) ?? types.FirstOrDefault(x => x.Name == name && x.IsPublic);
                if (t != null) break;
            }
            typeCache[name] = t;
            return t;
        }

        static List<string> SplitChain(string expr)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            bool inStr = false;
            for (int i = 0; i < expr.Length; i++)
            {
                char c = expr[i];
                if (c == '"') inStr = !inStr;
                if (inStr) continue;
                if (c == '(' || c == '[') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == '.' && depth == 0 && !(i > 0 && char.IsDigit(expr[i - 1]) && i + 1 < expr.Length && char.IsDigit(expr[i + 1]) && parts.Count == 0))
                {
                    parts.Add(expr.Substring(start, i - start).Trim());
                    start = i + 1;
                }
            }
            parts.Add(expr.Substring(start).Trim());
            return parts;
        }

        static object ParseLiteral(string s)
        {
            s = s.Trim();
            if (s.StartsWith("\"") && s.EndsWith("\"")) return s.Substring(1, s.Length - 2);
            if (s == "true") return true;
            if (s == "false") return false;
            if (s == "null") return null;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)) return d;
            throw new HarnessException("can't parse argument literal: " + s);
        }

        static object Convert(object v, Type t)
        {
            if (v == null) return null;
            if (t.IsInstanceOfType(v)) return v;
            if (t.IsEnum) return Enum.Parse(t, v.ToString(), true);
            return System.Convert.ChangeType(v, t, CultureInfo.InvariantCulture);
        }

        static object Step(object obj, Type type, string token)
        {
            string name = token;
            int? index = null;
            List<object> args = null;
            int b = name.IndexOf('[');
            if (b >= 0 && name.EndsWith("]"))
            {
                index = int.Parse(name.Substring(b + 1, name.Length - b - 2));
                name = name.Substring(0, b);
            }
            int p = name.IndexOf('(');
            if (p >= 0)
            {
                var inner = name.Substring(p + 1, name.Length - p - 2);
                args = inner.Trim().Length == 0 ? new List<object>() : inner.Split(',').Select(ParseLiteral).ToList();
                name = name.Substring(0, p);
            }
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            object result;
            if (args != null)
            {
                var m = type.GetMethods(F).FirstOrDefault(x => x.Name == name && x.GetParameters().Length == args.Count)
                    ?? throw new HarnessException("no method " + type.Name + "." + name + "/" + args.Count);
                var ps = m.GetParameters();
                result = m.Invoke(obj, args.Select((x, i) => Convert(x, ps[i].ParameterType)).ToArray());
            }
            else
            {
                var f = type.GetField(name, F);
                if (f != null) result = f.GetValue(obj);
                else
                {
                    var pr = type.GetProperty(name, F) ?? throw new HarnessException("no member " + type.Name + "." + name);
                    result = pr.GetValue(obj, null);
                }
            }
            if (index.HasValue)
            {
                if (result is IList l) result = l[index.Value];
                else throw new HarnessException(name + " is not indexable");
            }
            return result;
        }

        /// <summary>Evaluate a dotted chain like FlightGlobals.ActiveVessel.orbit.ApA. Returns (object, lastToken, parent).</summary>
        static object EvalChain(List<string> chain, int upto, out object parent, out Type parentType)
        {
            // Find the longest prefix that names a static type.
            int k = chain.Count;
            Type t = null;
            for (; k > 0; k--)
            {
                t = FindType(string.Join(".", chain.Take(k).ToArray()));
                if (t != null) break;
            }
            if (t == null) throw new HarnessException("unknown type in '" + string.Join(".", chain.ToArray()) + "'");
            object cur = null;
            Type curType = t;
            parent = null; parentType = t;
            for (int i = k; i < upto; i++)
            {
                parent = cur; parentType = curType;
                cur = Step(cur, curType, chain[i]);
                if (cur == null && i < upto - 1) throw new HarnessException("null at " + chain[i]);
                curType = cur?.GetType();
            }
            if (k == upto) { parent = null; parentType = t; }
            return cur;
        }

        static object Describe(object o, bool members)
        {
            if (o == null || o is string || o is bool || o is Enum || o.GetType().IsPrimitive || o is Vector3 || o is Vector3d || o is Quaternion || o is Guid) return o;
            if (o is IDictionary dict)
            {
                var d = new Dictionary<string, object>();
                foreach (DictionaryEntry kv in dict) { if (d.Count >= 100) break; d[kv.Key.ToString()] = kv.Value?.ToString(); }
                return d;
            }
            if (o is IEnumerable en)
                return en.Cast<object>().Take(100).Select(x => x == null || x.GetType().IsPrimitive || x is string ? x : x.ToString()).ToList();
            if (!members) return o.ToString();
            var res = new Dictionary<string, object> { ["_type"] = o.GetType().FullName, ["_str"] = o.ToString() };
            foreach (var f in o.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                try { var val = f.GetValue(o); res[f.Name] = val == null || val.GetType().IsPrimitive || val is string || val is Enum ? val : val.ToString(); } catch { }
            }
            foreach (var p in o.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length > 0) continue;
                try { var val = p.GetValue(o, null); res[p.Name] = val == null || val.GetType().IsPrimitive || val is string || val is Enum ? val : val.ToString(); } catch { }
            }
            return res;
        }

        [Cmd("eval", "Reflection escape hatch. Evaluate a member chain: {expr, members?:bool}. e.g. FlightGlobals.ActiveVessel.orbit.ApA, FlightGlobals.ActiveVessel.parts[0].partInfo.title, TimeWarp.SetRate(0,true,true)")]
        static object Eval(Args a)
        {
            var chain = SplitChain(a.ReqStr("expr"));
            var r = EvalChain(chain, chain.Count, out _, out _);
            return Describe(r, a.Bool("members"));
        }

        [Cmd("set", "Reflection escape hatch: set a field/property: {expr, value}. e.g. expr=FlightGlobals.ActiveVessel.vesselName value=\"Foo\"")]
        static object Set(Args a)
        {
            var chain = SplitChain(a.ReqStr("expr"));
            if (chain.Count < 2) throw new HarnessException("expr must be Owner.member");
            var last = chain[chain.Count - 1];
            // When the owner prefix is a static type, EvalChain returns null and reports that type.
            object owner = EvalChain(chain, chain.Count - 1, out _, out Type staticType);
            Type ownerType = owner?.GetType() ?? staticType;
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            var f = ownerType.GetField(last, F);
            object value = a.Raw("value");
            if (f != null) { f.SetValue(owner, Convert(value, f.FieldType)); return Describe(f.GetValue(owner), false); }
            var p = ownerType.GetProperty(last, F) ?? throw new HarnessException("no member " + last);
            p.SetValue(owner, Convert(value, p.PropertyType), null);
            return Describe(p.GetValue(owner, null), false);
        }
    }
}
