using HutongGames.PlayMaker;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// What the player state does not carry because it changes slowly or only matters for looks: how
// drunk the player is (the avatar sways) and whether smoke is being breathed out.
sealed class PlayerExtras(PlayerSync players)
{
    const double SendInterval = 0.25;

    Session session;
    FsmFloat drunk;
    PlayMakerFSM smoking;
    double nextSend;

    public void Attach(Session newSession)
    {
        session = newSession;
        session.MessageReceived += OnMessage;
    }

    public void Detach() => session = null;

    public void OnGameReady()
    {
        drunk = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerDrunk");
        var camera = GameObject.Find("PLAYER/Pivot/AnimPivot/Camera/FPSCamera");
        var smoke = camera == null ? null : camera.transform.Find("FPSCamera/Smoking");
        smoking = smoke == null ? null : smoke.gameObject.GetPlayMaker("Start");
    }

    public void Update()
    {
        if (session == null || session.State != SessionState.Connected)
            return;
        var now = session.HostTime;
        if (now < nextSend && nextSend - now < 1)
            return;
        nextSend = now + SendInterval;
        var level = drunk == null ? 0f : drunk.Value;
        var state = smoking != null && smoking.gameObject.activeInHierarchy ? smoking.ActiveStateName : "";
        var w = session.BeginMessage(MessageType.PlayerExtras);
        w.WriteByte(session.LocalSlot);
        w.WriteByte((byte)Mathf.Clamp(level * 50f, 0f, 255f));
        w.WriteBool(state.StartsWith("Outhale"));
        session.Broadcast(Channel.State);
    }

    void OnMessage(Connection from, MessageType type, BitReader reader)
    {
        if (type != MessageType.PlayerExtras)
            return;
        var slot = reader.ReadByte();
        var level = reader.ReadByte();
        var exhale = reader.ReadBool();
        if (reader.Overflow || slot >= Protocol.MaxPlayers || slot == session.LocalSlot)
            return;
        if (session.IsHost)
        {
            if (slot != from.Slot)
                return;
            var w = session.BeginMessage(MessageType.PlayerExtras);
            w.WriteByte(slot);
            w.WriteByte(level);
            w.WriteBool(exhale);
            session.Broadcast(Channel.State, from);
        }
        players.GetAvatar(slot)?.SetExtras(level / 50f, exhale);
    }
}
