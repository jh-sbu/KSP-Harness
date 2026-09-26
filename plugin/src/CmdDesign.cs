using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace KSPHarness
{
    /// <summary>Part catalogue queries used by the craft generator (python/kspharness/craft.py).</summary>
    static class CmdDesign
    {
        static double R(double x, int d = 4) => Math.Round(x, d);
        static List<object> V(Vector3 v) => new List<object> { R(v.x), R(v.y), R(v.z) };

        [Cmd("part_search", "Search loaded parts: {filter? (substring of name/title/category), category?}. Returns name, title, category, mass, cost.")]
        static object PartSearch(Args a)
        {
            var f = a.Str("filter");
            var cat = a.Str("category");
            return PartLoader.LoadedPartsList
                .Where(p => p.partPrefab != null && !p.TechHidden)
                .Where(p => f == null || p.name.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0 || p.title.IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(p => cat == null || p.category.ToString().Equals(cat, StringComparison.OrdinalIgnoreCase))
                .Select(p => (object)new Dictionary<string, object>
                {
                    ["name"] = p.name,
                    ["title"] = p.title,
                    ["category"] = p.category.ToString(),
                    ["mass"] = R(p.partPrefab.mass + p.partPrefab.GetResourceMass()),
                    ["dry_mass"] = R(p.partPrefab.mass),
                    ["cost"] = p.cost,
                }).ToList();
        }

        [Cmd("part_info", "Geometry and stats of parts for craft building: {names:[part names]}")]
        static object PartInfo(Args a)
        {
            var names = a.List("names")?.Select(x => x.ToString()).ToList() ?? new List<string> { a.ReqStr("name") };
            var res = new Dictionary<string, object>();
            foreach (var n in names)
            {
                var ap = PartLoader.getPartInfoByName(n) ?? throw new HarnessException("no part " + n);
                var p = ap.partPrefab;
                var d = new Dictionary<string, object>
                {
                    ["name"] = ap.name,
                    ["title"] = ap.title,
                    ["category"] = ap.category.ToString(),
                    ["dry_mass"] = R(p.mass),
                    ["wet_mass"] = R(p.mass + p.GetResourceMass()),
                    ["cost"] = ap.cost,
                    ["crew"] = p.CrewCapacity,
                    ["crash_tolerance"] = p.crashTolerance,
                    ["max_temp"] = p.maxTemp,
                    ["bulkhead"] = ap.bulkheadProfiles,
                    ["attach_rules"] = new Dictionary<string, object>
                    {
                        ["stack"] = p.attachRules.stack, ["srf_attach"] = p.attachRules.srfAttach, ["allow_srf"] = p.attachRules.allowSrfAttach,
                    },
                    ["nodes"] = p.attachNodes.Select(an => (object)new Dictionary<string, object>
                    {
                        ["id"] = an.id, ["pos"] = V(an.position), ["dir"] = V(an.orientation), ["size"] = an.size,
                    }).ToList(),
                    ["resources"] = p.Resources.Cast<PartResource>().ToDictionary(r => r.resourceName, r => (object)R(r.maxAmount, 2)),
                    ["modules"] = p.Modules.Cast<PartModule>().Select(m => m.moduleName).ToList(),
                };
                if (p.srfAttachNode != null)
                    d["srf_node"] = new Dictionary<string, object> { ["pos"] = V(p.srfAttachNode.position), ["dir"] = V(p.srfAttachNode.orientation) };
                var eng = p.Modules.OfType<ModuleEngines>().FirstOrDefault();
                if (eng != null)
                    d["engine"] = new Dictionary<string, object>
                    {
                        ["thrust_vac"] = eng.maxThrust,
                        ["isp_vac"] = eng.atmosphereCurve.Evaluate(0),
                        ["isp_1atm"] = eng.atmosphereCurve.Evaluate(1),
                        ["isp_5atm"] = eng.atmosphereCurve.Evaluate(5),
                        ["propellants"] = eng.propellants.Select(x => x.name).ToList(),
                        ["gimbal"] = p.Modules.OfType<ModuleGimbal>().Select(g => (object)g.gimbalRange).FirstOrDefault(),
                        ["alternators"] = p.Modules.OfType<ModuleAlternator>().Any(),
                    };
                var dec = p.Modules.OfType<ModuleDecouple>().FirstOrDefault();
                if (dec != null) d["decoupler"] = new Dictionary<string, object> { ["node"] = dec.explosiveNodeID, ["force"] = dec.ejectionForce };
                var chute = p.Modules.OfType<ModuleParachute>().FirstOrDefault();
                if (chute != null) d["parachute"] = new Dictionary<string, object> { ["deployed_drag"] = chute.fullyDeployedDrag, ["min_pressure"] = chute.minAirPressureToOpen, ["deploy_alt"] = chute.deployAltitude };
                res[n] = d;
            }
            return res;
        }
    }
}
