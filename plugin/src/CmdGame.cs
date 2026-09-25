using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Contracts;
using UnityEngine;

namespace KSPHarness
{
    static class CmdGame
    {
        static string Root => KSPUtil.ApplicationRootPath;
        static string SavesDir => Path.Combine(Root, "saves");

        static void RequireGame()
        {
            if (HighLogic.CurrentGame == null) throw new HarnessException("no game loaded (use new_game or load_game)");
        }

        public static Dictionary<string, object> GameInfo()
        {
            var g = HighLogic.CurrentGame;
            if (g == null) return null;
            var d = new Dictionary<string, object>
            {
                ["title"] = g.Title,
                ["save_folder"] = HighLogic.SaveFolder,
                ["mode"] = g.Mode.ToString(),
            };
            if (Funding.Instance != null) d["funds"] = Funding.Instance.Funds;
            if (ResearchAndDevelopment.Instance != null) d["science"] = ResearchAndDevelopment.Instance.Science;
            if (Reputation.Instance != null) d["reputation"] = Reputation.Instance.reputation;
            return d;
        }

        [Cmd("status", "Overview: scene, game, active vessel summary, warp, autopilot.")]
        static object Status(Args a)
        {
            var d = new Dictionary<string, object>
            {
                ["scene"] = HighLogic.LoadedScene.ToString(),
                ["game"] = GameInfo(),
                ["event_seq"] = EventLog.Seq,
            };
            if (Planetarium.fetch != null) d["ut"] = Planetarium.GetUniversalTime();
            if (TimeWarp.fetch != null) d["warp"] = new Dictionary<string, object> { ["index"] = TimeWarp.CurrentRateIndex, ["rate"] = TimeWarp.CurrentRate, ["mode"] = TimeWarp.WarpMode.ToString() };
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ActiveVessel != null)
            {
                d["paused"] = FlightDriver.Pause;
                d["vessel"] = CmdFlight.Summary(FlightGlobals.ActiveVessel);
                d["autopilot"] = CmdFlight.ApStatus(null);
            }
            d["dialogs"] = Dialogs().Select(p => p["title"]).ToList();
            return d;
        }

        // ------------------------------------------------------------------ saves

        [Cmd("saves", "List save folders and their .sfs files.")]
        static object Saves(Args a)
        {
            var res = new List<object>();
            foreach (var dir in Directory.GetDirectories(SavesDir))
            {
                var name = Path.GetFileName(dir);
                if (name == "scenarios" || name == "training") continue;
                var files = Directory.GetFiles(dir, "*.sfs").Select(f => Path.GetFileNameWithoutExtension(f)).ToList();
                res.Add(new Dictionary<string, object> { ["name"] = name, ["files"] = files });
            }
            return res;
        }

        [Cmd("new_game", "Create and start a new game: {name, mode=sandbox|science|career, difficulty=normal|easy|moderate|hard}")]
        static object NewGame(Args a)
        {
            var name = a.ReqStr("name");
            if (Directory.Exists(Path.Combine(SavesDir, name)) && !a.Bool("overwrite"))
                throw new HarnessException("save '" + name + "' already exists (pass overwrite=true to replace)");
            Game.Modes mode;
            switch (a.Str("mode", "sandbox").ToLowerInvariant())
            {
                case "sandbox": mode = Game.Modes.SANDBOX; break;
                case "science": mode = Game.Modes.SCIENCE_SANDBOX; break;
                case "career": mode = Game.Modes.CAREER; break;
                default: throw new HarnessException("mode must be sandbox|science|career");
            }
            var preset = (GameParameters.Preset)Enum.Parse(typeof(GameParameters.Preset), a.Str("difficulty", "normal"), true);
            var parms = GameParameters.GetDefaultParameters(mode, preset);
            Directory.CreateDirectory(Path.Combine(SavesDir, name));
            var game = GamePersistence.CreateNewGame(name, mode, parms, "Squad/Flags/default", GameScenes.SPACECENTER, EditorFacility.None);
            HighLogic.CurrentGame = game;
            HighLogic.SaveFolder = name;
            GamePersistence.SaveGame(game, "persistent", name, SaveMode.OVERWRITE);
            game.Start();
            return "starting new " + mode + " game '" + name + "'";
        }

