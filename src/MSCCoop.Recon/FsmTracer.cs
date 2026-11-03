using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace MSCCoop.Recon;

// Records what the game's PlayMaker logic does around the player: every FSM state change, and every
// bool, int and string variable change, within a radius. Doing one thing in game (peeing, filling a
// bucket) with the tracer on shows which FSMs and states a sync has to hook.
class FsmTracer : MonoBehaviour
{
    const float Interval = 0.1f;
    const float RefreshInterval = 5f;

    sealed class Watched
    {
        public PlayMakerFSM Fsm;
        public string Path;
        public string State;
        public Dictionary<string, string> Values = new();
    }

    readonly List<Watched> watched = new();
    readonly HashSet<PlayMakerFSM> known = new();
    StreamWriter log;
    Transform player;
    float radius;
    float nextPoll;
    float nextRefresh;
    float started;
    int changes;

    public string File { get; private set; }

    public static FsmTracer Start(string dir, float radius)
    {
        var go = new GameObject("MSCCoopRecon Tracer");
        DontDestroyOnLoad(go);
        var tracer = go.AddComponent<FsmTracer>();
        tracer.radius = radius;
        tracer.File = Path.Combine(dir, "trace.log");
        tracer.log = new StreamWriter(tracer.File, false, new UTF8Encoding(false)) { AutoFlush = true };
        tracer.started = Time.realtimeSinceStartup;
        tracer.log.WriteLine("# trace radius " + radius + " m, started " + DateTime.Now.ToString("s"));
        return tracer;
    }

    public void Stop()
    {
        log.WriteLine("# stopped, " + changes + " changes");
        log.Dispose();
        Destroy(gameObject);
    }

    public void Note(string text) => log.WriteLine(Stamp() + " NOTE " + text);

    void Update()
    {
        if (Time.realtimeSinceStartup >= nextRefresh)
        {
            nextRefresh = Time.realtimeSinceStartup + RefreshInterval;
            Refresh();
        }
        if (Time.realtimeSinceStartup < nextPoll)
            return;
        nextPoll = Time.realtimeSinceStartup + Interval;
        if (player == null)
        {
            var go = GameObject.Find("PLAYER");
            player = go == null ? null : go.transform;
        }
        var center = player == null ? Vector3.zero : player.position;
        foreach (var w in watched)
        {
            var fsm = w.Fsm;
            if (fsm == null || !fsm.enabled || !fsm.gameObject.activeInHierarchy)
                continue;
            if (player != null && !IsUnder(fsm.transform, player) && (fsm.transform.position - center).sqrMagnitude > radius * radius)
                continue;
            var state = fsm.ActiveStateName;
            if (state != w.State)
            {
                if (w.State != null)
                    Write(w.Path + " [" + fsm.FsmName + "] " + w.State + " -> " + state);
                w.State = state;
            }
            TraceVariables(w);
        }
    }

    void TraceVariables(Watched w)
    {
        var vars = w.Fsm.FsmVariables;
        foreach (var v in vars.BoolVariables)
            Check(w, v.Name, v.Value ? "true" : "false");
        foreach (var v in vars.IntVariables)
            Check(w, v.Name, v.Value.ToString());
        foreach (var v in vars.StringVariables)
            Check(w, v.Name, v.Value);
        foreach (var v in vars.GameObjectVariables)
            Check(w, v.Name, v.Value == null ? "null" : v.Value.name);
    }

    void Check(Watched w, string name, string value)
    {
        if (w.Values.TryGetValue(name, out var old))
        {
            if (old == value)
                return;
            Write(w.Path + " [" + w.Fsm.FsmName + "] " + name + ": " + old + " -> " + value);
        }
        w.Values[name] = value;
    }

    void Refresh()
    {
        foreach (PlayMakerFSM fsm in Resources.FindObjectsOfTypeAll(typeof(PlayMakerFSM)))
        {
            if (fsm.gameObject.hideFlags != HideFlags.None || known.Contains(fsm))
                continue;
            known.Add(fsm);
            watched.Add(new Watched { Fsm = fsm, Path = PathOf(fsm.transform) });
        }
    }

    void Write(string line)
    {
        changes++;
        log.WriteLine(Stamp() + " " + line);
    }

    string Stamp() => (Time.realtimeSinceStartup - started).ToString("0.00");

    static bool IsUnder(Transform t, Transform root)
    {
        for (var c = t; c != null; c = c.parent)
        {
            if (c == root)
                return true;
        }
        return false;
    }

    static string PathOf(Transform t)
    {
        var sb = new StringBuilder(t.name);
        for (var c = t.parent; c != null; c = c.parent)
            sb.Insert(0, c.name + "/");
        return sb.ToString();
    }
}
