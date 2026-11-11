using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace MSCCoop.Recon;

// Shortcuts for setting up co-op test scenarios alone. Everything here goes through the same
// game state a player would change, so the co-op mod syncs it like any other change.
class TestCommand(string marksFile) : ConsoleCommand
{
    static readonly string[] Needs = { "PlayerHunger", "PlayerThirst", "PlayerFatigue", "PlayerStress", "PlayerUrine", "PlayerDirtiness" };
    static readonly string[] Keys = { "PlayerKeyFerndale", "PlayerKeyGifu", "PlayerKeyHayosiko", "PlayerKeyHome", "PlayerKeyRuscko", "PlayerKeySatsuma" };

    public override string Name => "test";

    public override string Help =>
        "test money AMOUNT | test needs | test keys | test tp VEHICLE|player|MARK | test bring VEHICLE | " +
        "test mark NAME | test marks | test bringall | test god | test flip | test home | test fix";

    // Where the player stands in front of the house in a fresh save.
    static readonly Vector3 Home = new(-8.3f, 0.8f, 11.5f);

    static readonly string[] Vehicles = { "SATSUMA", "HAYOSIKO", "FERNDALE", "GIFU", "RCO_RUSCKO", "KEKMET", "JONNEZ" };

    public override void Run(string[] rawArgs)
    {
        var args = Array.FindAll(rawArgs, a => a.Length > 0);
        var verb = args.Length > 0 ? args[0] : "";
        var target = args.Length > 1 ? string.Join(" ", args, 1, args.Length - 1) : "";
        switch (verb)
        {
            case "money" when float.TryParse(target, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount):
                var money = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerMoney");
                money.Value += amount;
                ModConsole.Print("money: " + money.Value);
                break;
            case "keys":
                foreach (var key in Keys)
                    PlayMakerGlobals.Instance.Variables.FindFsmInt(key).Value = 1;
                ModConsole.Print("all keys given");
                break;
            case "needs":
                foreach (var name in Needs)
                    PlayMakerGlobals.Instance.Variables.FindFsmFloat(name).Value = 0;
                ModConsole.Print("needs reset");
                break;
            case "tp" when target.Length > 0:
                Teleport(target);
                break;
            case "bring" when target.Length > 0:
                Bring(target);
                break;
            case "god":
                GodMode.Ensure();
                GodMode.Enabled = !GodMode.Enabled;
                ModConsole.Print("god mode " + (GodMode.Enabled ? "ON: no death, needs stay below 80%" : "OFF"));
                break;
            case "flip":
                Flip();
                break;
            case "fix":
                Fix();
                break;
            case "home":
                TeleportTo(Home);
                break;
            case "bringall":
                BringAll();
                break;
            case "mark" when target.Length > 0:
                Mark(target);
                break;
            case "marks":
                foreach (var mark in ReadMarks())
                    ModConsole.Print(mark.Key + " " + mark.Value);
                break;
            default:
                ModConsole.Print(Help);
                break;
        }
    }

    void Teleport(string target)
    {
        var player = GameObject.Find("PLAYER");
        if (player == null || PlayMakerGlobals.Instance.Variables.FindFsmBool("PlayerSeated").Value)
        {
            ModConsole.Error("test: get out of the vehicle first");
            return;
        }
        Vector3 destination;
        if (ReadMarks().TryGetValue(target, out var marked))
            destination = marked;
        else
        {
            var root = FindRoot(target == "player" ? "MSCCoop Player" : target);
            if (root == null)
                return;
            destination = root.position + root.right * 2.5f + Vector3.up;
        }
        player.transform.position = destination;
        ModConsole.Print("teleported to " + destination);
    }

    static void Bring(string target)
    {
        var root = FindRoot(target);
        var player = GameObject.Find("PLAYER");
        if (root == null || player == null)
            return;
        var look = player.transform.forward;
        var forward = new Vector3(look.x, 0, look.z).normalized;
        // Dropped from above, so it lands moving and the co-op mod claims and streams it.
        var position = player.transform.position + forward * 5 + Vector3.up * 1.5f;
        var rotation = Quaternion.LookRotation(Vector3.Cross(forward, Vector3.up));
        Unpin(root);
        root.position = position;
        root.rotation = rotation;
        ModConsole.Print("brought " + root.name);
    }