        [Cmd("load_game", "Load a save: {save, file=persistent}. Opens at the Space Center (or in flight if the save was made in flight and flight=true).")]
        static object LoadGame(Args a)
        {
            var save = a.ReqStr("save");
            var file = a.Str("file", "persistent");
            var game = GamePersistence.LoadGame(file, save, true, false);
            if (game == null) throw new HarnessException("could not load " + save + "/" + file + ".sfs");
            HighLogic.CurrentGame = game;
            HighLogic.SaveFolder = save;
            if (a.Bool("flight") && game.startScene == GameScenes.FLIGHT && game.flightState != null)
            {
                FlightDriver.StartAndFocusVessel(game, game.flightState.activeVesselIdx);
                return "loading " + save + "/" + file + " into flight";
            }
            game.startScene = GameScenes.SPACECENTER;
            game.Start();
            return "loading " + save + "/" + file;
        }

        [Cmd("save", "Save the current game: {file=persistent}")]
        static object Save(Args a)
        {
            RequireGame();
            var file = a.Str("file", "persistent");
            if (HighLogic.LoadedSceneIsFlight && FlightGlobals.ClearToSave() != ClearToSaveStatus.CLEAR && !a.Bool("force"))
                throw new HarnessException("not clear to save: " + FlightGlobals.ClearToSave() + "; pass force=true");
            var path = GamePersistence.SaveGame(file, HighLogic.SaveFolder, SaveMode.OVERWRITE);
            return path;
        }

        [Cmd("quicksave", "Quicksave (file 'quicksave').")]
        static object QuickSave(Args a)
        {
            RequireGame();
            return GamePersistence.SaveGame("quicksave", HighLogic.SaveFolder, SaveMode.OVERWRITE);
        }

        [Cmd("quickload", "Load the quicksave into flight: {file=quicksave}")]
        static object QuickLoad(Args a)
        {
            RequireGame();
            var file = a.Str("file", "quicksave");
            var game = GamePersistence.LoadGame(file, HighLogic.SaveFolder, true, false);
            if (game == null) throw new HarnessException("no " + file + ".sfs");
            if (game.flightState != null && game.startScene == GameScenes.FLIGHT)
            {
                HighLogic.CurrentGame = game;
                FlightDriver.StartAndFocusVessel(game, game.flightState.activeVesselIdx);
            }
            else
            {
                HighLogic.CurrentGame = game;
                game.Start();
            }
            return "loading " + file;
        }

        // ------------------------------------------------------------------ scenes

        [Cmd("scene", "Switch scene: {name=spacecenter|vab|sph|tracking|mainmenu, force?}. Leaving flight saves 'persistent'.")]
        static object Scene(Args a)
        {
            var name = a.ReqStr("name").ToLowerInvariant();
            if (name != "mainmenu") RequireGame();
            if (HighLogic.LoadedSceneIsFlight && name != "mainmenu")
            {
                var clear = FlightGlobals.ClearToSave();
                if (clear != ClearToSaveStatus.CLEAR && !a.Bool("force"))
                    throw new HarnessException("can't leave flight now: " + clear + " (pass force=true to leave anyway)");
                GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            }
            Autopilot.Reset();
            switch (name)
            {
                case "spacecenter": case "ksc": HighLogic.LoadScene(GameScenes.SPACECENTER); break;
                case "vab": EditorDriver.StartEditor(EditorFacility.VAB); break;
                case "sph": EditorDriver.StartEditor(EditorFacility.SPH); break;
                case "tracking": case "trackingstation": HighLogic.LoadScene(GameScenes.TRACKSTATION); break;
                case "mainmenu": HighLogic.LoadScene(GameScenes.MAINMENU); break;
                default: throw new HarnessException("unknown scene " + name);
            }
            return "loading " + name;
        }

        // ------------------------------------------------------------------ craft & launch

        static IEnumerable<string> CraftDirs(string facility)
        {
            var facs = facility == null ? new[] { "VAB", "SPH" } : new[] { facility.ToUpperInvariant() };
            foreach (var f in facs)
            {
                if (HighLogic.SaveFolder != null) yield return Path.Combine(Path.Combine(Path.Combine(SavesDir, HighLogic.SaveFolder), "Ships"), f);
                yield return Path.Combine(Path.Combine(Root, "Ships"), f);
            }
        }

