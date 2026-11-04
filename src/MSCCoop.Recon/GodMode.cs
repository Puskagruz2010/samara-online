using HutongGames.PlayMaker;
using MSCLoader;
using UnityEngine;

namespace MSCCoop.Recon;

// "test god": the game kills the player by switching on Systems/Death, whose FSM strips the player
// and loads the menu. While god mode is on that FSM is disabled, so switching Death on does nothing,
// and Death is switched back off. Its FSM is not initialised until the first death, so nothing is
// injected into it. Needs are held below the level that kills, so drinking, eating and peeing still
// work.
class GodMode : MonoBehaviour
{
    const float NeedCap = 80f;
    static readonly string[] Needs = { "PlayerHunger", "PlayerThirst", "PlayerFatigue", "PlayerStress", "PlayerUrine", "PlayerDirtiness" };

    public static bool Enabled;
    static GodMode instance;

    GameObject death;
    PlayMakerFSM deathFsm;
    float nextNeeds;

    public static void Ensure()
    {
        if (instance != null)
            return;
        var go = new GameObject("MSCCoopRecon GodMode");
        DontDestroyOnLoad(go);
        instance = go.AddComponent<GodMode>();
    }

    void Update()
    {
        if (Application.loadedLevelName != "GAME")
        {
            death = null;
            deathFsm = null;
            return;
        }
        if (death == null)
        {
            var systems = GameObject.Find("Systems");
            var t = systems == null ? null : systems.transform.Find("Death");
            if (t == null)
                return;
            death = t.gameObject;
            deathFsm = death.GetPlayMaker("Activate Dead Body");
        }
        if (deathFsm != null && deathFsm.enabled == Enabled)
            deathFsm.enabled = !Enabled;
        if (!Enabled)
            return;
        if (death.activeSelf)
        {
            death.SetActive(false);
            ModConsole.Print("god: death cancelled");
        }
        if (Time.unscaledTime < nextNeeds)
            return;
        nextNeeds = Time.unscaledTime + 1f;
        var globals = PlayMakerGlobals.Instance.Variables;
        foreach (var name in Needs)
        {
            var need = globals.FindFsmFloat(name);
            if (need != null && need.Value > NeedCap)
                need.Value = NeedCap;
        }
    }
}