    static void TeleportTo(Vector3 destination)
    {
        var player = GameObject.Find("PLAYER");
        if (player == null || PlayMakerGlobals.Instance.Variables.FindFsmBool("PlayerSeated").Value)
        {
            ModConsole.Error("test: get out of the vehicle first");
            return;
        }
        player.transform.position = destination;
        ModConsole.Print("teleported to " + destination);
    }

    // Puts the nearest vehicle back on its wheels where it lies, facing the way it faced.
    static void Flip()
    {
        var player = GameObject.Find("PLAYER");
        if (player == null)
            return;
        Transform best = null;
        var bestDistance = 25f;
        foreach (var name in Vehicles)
        {
            foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
            {
                if (t.parent != null || !t.name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
                    continue;
                var distance = Vector3.Distance(t.position, player.transform.position);
                if (distance < bestDistance)
                {
                    best = t;
                    bestDistance = distance;
                }
            }
        }
        if (best == null)
        {
            ModConsole.Error("test: no vehicle within 25 m");
            return;
        }
        Unpin(best);
        var forward = best.forward;
        forward.y = 0;
        if (forward.sqrMagnitude < 0.01f)
            forward = Vector3.forward;
        best.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
        best.position += Vector3.up * 1.5f;
        ModConsole.Print("flipped " + best.name);
    }

    // Fills every vehicle's tank, the Satsuma's oil, coolant, brake and clutch fluid, charges its
    // battery and stands up vehicles lying on their side or roof. These are the values the co-op mod
    // shares, so running it on one machine is enough. Worn or broken parts and dented panels stay as
    // they are.
    static void Fix()
    {
        var filled = 0;
        var roots = new List<Transform>();
        var database = GameObject.Find("Database");
        if (database != null)
            roots.Add(database.transform);
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
        {
            if (t.parent == null && IsVehicle(t.name))
                roots.Add(t);
        }
        foreach (var root in roots)
        {
            foreach (var fsm in root.GetComponentsInChildren<PlayMakerFSM>(true))
            {
                if (fsm.FsmName != "Data")
                    continue;
                var vars = fsm.FsmVariables;
                var installed = vars.FindFsmBool("Installed");
                if (installed != null && !installed.Value)
                    continue;
                var capacity = vars.FindFsmFloat("MaxCapacity");
                var gifu = root.name.StartsWith("GIFU", StringComparison.OrdinalIgnoreCase);
                if (capacity != null && capacity.Value > 0)
                    filled += Set(vars, "FuelLevel", capacity.Value);
                else if (gifu)
                    filled += Set(vars, "FuelLevel", 300);
                if (vars.FindFsmFloat("OilContamination") != null)
                {
                    filled += Set(vars, "Oil", 3);
                    filled += Set(vars, "OilContamination", 0);
                }
                if (root == roots[0] && database != null)
                {
                    filled += Set(vars, "Water", fsm.gameObject.name == "Racing Radiator" ? 7 : 5.4f);
                    filled += Set(vars, "BrakeFluidF", 1);
                    filled += Set(vars, "BrakeFluidR", 1);
                    filled += Set(vars, "ClutchFluid", 0.5f);
                    var maxCharge = vars.FindFsmFloat("MaxCharge");
                    if (maxCharge != null && maxCharge.Value > 0)
                        filled += Set(vars, "Charge", maxCharge.Value);
                }
            }
        }

        var stood = 0;
        foreach (var root in roots)
        {
            if (root.name == "Database" || root.up.y > 0.5f)
                continue;
            Unpin(root);
            var forward = root.forward;
            forward.y = 0;
            if (forward.sqrMagnitude < 0.01f)
                forward = Vector3.forward;
            root.rotation = Quaternion.LookRotation(forward.normalized, Vector3.up);
            root.position += Vector3.up * 1.5f;
            stood++;
        }
        ModConsole.Print("fixed: " + filled + " fuel, fluid and charge levels topped up, " + stood + " vehicle(s) put back on their wheels");
    }

    static bool IsVehicle(string name)
    {
        foreach (var vehicle in Vehicles)
        {
            if (name.StartsWith(vehicle, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static int Set(FsmVariables vars, string name, float value)
    {
        var v = vars.FindFsmFloat(name);
        if (v == null || Mathf.Approximately(v.Value, value))
            return 0;
        v.Value = value;
        return 1;
    }

    // Far from the player the game pins a parked vehicle to the world with a FixedJoint (its LOD FSM,
    // state OFF). Moving a pinned vehicle lets the joint yank it back to the old spot, which shakes it
    // or throws it in the air, so the pin is removed and the vehicle stopped before it is moved.
    static void Unpin(Transform root)
    {
        foreach (var joint in root.GetComponents<FixedJoint>())
        {
            if (joint.connectedBody == null)
                UnityEngine.Object.DestroyImmediate(joint);
        }
        var body = root.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.velocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
    }

    // Lines every vehicle up beside the player, a few metres apart, for a session that starts with
    // everything at home.
    static void BringAll()
    {
        var player = GameObject.Find("PLAYER");
        if (player == null)
            return;
        var look = player.transform.forward;
        var forward = new Vector3(look.x, 0, look.z).normalized;
        var right = Vector3.Cross(Vector3.up, forward);
        var rotation = Quaternion.LookRotation(forward);
        var slot = 0;
        foreach (var name in Vehicles)
        {
            var root = FindRoot(name);
            if (root == null)
                continue;
            Unpin(root);
            var side = (slot % 2 == 0 ? 1 : -1) * (slot / 2 + 1) * 4.5f;
            root.position = player.transform.position + forward * 8 + right * side + Vector3.up * 1.5f;
            root.rotation = rotation;
            slot++;
            ModConsole.Print("brought " + root.name);
        }
    }

    void Mark(string name)
    {
        var player = GameObject.Find("PLAYER");
        if (player == null || name == "player" || name.IndexOf(' ') >= 0)
        {
            ModConsole.Error("test: marks are single words and only exist in game");
            return;
        }
        var marks = ReadMarks();
        marks[name] = player.transform.position;
        var lines = new List<string>();
        foreach (var mark in marks)
        {
            var p = mark.Value;
            lines.Add(mark.Key + " " + F(p.x) + " " + F(p.y) + " " + F(p.z));
        }
        File.WriteAllText(marksFile, string.Join("\n", lines.ToArray()) + "\n");
        ModConsole.Print("marked " + name + " at " + player.transform.position);
    }

    static string F(float v) => v.ToString("R", CultureInfo.InvariantCulture);

    Dictionary<string, Vector3> ReadMarks()
    {
        var marks = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(marksFile))
            return marks;
        foreach (var line in File.ReadAllLines(marksFile))
        {
            var f = line.Split(' ');
            if (f.Length == 4 && TryFloat(f[1], out var x) && TryFloat(f[2], out var y) && TryFloat(f[3], out var z))
                marks[f[0]] = new Vector3(x, y, z);
        }
        return marks;
    }

    static bool TryFloat(string s, out float v) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);

    // Vehicles are scene roots named like "SATSUMA(557kg, 248)", so a prefix is enough to pick one.
    static Transform FindRoot(string prefix)
    {
        Transform found = null;
        foreach (var t in UnityEngine.Object.FindObjectsOfType<Transform>())
        {
            if (t.parent != null || !t.name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            if (found != null)
            {
                ModConsole.Error("test: \"" + prefix + "\" matches both " + found.name + " and " + t.name);
                return null;
            }
            found = t;
        }
        if (found == null)
            ModConsole.Error("test: nothing named " + prefix);
        return found;
    }
}