        [Cmd("crafts", "List craft files available: {facility?=VAB|SPH}")]
        static object Crafts(Args a)
        {
            var res = new List<object>();
            foreach (var dir in CraftDirs(a.Str("facility")))
            {
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*.craft"))
                    res.Add(new Dictionary<string, object>
                    {
                        ["name"] = Path.GetFileNameWithoutExtension(f),
                        ["facility"] = Path.GetFileName(dir),
                        ["stock"] = !dir.Contains(Path.DirectorySeparatorChar + "saves" + Path.DirectorySeparatorChar),
                        ["path"] = f,
                    });
            }
            return res;
        }

        public static string ResolveCraft(string craft, string facility, out string fac)
        {
            fac = facility?.ToUpperInvariant();
            if (File.Exists(craft))
            {
                if (fac == null) fac = craft.Contains("/SPH/") ? "SPH" : "VAB";
                return Path.GetFullPath(craft);
            }
            foreach (var dir in CraftDirs(facility))
            {
                var p = Path.Combine(dir, craft.EndsWith(".craft") ? craft : craft + ".craft");
                if (File.Exists(p)) { fac = Path.GetFileName(dir); return p; }
            }
            throw new HarnessException("craft '" + craft + "' not found (see 'crafts')");
        }

        [Cmd("craft_check", "Validate a craft file for launch in the current game: {craft, facility?}. Reports missing/locked parts and cost.")]
        static object CraftCheck(Args a)
        {
            RequireGame();
            var path = ResolveCraft(a.ReqStr("craft"), a.Str("facility"), out var fac);
            return CheckCraft(path);
        }

        static Dictionary<string, object> CheckCraft(string path)
        {
            var node = ConfigNode.Load(path);
            var problems = new List<object>();
            string err = "";
            if (!ShipConstruction.AllPartsFound(node, ref err)) problems.Add("missing parts: " + err);
            double cost = 0, dryCost = 0;
            int count = 0;
            var locked = new HashSet<string>();
            bool checkTech = HighLogic.CurrentGame.Mode == Game.Modes.CAREER || HighLogic.CurrentGame.Mode == Game.Modes.SCIENCE_SANDBOX;
            foreach (var pn in node.GetNodes("PART"))
            {
                count++;
                var pname = pn.GetValue("part");
                if (pname == null) continue;
                pname = pname.Split('_')[0];
                var ap = PartLoader.getPartInfoByName(pname.Replace('.', '_')) ?? PartLoader.getPartInfoByName(pname);
                if (ap == null) continue;
                cost += ap.cost;
                if (checkTech && !(ResearchAndDevelopment.PartTechAvailable(ap) && ResearchAndDevelopment.PartModelPurchased(ap)))
                    locked.Add(ap.name + " (" + ap.TechRequired + ")");
            }
            if (locked.Count > 0) problems.Add("parts not unlocked/purchased: " + string.Join(", ", locked));
            return new Dictionary<string, object>
            {
                ["path"] = path,
                ["ship_name"] = node.GetValue("ship"),
                ["parts"] = count,
                ["approx_cost"] = cost,
                ["ok"] = problems.Count == 0,
                ["problems"] = problems,
            };
        }

        [Cmd("launch", "Launch a craft: {craft (name or path), facility?=VAB|SPH, site?=LaunchPad|Runway|..., crew?:[names], ignore_checks?}")]
        static object Launch(Args a)
        {
            RequireGame();
            if (HighLogic.LoadedScene != GameScenes.SPACECENTER && HighLogic.LoadedScene != GameScenes.EDITOR && HighLogic.LoadedScene != GameScenes.TRACKSTATION && !HighLogic.LoadedSceneIsFlight)
                throw new HarnessException("must be at the space center, editor, tracking station or in flight");
            var path = ResolveCraft(a.ReqStr("craft"), a.Str("facility"), out var fac);
            var check = CheckCraft(path);
            if (!(bool)check["ok"] && !a.Bool("ignore_checks"))
                throw new HarnessException("craft check failed: " + string.Join("; ", ((List<object>)check["problems"]).Select(x => x.ToString())));
            if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER)
            {
                double cost = (double)check["approx_cost"];
                if (Funding.Instance.Funds < cost && !a.Bool("ignore_checks"))
                    throw new HarnessException("insufficient funds: need " + cost + ", have " + Funding.Instance.Funds);
                Funding.Instance.AddFunds(-cost, TransactionReasons.VesselRollout);
            }
            var node = ConfigNode.Load(path);
            var manifest = VesselCrewManifest.FromConfigNode(node);
            var roster = HighLogic.CurrentGame.CrewRoster;
            var crew = a.List("crew");
            if (crew != null)
            {
                var names = new Queue<string>(crew.Select(c => c.ToString()));
                foreach (var pcm in manifest.GetCrewableParts())
                    for (int i = 0; i < pcm.partCrew.Length && names.Count > 0; i++)
                    {
                        var k = roster[names.Dequeue()];
                        if (k == null) throw new HarnessException("no such kerbal");
                        pcm.AddCrewToSeat(k, i);
                    }
            }
            else manifest = roster.DefaultCrewForVessel(node, manifest, true, true);

