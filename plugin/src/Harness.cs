using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace KSPHarness
{
    /// <summary>Marks a static method `object M(Args a)` as a remotely callable command.</summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class CmdAttribute : Attribute
    {
        public readonly string Name;
        public readonly string Help;
        public CmdAttribute(string name, string help) { Name = name; Help = help; }
    }

    public class HarnessException : Exception
    {
        public HarnessException(string msg) : base(msg) { }
    }

    /// <summary>Typed accessors over a decoded JSON argument object.</summary>
    public class Args
    {
        readonly Dictionary<string, object> d;
        public Args(Dictionary<string, object> d) { this.d = d ?? new Dictionary<string, object>(); }
        public bool Has(string k) => d.ContainsKey(k) && d[k] != null;
        public object Raw(string k) => Has(k) ? d[k] : null;
        public IEnumerable<string> Keys => d.Keys;

        public string Str(string k, string def = null) => Has(k) ? Convert.ToString(d[k], CultureInfo.InvariantCulture) : def;
        public string ReqStr(string k) => Has(k) ? Str(k) : throw new HarnessException("missing argument '" + k + "'");
        public double Num(string k, double def = double.NaN)
        {
            if (!Has(k)) return def;
            var v = d[k];
            if (v is string s) return double.Parse(s, CultureInfo.InvariantCulture);
            if (v is bool b) return b ? 1 : 0;
            return Convert.ToDouble(v, CultureInfo.InvariantCulture);
        }
        public double ReqNum(string k) => Has(k) ? Num(k) : throw new HarnessException("missing argument '" + k + "'");
        public bool Bool(string k, bool def = false)
        {
            if (!Has(k)) return def;
            var v = d[k];
            if (v is bool b) return b;
            if (v is string s) return s == "1" || s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("on", StringComparison.OrdinalIgnoreCase);
            return Convert.ToDouble(v, CultureInfo.InvariantCulture) != 0;
        }
        public List<object> List(string k) => Has(k) ? d[k] as List<object> : null;
        public Dictionary<string, object> Obj(string k) => Has(k) ? d[k] as Dictionary<string, object> : null;
    }

    class Request
    {
        public string Line;
        public string Response;
        public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
    }

    /// <summary>Ring buffer of notable game events so a client can poll "what happened since seq N".</summary>
    public static class EventLog
    {
        static readonly List<Dictionary<string, object>> buf = new List<Dictionary<string, object>>();
        static long seq;
        const int Max = 500;

        public static void Add(string type, string msg, Dictionary<string, object> extra = null)
        {
            var e = new Dictionary<string, object>
            {
                ["seq"] = ++seq,
                ["type"] = type,
                ["msg"] = msg,
                ["ut"] = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0,
                ["real_time"] = DateTime.Now.ToString("HH:mm:ss"),
            };
            if (extra != null) foreach (var kv in extra) e[kv.Key] = kv.Value;
            lock (buf)
            {
                buf.Add(e);
                if (buf.Count > Max) buf.RemoveAt(0);
            }
        }

        public static long Seq => seq;

        public static List<Dictionary<string, object>> Since(long s, string typePrefix = null)
        {
            lock (buf) return buf.Where(e => (long)e["seq"] > s && (typePrefix == null || ((string)e["type"]).StartsWith(typePrefix))).ToList();
        }
    }

    [KSPAddon(KSPAddon.Startup.Instantly, true)]
    public class Harness : MonoBehaviour
    {
        public static Harness Instance;
        public const string Version = "0.1.0";

        static readonly Dictionary<string, MethodInfo> commands = new Dictionary<string, MethodInfo>();
        static readonly Dictionary<string, string> helps = new Dictionary<string, string>();
        readonly Queue<Request> queue = new Queue<Request>();
        TcpListener listener;
        Thread acceptThread;
        volatile bool running;
        int port;

        /// <summary>Actions to run once a condition becomes true (checked every frame).</summary>
        readonly List<KeyValuePair<Func<bool>, Action>> deferred = new List<KeyValuePair<Func<bool>, Action>>();

        public void Defer(Func<bool> when, Action what) => deferred.Add(new KeyValuePair<Func<bool>, Action>(when, what));

        void Awake()
        {
            if (Instance != null) { Destroy(gameObject); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Application.runInBackground = true;

            foreach (var t in Assembly.GetExecutingAssembly().GetTypes())
                foreach (var m in t.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    var a = m.GetCustomAttributes(typeof(CmdAttribute), false).FirstOrDefault() as CmdAttribute;
                    if (a == null) continue;
                    commands[a.Name] = m;
                    helps[a.Name] = a.Help;
                }

            GameHooks.Install();
            Application.logMessageReceived += OnUnityLog;

            port = 50555;
            var env = Environment.GetEnvironmentVariable("KSP_HARNESS_PORT");
            if (!string.IsNullOrEmpty(env)) int.TryParse(env, out port);
            running = true;
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "KSPHarness-accept" };
            acceptThread.Start();
            Debug.Log("[KSPHarness] v" + Version + " listening on 127.0.0.1:" + port + " with " + commands.Count + " commands");
            EventLog.Add("harness", "harness started");
        }

        void OnUnityLog(string condition, string stack, LogType type)
        {
            if (type == LogType.Exception || type == LogType.Error)
                EventLog.Add("log.error", condition.Length > 500 ? condition.Substring(0, 500) : condition);
        }

        void OnDestroy()
        {
            if (Instance != this) return;
            running = false;
            try { listener?.Stop(); } catch { }
        }

        void AcceptLoop()
        {
            while (running)
            {
                TcpClient c;
                try { c = listener.AcceptTcpClient(); }
                catch { if (!running) return; continue; }
                var th = new Thread(() => ClientLoop(c)) { IsBackground = true, Name = "KSPHarness-client" };
                th.Start();
            }
        }

        void ClientLoop(TcpClient c)
        {
            try
            {
                c.NoDelay = true;
                using (var stream = c.GetStream())
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" })
                {
                    string line;
                    while (running && (line = reader.ReadLine()) != null)
                    {
                        if (line.Trim().Length == 0) continue;
                        var req = new Request { Line = line };
                        lock (queue) queue.Enqueue(req);
                        if (!req.Done.Wait(60000))
                            req.Response = "{\"ok\":false,\"error\":\"timed out waiting for main thread (is the game frozen or loading?)\"}";
                        writer.WriteLine(req.Response);
                    }
                }
            }
            catch (Exception) { }
            finally { try { c.Close(); } catch { } }
        }

        void Update()
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 20)
            {
                Request r;
                lock (queue)
                {
                    if (queue.Count == 0) break;
                    r = queue.Dequeue();
                }
                r.Response = Execute(r.Line);
                r.Done.Set();
            }

            for (int i = deferred.Count - 1; i >= 0; i--)
            {
                var kv = deferred[i];
                bool ready;
                try { ready = kv.Key(); } catch { ready = true; }
                if (!ready) continue;
                deferred.RemoveAt(i);
                try { kv.Value(); } catch (Exception e) { EventLog.Add("harness.error", e.ToString()); }
            }

            Autopilot.Update();
        }

        static string Execute(string line)
        {
            object id = null;
            try
            {
                var msg = Json.Parse(line) as Dictionary<string, object>;
                if (msg == null) throw new HarnessException("request must be a JSON object");
                msg.TryGetValue("id", out id);
                if (!msg.TryGetValue("cmd", out var cmdObj)) throw new HarnessException("missing 'cmd'");
                var cmd = (string)cmdObj;
                if (!commands.TryGetValue(cmd, out var m)) throw new HarnessException("unknown command '" + cmd + "' (try 'help')");
                msg.TryGetValue("args", out var argObj);
                var result = m.Invoke(null, new object[] { new Args(argObj as Dictionary<string, object>) });
                return Json.Serialize(new Dictionary<string, object> { ["id"] = id, ["ok"] = true, ["result"] = result });
            }
            catch (Exception e)
            {
                if (e is TargetInvocationException tie && tie.InnerException != null) e = tie.InnerException;
                string err = e is HarnessException ? e.Message : e.GetType().Name + ": " + e.Message + "\n" + e.StackTrace;
                return Json.Serialize(new Dictionary<string, object> { ["id"] = id, ["ok"] = false, ["error"] = err });
            }
        }

        [Cmd("help", "List commands, or show help for one: {name?}")]
        static object Help(Args a)
        {
            var name = a.Str("name");
            if (name != null)
                return helps.TryGetValue(name, out var h) ? h : throw new HarnessException("no such command");
            return helps.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => (object)kv.Value);
        }

        [Cmd("ping", "Health check. Returns version, scene, UT.")]
        static object Ping(Args a) => new Dictionary<string, object>
        {
            ["version"] = Version,
            ["scene"] = HighLogic.LoadedScene.ToString(),
            ["ut"] = Planetarium.fetch != null ? Planetarium.GetUniversalTime() : 0.0,
            ["event_seq"] = EventLog.Seq,
        };

        [Cmd("events", "Game events since seq: {since=0, type?:prefix filter}")]
        static object Events(Args a) => new Dictionary<string, object>
        {
            ["seq"] = EventLog.Seq,
            ["events"] = EventLog.Since((long)a.Num("since", 0), a.Str("type")),
        };
    }
}
