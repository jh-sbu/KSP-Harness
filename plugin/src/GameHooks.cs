using System.Collections.Generic;

namespace KSPHarness
{
    /// <summary>Subscribes to KSP GameEvents and records them in the EventLog.</summary>
    static class GameHooks
    {
        static bool installed;

        static string VName(Vessel v) => v != null ? v.vesselName : "?";

        public static void Install()
        {
            if (installed) return;
            installed = true;

            GameEvents.onGameSceneLoadRequested.Add(s => EventLog.Add("scene.requested", s.ToString()));
            GameEvents.onLevelWasLoadedGUIReady.Add(s => EventLog.Add("scene.loaded", s.ToString()));
            GameEvents.onFlightReady.Add(() => EventLog.Add("flight.ready", VName(FlightGlobals.ActiveVessel)));
            GameEvents.onVesselChange.Add(v => { EventLog.Add("vessel.switched", VName(v)); Autopilot.OnVesselChange(v); });
            GameEvents.onStageActivate.Add(s => EventLog.Add("vessel.staged", "stage " + s));
            GameEvents.onCrash.Add(r => EventLog.Add("vessel.crash", r.msg ?? (r.origin != null ? r.origin.partInfo.title : "")));
            GameEvents.onCrashSplashdown.Add(r => EventLog.Add("vessel.crash_splashdown", r.msg ?? ""));
            GameEvents.onCrewKilled.Add(r => EventLog.Add("crew.killed", r.sender ?? r.msg ?? ""));
            GameEvents.onPartDie.Add(p =>
            {
                if (p != null && p.vessel != null && p.vessel.loaded)
                    EventLog.Add("part.destroyed", p.partInfo != null ? p.partInfo.title : p.name, new Dictionary<string, object> { ["vessel"] = VName(p.vessel) });
            });
            GameEvents.onVesselSituationChange.Add(e => EventLog.Add("vessel.situation", VName(e.host) + ": " + e.from + " -> " + e.to));
            GameEvents.onVesselSOIChanged.Add(e => EventLog.Add("vessel.soi", VName(e.host) + ": " + e.from.bodyName + " -> " + e.to.bodyName));
            GameEvents.onVesselRecovered.Add((pv, quick) => EventLog.Add("vessel.recovered", pv != null ? pv.vesselName : "?"));
            GameEvents.onVesselTerminated.Add(pv => EventLog.Add("vessel.terminated", pv != null ? pv.vesselName : "?"));
            GameEvents.OnScienceRecieved.Add((amt, subj, pv, rev) => EventLog.Add("science.received", subj.title + ": +" + amt.ToString("F1")));
            GameEvents.OnTechnologyResearched.Add(e => EventLog.Add("tech.researched", e.host.techID + " " + e.target));
            GameEvents.onFlagPlant.Add(v => EventLog.Add("flag.planted", VName(v)));
            GameEvents.onKerbalLevelUp.Add(k => EventLog.Add("crew.levelup", k.name + " -> " + k.experienceLevel));
            GameEvents.OnProgressComplete.Add(n => EventLog.Add("progress", n.Id));
            GameEvents.Contract.onCompleted.Add(c => EventLog.Add("contract.completed", c.Title));
            GameEvents.Contract.onFailed.Add(c => EventLog.Add("contract.failed", c.Title));
            GameEvents.Contract.onAccepted.Add(c => EventLog.Add("contract.accepted", c.Title));
            GameEvents.onGamePause.Add(() => EventLog.Add("game.paused", ""));
            GameEvents.onGameUnpause.Add(() => EventLog.Add("game.unpaused", ""));
        }
    }
}
