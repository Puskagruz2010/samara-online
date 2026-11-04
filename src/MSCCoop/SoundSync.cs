using HutongGames.PlayMaker;
using HutongGames.PlayMaker.Actions;
using MSCCoop.Net;
using MSCLoader;
using UnityEngine;

namespace MSCCoop;

// Everything the player's own body says or does out loud goes through one PlayMaker action,
// MasterAudioPlaySound, in the FSMs under PLAYER: swearing, the middle finger shout, drunk talk,
// "yes", drinking, burping, puking, the lighter, punches, tools, footsteps. A hook in front of each
// of those actions tells the others which sound group and variation played, and they play the same
// sound from the speaker's avatar.
sealed class SoundSync(PlayerSync players)
{
    const int MaxChars = 32;
    const float HearRange = 70f;
    // Footsteps come several times a second and are worthless late, so they go unreliable.
    const string Footsteps = "Walking";

    Session session;
    int hooked;
    // FSMs on objects the game has not switched on yet (the drinking hand) cannot load their actions
    // until PlayMaker initialises them, which happens the first time they run; they are hooked then.
    readonly System.Collections.Generic.List<PlayMakerFSM> pending = new();
    float nextRetry;

    public int Played { get; private set; }

    public void Attach(Session newSession)
    {
        session = newSession;
        session.MessageReceived += OnMessage;
    }

    public void Detach() => session = null;

    public void OnGameReady()
    {
        hooked = 0;
        pending.Clear();
        var player = GameObject.Find("PLAYER");
        if (player == null)
            return;
        foreach (var fsm in player.GetComponentsInChildren<PlayMakerFSM>(true))
        {
            if (fsm.Fsm == null || !fsm.Fsm.Initialized)
                pending.Add(fsm);
            else
                Hook(fsm);
        }
        Debug.Log("Co-op: " + hooked + " player sounds shared, " + pending.Count + " FSMs wait for the game to start them.");
    }

    public void Update()
    {
        if (pending.Count == 0 || Time.unscaledTime < nextRetry)
            return;
        nextRetry = Time.unscaledTime + 1f;
        for (var i = pending.Count - 1; i >= 0; i--)
        {
            var fsm = pending[i];
            if (fsm == null)
            {
                pending.RemoveAt(i);
                continue;
            }
            if (fsm.Fsm == null || !fsm.Fsm.Initialized)
                continue;
            pending.RemoveAt(i);
            Hook(fsm);
        }
    }

    void Hook(PlayMakerFSM fsm)
    {
        try
        {
            foreach (var state in fsm.FsmStates)
            {
                var actions = state.Actions;
                // From the end, so inserting a hook does not move the actions still to visit.
                for (var i = actions.Length - 1; i >= 0; i--)
                {
                    if (actions[i] is not MasterAudioPlaySound sound)
                        continue;
                    var captured = sound;
                    if (fsm.FsmInject(state.Name, () => Send(captured), false, i))
                        hooked++;
                }
            }
        }
        catch (System.Exception e)
        {
            Debug.Log("Co-op: could not share the sounds of " + fsm.FsmName + " on " + fsm.gameObject.name + ": " + e.Message);
        }
    }

    void Send(MasterAudioPlaySound sound)
    {
        if (session == null || session.State != SessionState.Connected)
            return;
        var group = sound.soundGroupName == null ? "" : sound.soundGroupName.Value;
        if (string.IsNullOrEmpty(group))
            return;
        var variation = sound.variationName == null || sound.variationName.IsNone ? "" : sound.variationName.Value ?? "";
        var w = session.BeginMessage(MessageType.PlayerSound);
        w.WriteByte(session.LocalSlot);
        w.WriteString(group, MaxChars);
        w.WriteString(variation, MaxChars);
        session.Broadcast(group == Footsteps ? Channel.State : Channel.Control);
    }

    void OnMessage(Connection from, MessageType type, BitReader reader)
    {
        if (type != MessageType.PlayerSound)
            return;
        var slot = reader.ReadByte();
        var group = reader.ReadString(MaxChars);
        var variation = reader.ReadString(MaxChars);
        if (reader.Overflow || slot >= Protocol.MaxPlayers || slot == session.LocalSlot)
            return;

        if (session.IsHost)
        {
            if (slot != from.Slot)
                return;
            var w = session.BeginMessage(MessageType.PlayerSound);
            w.WriteByte(slot);
            w.WriteString(group, MaxChars);
            w.WriteString(variation, MaxChars);
            session.Broadcast(group == Footsteps ? Channel.State : Channel.Control, from);
        }

        var avatar = players.GetAvatar(slot);
        if (avatar == null)
            return;
        var position = avatar.Head.position;
        var listener = Camera.main;
        if (listener != null && (listener.transform.position - position).sqrMagnitude > HearRange * HearRange)
            return;
        try
        {
            MasterAudio.PlaySound3DAtVector3AndForget(group, position, 1f, null, 0f, variation.Length == 0 ? null : variation);
            Played++;
        }
        catch (System.Exception e)
        {
            Debug.Log("Co-op: could not play " + group + "/" + variation + ": " + e.Message);
        }
    }
}