            var site = a.Str("site", fac == "SPH" ? "Runway" : "LaunchPad");
            Autopilot.Reset();
            FlightDriver.StartWithNewLaunch(path, HighLogic.CurrentGame.flagURL, site, manifest);
            return new Dictionary<string, object> { ["launching"] = path, ["site"] = site, ["crew"] = manifest.GetAllCrew(false).Where(c => c != null).Select(c => c.name).ToList() };
        }

        [Cmd("recover", "Recover the active vessel (must be landed/splashed or pre-launch).")]
        static object Recover(Args a)
        {
            var v = CmdFlight.RequireVessel();
            if (!v.IsRecoverable && !a.Bool("force")) throw new HarnessException("vessel is not recoverable in situation " + v.situation);
            GameEvents.OnVesselRecoveryRequested.Fire(v);
            return "recovery requested for " + v.vesselName;
        }

        [Cmd("revert", "Revert flight: {to=launch|editor}")]
        static object Revert(Args a)
        {
            var to = a.Str("to", "launch");
            Autopilot.Reset();
            if (to == "launch")
            {
                if (!FlightDriver.CanRevertToPostInit) throw new HarnessException("cannot revert to launch");
                FlightDriver.RevertToLaunch();
            }
            else
            {
                if (!FlightDriver.CanRevertToPrelaunch) throw new HarnessException("cannot revert to editor");
                FlightDriver.RevertToPrelaunch(ShipConstruction.ShipType == EditorFacility.SPH ? EditorFacility.SPH : EditorFacility.VAB);
            }
            return "reverting to " + to;
        }

        // ------------------------------------------------------------------ dialogs

        static void CollectButtons(DialogGUIBase b, List<DialogGUIButton> outList)
        {
            if (b == null) return;
            if (b is DialogGUIButton btn) outList.Add(btn);
            if (b.children != null) foreach (var c in b.children) CollectButtons(c, outList);
        }

        static List<Dictionary<string, object>> Dialogs()
        {
            var res = new List<Dictionary<string, object>>();
            int i = 0;
            foreach (var pd in UnityEngine.Object.FindObjectsOfType<PopupDialog>())
            {
                var md = pd.dialogToDisplay;
                var buttons = new List<DialogGUIButton>();
                if (md?.Options != null) foreach (var o in md.Options) CollectButtons(o, buttons);
                res.Add(new Dictionary<string, object>
                {
                    ["index"] = i++,
                    ["name"] = md?.name,
                    ["title"] = md?.title,
                    ["message"] = md?.message,
                    ["buttons"] = buttons.Select(b => b.OptionText).ToList(),
                    ["_obj"] = pd,
                    ["_buttons"] = buttons,
                });
            }
            // Non-PopupDialog modal windows (e.g. the "Continue Saved Game" browser opened by holding F9).
            foreach (var w in UnityEngine.Object.FindObjectsOfType<LoadGameDialog>())
                res.Add(new Dictionary<string, object>
                {
                    ["index"] = i++,
                    ["name"] = "LoadGameDialog",
                    ["title"] = "Continue Saved Game",
                    ["message"] = "",
                    ["buttons"] = new List<object> { "Cancel" },
                    ["_obj"] = w,
                });
            return res;
        }

        /// <summary>Close a non-PopupDialog window: call its private cancel handler if any, else destroy it.</summary>
        static string CloseWindow(MonoBehaviour w)
        {
            var m = w.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                .FirstOrDefault(x => x.Name.IndexOf("Cancel", StringComparison.OrdinalIgnoreCase) >= 0 && x.GetParameters().Length == 0);
            if (m != null) { m.Invoke(w, null); return "cancelled via " + m.Name; }
            UnityEngine.Object.Destroy(w.gameObject);
            return "destroyed window";
        }

        [Cmd("dialogs", "List open popup dialogs and their buttons.")]
        static object ListDialogs(Args a) =>
            Dialogs().Select(d => d.Where(kv => !kv.Key.StartsWith("_")).ToDictionary(kv => kv.Key, kv => kv.Value)).ToList();

        [Cmd("dialog_click", "Click a dialog button: {button (text, case-insensitive substring), index?=dialog index}. With no button: dismiss.")]
        static object DialogClick(Args a)
        {
            var all = Dialogs();
            if (all.Count == 0) throw new HarnessException("no dialogs open");
            var d = all[(int)a.Num("index", 0)];
            if (d["_obj"] is LoadGameDialog lgd) return CloseWindow(lgd);
            var pd = (PopupDialog)d["_obj"];
            var btext = a.Str("button");
            if (btext == null) { pd.Dismiss(); return "dismissed"; }
            var btn = ((List<DialogGUIButton>)d["_buttons"]).FirstOrDefault(b => b.OptionText != null && b.OptionText.IndexOf(btext, StringComparison.OrdinalIgnoreCase) >= 0);
            if (btn == null) throw new HarnessException("no button matching '" + btext + "'");
            btn.OptionSelected();
            if (btn.DismissOnSelect && pd != null) pd.Dismiss();
            return "clicked " + btn.OptionText;
        }

        // ------------------------------------------------------------------ time

        [Cmd("warp", "Set time warp: {index} (0=1x), or {to_ut} / {seconds} to warp to a time; {physics?:true} uses physics warp.")]
        static object Warp(Args a)
        {
            if (TimeWarp.fetch == null) throw new HarnessException("no time warp in this scene");
            if (a.Has("to_ut") || a.Has("seconds"))
            {
                double ut = a.Has("to_ut") ? a.Num("to_ut") : Planetarium.GetUniversalTime() + a.Num("seconds");
                TimeWarp.fetch.WarpTo(ut);
                return new Dictionary<string, object> { ["warping_to"] = ut };
            }
            TimeWarp.fetch.CancelAutoWarp();
            TimeWarp.fetch.Mode = a.Bool("physics") ? TimeWarp.Modes.LOW : TimeWarp.Modes.HIGH;
            TimeWarp.SetRate((int)a.Num("index", 0), a.Bool("instant", false), true);
            return new Dictionary<string, object> { ["index"] = TimeWarp.CurrentRateIndex, ["rates"] = (a.Bool("physics") ? TimeWarp.fetch.physicsWarpRates : TimeWarp.fetch.warpRates).ToList() };
        }

        [Cmd("pause", "Pause/unpause flight: {on=true}")]
        static object Pause(Args a)
        {
            FlightDriver.SetPause(a.Bool("on", true), true);
            return FlightDriver.Pause;
        }

        // ------------------------------------------------------------------ vessels & targets

        [Cmd("vessels", "List vessels in the game: {type?} (e.g. Ship, Probe, Debris, SpaceObject)")]
        static object Vessels(Args a)
        {
            RequireGame();
            var type = a.Str("type");
            var res = new List<object>();
            if (HighLogic.LoadedSceneIsFlight || HighLogic.LoadedScene == GameScenes.TRACKSTATION)
            {
                var av = FlightGlobals.ActiveVessel;
                foreach (var v in FlightGlobals.Vessels)
                {
                    if (type != null && !v.vesselType.ToString().Equals(type, StringComparison.OrdinalIgnoreCase)) continue;
                    var d = new Dictionary<string, object>
                    {
                        ["id"] = v.id.ToString(),
                        ["name"] = v.vesselName,
                        ["type"] = v.vesselType.ToString(),
                        ["situation"] = v.situation.ToString(),
                        ["body"] = v.mainBody.bodyName,
                        ["loaded"] = v.loaded,
                        ["active"] = v == av,
                    };
                    if (av != null && v != av) d["distance"] = (v.GetWorldPos3D() - av.GetWorldPos3D()).magnitude;
                    res.Add(d);
                }
            }
            else
            {
                foreach (var pv in HighLogic.CurrentGame.flightState.protoVessels)
                {
                    if (type != null && !pv.vesselType.ToString().Equals(type, StringComparison.OrdinalIgnoreCase)) continue;
                    res.Add(new Dictionary<string, object>
                    {
                        ["id"] = pv.vesselID.ToString(),
                        ["name"] = pv.vesselName,
                        ["type"] = pv.vesselType.ToString(),
                        ["situation"] = pv.situation.ToString(),
                        ["body"] = FlightGlobals.Bodies[pv.orbitSnapShot.ReferenceBodyIndex].bodyName,
                    });
                }
            }
            return res;
        }

        static int ProtoIndex(Func<ProtoVessel, bool> pred)
        {
            var pvs = HighLogic.CurrentGame.flightState.protoVessels;
            for (int i = 0; i < pvs.Count; i++) if (pred(pvs[i])) return i;
            return -1;
        }

        [Cmd("switch_vessel", "Switch to / fly a vessel: {id or name}. Works from flight, space center and tracking station.")]
        static object SwitchVessel(Args a)
        {
            RequireGame();
            var key = a.ReqStr("id");
            Func<string, string, bool> match = (id, name) => id == key || name.Equals(key, StringComparison.OrdinalIgnoreCase);
            if (HighLogic.LoadedSceneIsFlight)
            {
                var v = FlightGlobals.Vessels.FirstOrDefault(x => match(x.id.ToString(), x.vesselName));
                if (v == null) throw new HarnessException("no such vessel");
                if (v.loaded) { FlightGlobals.SetActiveVessel(v); return "switched to " + v.vesselName; }
                GamePersistence.SaveGame("persistent", HighLogic.SaveFolder, SaveMode.OVERWRITE);
            }
            var game = GamePersistence.LoadGame("persistent", HighLogic.SaveFolder, true, false);
            HighLogic.CurrentGame = game;
            var pvs = game.flightState.protoVessels;
            int idx = -1;
            for (int i = 0; i < pvs.Count; i++) if (match(pvs[i].vesselID.ToString(), pvs[i].vesselName)) { idx = i; break; }
            if (idx < 0) throw new HarnessException("no such vessel");
            Autopilot.Reset();
            FlightDriver.StartAndFocusVessel(game, idx);
            return "loading flight for " + pvs[idx].vesselName;
        }

        [Cmd("target", "Set target: {name (body or vessel name/id)} or {clear:true}")]
        static object Target(Args a)
        {
            if (!HighLogic.LoadedSceneIsFlight) throw new HarnessException("not in flight");
            if (a.Bool("clear")) { FlightGlobals.fetch.SetVesselTarget(null); return "cleared"; }
            var name = a.ReqStr("name");
            ITargetable t = FlightGlobals.Bodies.FirstOrDefault(b => b.bodyName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (t == null) t = FlightGlobals.Vessels.FirstOrDefault(v => v.id.ToString() == name || v.vesselName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (t == null) throw new HarnessException("no body or vessel named " + name);
            FlightGlobals.fetch.SetVesselTarget(t);
            return "target set to " + t.GetName();
        }

        [Cmd("bodies", "Celestial body data: {name?}")]
        static object Bodies(Args a)
        {
            var name = a.Str("name");
            return FlightGlobals.Bodies.Where(b => name == null || b.bodyName.Equals(name, StringComparison.OrdinalIgnoreCase)).Select(b =>
            {
                var d = new Dictionary<string, object>
                {
                    ["name"] = b.bodyName,
                    ["radius"] = b.Radius,
                    ["mu"] = b.gravParameter,
                    ["surface_gravity"] = b.GeeASL * 9.80665,
                    ["soi"] = b.sphereOfInfluence,
                    ["rotation_period"] = b.rotationPeriod,
                    ["atmosphere"] = b.atmosphere,
                    ["atmosphere_depth"] = b.atmosphere ? b.atmosphereDepth : 0,
                    ["has_ocean"] = b.ocean,
                    ["parent"] = b.referenceBody != null && b.referenceBody != b ? b.referenceBody.bodyName : null,
                    ["satellites"] = b.orbitingBodies.Select(x => x.bodyName).ToList(),
                };
                if (b.orbit != null && b.referenceBody != b)
                    d["orbit"] = CmdFlight.OrbitInfo(b.orbit);
                return d;
            }).ToList();
        }

        // ------------------------------------------------------------------ career: contracts, tech, facilities

        [Cmd("contracts", "List contracts: {state?=Offered|Active|Completed}")]
        static object ContractsList(Args a)
        {
            if (ContractSystem.Instance == null) throw new HarnessException("no contract system (sandbox or not at KSC?)");
            var state = a.Str("state");
            var list = ContractSystem.Instance.Contracts.AsEnumerable();
            if (state == "Completed") list = ContractSystem.Instance.ContractsFinished;
            else if (state != null) list = list.Where(c => c.ContractState.ToString().Equals(state, StringComparison.OrdinalIgnoreCase));
            return list.Select(c => new Dictionary<string, object>
            {
                ["id"] = c.ContractID,
                ["title"] = c.Title,
                ["state"] = c.ContractState.ToString(),
                ["prestige"] = c.Prestige.ToString(),
                ["funds_advance"] = c.FundsAdvance,
                ["funds_completion"] = c.FundsCompletion,
                ["science"] = c.ScienceCompletion,
                ["reputation"] = c.ReputationCompletion,
                ["deadline_ut"] = c.DateDeadline,
                ["parameters"] = c.AllParameters.Select(p => new Dictionary<string, object> { ["title"] = p.Title, ["state"] = p.State.ToString(), ["optional"] = p.Optional }).ToList(),
                ["synopsis"] = a.Bool("verbose") ? c.Synopsys : null,
                ["notes"] = a.Bool("verbose") ? c.Notes : null,
            }).ToList();
        }

        static Contract FindContract(Args a)
        {
            var id = (long)a.ReqNum("id");
            var c = ContractSystem.Instance?.Contracts.FirstOrDefault(x => x.ContractID == id);
            if (c == null) throw new HarnessException("no contract " + id);
            return c;
        }

        [Cmd("contract_accept", "Accept an offered contract: {id}")]
        static object ContractAccept(Args a) => FindContract(a).Accept();

        [Cmd("contract_decline", "Decline an offered contract: {id}")]
        static object ContractDecline(Args a) => FindContract(a).Decline();

        [Cmd("contract_cancel", "Cancel an active contract: {id}")]
        static object ContractCancel(Args a) => FindContract(a).Cancel();

        static ConfigNode TechTree()
        {
            var url = HighLogic.CurrentGame.Parameters.Career.TechTreeUrl;
            var cfg = GameDatabase.Instance.GetConfigNodes("TechTree").FirstOrDefault();
            return cfg;
        }

        [Cmd("tech", "Tech tree: {available_only?}. Lists nodes with cost, state, prerequisites and parts.")]
        static object Tech(Args a)
        {
            RequireGame();
            if (ResearchAndDevelopment.Instance == null) throw new HarnessException("no R&D in this game mode");
            var tree = TechTree();
            var res = new List<object>();
            var researched = new HashSet<string>();
            foreach (var n in tree.GetNodes("RDNode"))
            {
                var id = n.GetValue("id");
                if (ResearchAndDevelopment.GetTechnologyState(id) == RDTech.State.Available) researched.Add(id);
            }
            foreach (var n in tree.GetNodes("RDNode"))
            {
                var id = n.GetValue("id");
                var parents = n.GetNodes("Parent").Select(p => p.GetValue("parentID")).ToList();
                bool anyToUnlock = n.GetValue("anyToUnlock") == "True";
                bool done = researched.Contains(id);
                bool canResearch = !done && (parents.Count == 0 || (anyToUnlock ? parents.Any(researched.Contains) : parents.All(researched.Contains)));
                if (a.Bool("available_only") && !canResearch) continue;
                res.Add(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["title"] = n.GetValue("title"),
                    ["cost"] = double.Parse(n.GetValue("cost")),
                    ["researched"] = done,
                    ["can_research"] = canResearch,
                    ["parents"] = parents,
                    ["any_parent"] = anyToUnlock,
                    ["parts"] = PartLoader.LoadedPartsList.Where(p => p.TechRequired == id && !p.TechHidden).Select(p => p.name).ToList(),
                });
            }
            return res;
        }

        [Cmd("tech_research", "Research a tech node, spending science: {id, purchase_parts=true}")]
        static object TechResearch(Args a)
        {
            RequireGame();
            var id = a.ReqStr("id");
            var n = TechTree().GetNodes("RDNode").FirstOrDefault(x => x.GetValue("id") == id) ?? throw new HarnessException("no tech " + id);
            if (ResearchAndDevelopment.GetTechnologyState(id) == RDTech.State.Available) throw new HarnessException("already researched");
            var cost = int.Parse(n.GetValue("cost"));
            if (ResearchAndDevelopment.Instance.Science < cost) throw new HarnessException("need " + cost + " science, have " + ResearchAndDevelopment.Instance.Science);
            var parts = PartLoader.LoadedPartsList.Where(p => p.TechRequired == id).ToList();
            var ptn = new ProtoTechNode { techID = id, state = RDTech.State.Available, scienceCost = cost, partsPurchased = new List<AvailablePart>() };
            bool bypass = HighLogic.CurrentGame.Parameters.Difficulty.BypassEntryPurchaseAfterResearch;
            if (a.Bool("purchase_parts", true) || bypass || HighLogic.CurrentGame.Mode != Game.Modes.CAREER) ptn.partsPurchased.AddRange(parts);
            ResearchAndDevelopment.Instance.AddScience(-cost, TransactionReasons.RnDTechResearch);
            ResearchAndDevelopment.Instance.UnlockProtoTechNode(ptn);
            if (HighLogic.CurrentGame.Mode == Game.Modes.CAREER && !bypass && a.Bool("purchase_parts", true))
            {
                double entry = parts.Sum(p => p.entryCost);
                Funding.Instance.AddFunds(-entry, TransactionReasons.RnDPartPurchase);
            }
            return new Dictionary<string, object> { ["researched"] = id, ["science_left"] = ResearchAndDevelopment.Instance.Science, ["parts"] = parts.Select(p => p.name).ToList() };
        }

        [Cmd("facilities", "Space center facility levels and upgrade costs.")]
        static object Facilities(Args a)
        {
            var res = new List<object>();
            foreach (var kv in ScenarioUpgradeableFacilities.protoUpgradeables)
            {
                var f = kv.Value.facilityRefs.FirstOrDefault();
                res.Add(new Dictionary<string, object>
                {
                    ["id"] = kv.Key,
                    ["level"] = f != null ? f.FacilityLevel : -1,
                    ["max_level"] = f != null ? f.MaxLevel : -1,
                    ["upgrade_cost"] = f != null && f.FacilityLevel < f.MaxLevel ? (object)f.GetUpgradeCost() : null,
                });
            }
            return res;
        }

        [Cmd("facility_upgrade", "Upgrade a facility one level, paying funds: {id} (from 'facilities'; must be at space center)")]
        static object FacilityUpgrade(Args a)
        {
            var id = a.ReqStr("id");
            if (!ScenarioUpgradeableFacilities.protoUpgradeables.TryGetValue(id, out var pu)) throw new HarnessException("no facility " + id);
            var f = pu.facilityRefs.FirstOrDefault() ?? throw new HarnessException("facility not loaded (go to the space center)");
            if (f.FacilityLevel >= f.MaxLevel) throw new HarnessException("already max level");
            var cost = f.GetUpgradeCost();
            if (Funding.Instance != null)
            {
                if (Funding.Instance.Funds < cost) throw new HarnessException("need " + cost + " funds");
                Funding.Instance.AddFunds(-cost, TransactionReasons.StructureConstruction);
            }
            f.SetLevel(f.FacilityLevel + 1);
            return new Dictionary<string, object> { ["id"] = id, ["level"] = f.FacilityLevel, ["cost"] = cost };
        }

        [Cmd("crew", "List the kerbal roster.")]
        static object Crew(Args a)
        {
            RequireGame();
            return HighLogic.CurrentGame.CrewRoster.Crew.Select(k => new Dictionary<string, object>
            {
                ["name"] = k.name,
                ["trait"] = k.trait,
                ["level"] = k.experienceLevel,
                ["status"] = k.rosterStatus.ToString(),
                ["courage"] = k.courage,
                ["stupidity"] = k.stupidity,
            }).ToList();
        }
    }
}
