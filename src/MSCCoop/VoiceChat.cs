using System;
using HutongGames.PlayMaker;
using MSCCoop.Net;
using MSCLoader;
using Steamworks;
using UnityEngine;

namespace MSCCoop;

// Proximity voice over Steam Voice. Steam captures, denoises and compresses the microphone; the
// compressed frames travel on the unreliable channel through the host like player states, and every
// machine decompresses and plays them from a 3D source at the speaker's head. Holding the radio key
// sends the same voice flagged as radio: it is heard everywhere, flat and band-limited like a walkie-talkie.
sealed class VoiceChat(PlayerSync players)
{
    public const int SampleRate = 24000;
    const int MaxFrameBytes = 900;
    // Steam keeps producing the tail of the last word for a moment after recording stops.
    const float TailSeconds = 0.4f;
    const float SpeakingHold = 0.25f;

    public static SettingsKeybind TalkKey;
    public static SettingsKeybind RadioKey;
    public static SettingsCheckBox OpenMic;
    public static SettingsSlider Volume;
    public static SettingsSlider Range;
    public static SettingsCheckBox DrunkVoice;

    readonly byte[] compressed = new byte[8 * 1024];
    readonly byte[] relay = new byte[MaxFrameBytes];
    readonly byte[] pcm = new byte[SampleRate * 2];
    readonly VoicePlayer[] voices = new VoicePlayer[Protocol.MaxPlayers];
    Session session;
    FsmFloat drunk;
    bool recording;
    bool radio;
    float tailUntil;
    ushort sequence;
    bool broken;

    public bool Talking => recording;
    public bool RadioTalking => recording && radio;

    public static void CreateSettings()
    {
        Settings.AddHeader("Голос");
        TalkKey = Keybind.Add("voiceTalk", "Говорить (рядом)", KeyCode.CapsLock);
        RadioKey = Keybind.Add("voiceRadio", "Говорить по рации (всем)", KeyCode.Mouse3);
        OpenMic = Settings.AddCheckBox("voiceOpenMic", "Открытый микрофон (без кнопки)", false);
        Volume = Settings.AddSlider("voiceVolume", "Громкость голосов", 0f, 2f, 1f);
        Range = Settings.AddSlider("voiceRange", "Дальность слышимости, м", 10f, 120f, 40f, null, 0);
        DrunkVoice = Settings.AddCheckBox("voiceDrunk", "Пьяный голос плывёт", true);
        Settings.AddText("Микрофон берётся из настроек Steam → Голос.");
    }

    public void Attach(Session newSession)
    {
        session = newSession;
        session.MessageReceived += OnMessage;
        session.PlayerLeft += p => Remove(p.Slot);
    }

    public void Detach()
    {
        StopRecording();
        session = null;
        RemoveAll();
    }

    public void OnLevelLoaded() => RemoveAll();

    public void OnGameReady() => drunk = PlayMakerGlobals.Instance.Variables.FindFsmFloat("PlayerDrunk");

    public void Update()
    {
        if (broken || session == null || session.State != SessionState.Connected)
        {
            StopRecording();
            return;
        }

        try
        {
            Capture();
        }
        catch (Exception e)
        {
            // Without Steam (a LAN game started outside it) there is no voice; say so once.
            broken = true;
            ModConsole.Warning("Co-op: voice chat is off, Steam Voice is not available. " + e.Message);
            return;
        }

        for (var slot = 0; slot < voices.Length; slot++)
        {
            var voice = voices[slot];
            if (voice == null)
                continue;
            var avatar = players.GetAvatar(slot);
            voice.Follow(avatar == null ? null : avatar.Head);
            voice.Configure(Volume == null ? 1f : Volume.GetValue(), Range == null ? 40f : Range.GetValue(),
                DrunkVoice == null || DrunkVoice.GetValue());
            var speaking = voice.Speaking(SpeakingHold);
            avatar?.SetSpeaking(speaking, voice.Radio);
        }
    }

    void Capture()
    {
        var radioHeld = RadioKey != null && RadioKey.GetKeybind();
        var talkHeld = radioHeld || (TalkKey != null && TalkKey.GetKeybind()) || (OpenMic != null && OpenMic.GetValue());
        if (talkHeld && !recording)
        {
            SteamUser.StartVoiceRecording();
            recording = true;
        }
        else if (!talkHeld && recording)
        {
            SteamUser.StopVoiceRecording();
            recording = false;
            tailUntil = Time.unscaledTime + TailSeconds;
        }
        if (talkHeld)
            radio = radioHeld;
        if (!recording && Time.unscaledTime > tailUntil)
            return;

        if (SteamUser.GetAvailableVoice(out var available, out _, 0) != EVoiceResult.k_EVoiceResultOK || available == 0)
            return;
        if (SteamUser.GetVoice(true, compressed, (uint)compressed.Length, out var written, false, null, 0, out _, 0)
            != EVoiceResult.k_EVoiceResultOK || written == 0)
            return;
        // A frame that big means the game stalled; stale speech is worse than a gap.
        if (written > MaxFrameBytes)
            return;
        Send((int)written);
    }

    void Send(int length)
    {
        var drunkLevel = drunk == null ? 0f : drunk.Value;
        var w = session.BeginMessage(MessageType.Voice);
        w.WriteByte(session.LocalSlot);
        w.WriteUShort(sequence++);
        w.WriteBool(radio);
        w.WriteByte((byte)Mathf.Clamp(drunkLevel * 50f, 0f, 255f));
        w.Write((uint)length, 10);
        w.WriteBytes(compressed, 0, length);
        session.Broadcast(Channel.State);
    }

    void OnMessage(Connection from, MessageType type, BitReader reader)
    {
        if (type != MessageType.Voice)
            return;
        var slot = reader.ReadByte();
        var seq = reader.ReadUShort();
        var isRadio = reader.ReadBool();
        var drunkByte = reader.ReadByte();
        var length = (int)reader.Read(10);
        if (length > MaxFrameBytes || slot >= Protocol.MaxPlayers)
            return;
        reader.ReadBytes(relay, 0, length);
        if (reader.Overflow || slot == session.LocalSlot)
            return;

        if (session.IsHost)
        {
            if (slot != from.Slot)
                return;
            var w = session.BeginMessage(MessageType.Voice);
            w.WriteByte(slot);
            w.WriteUShort(seq);
            w.WriteBool(isRadio);
            w.WriteByte(drunkByte);
            w.Write((uint)length, 10);
            w.WriteBytes(relay, 0, length);
            session.Broadcast(Channel.State, from);
        }

        if (SteamUser.DecompressVoice(relay, (uint)length, pcm, (uint)pcm.Length, out var bytes, SampleRate)
            != EVoiceResult.k_EVoiceResultOK)
            return;
        var voice = voices[slot] ??= VoicePlayer.Create(slot);
        voice.Push(pcm, (int)bytes / 2, seq, isRadio, drunkByte / 50f);
    }

    void StopRecording()
    {
        if (!recording)
            return;
        recording = false;
        try
        {
            SteamUser.StopVoiceRecording();
        }
        catch (Exception)
        {
            // Steam already gone.
        }
    }

    void Remove(int slot)
    {
        if (voices[slot] == null)
            return;
        voices[slot].Destroy();
        voices[slot] = null;
        players.GetAvatar(slot)?.SetSpeaking(false, false);
    }

    void RemoveAll()
    {
        for (var i = 0; i < voices.Length; i++)
            Remove(i);
    }
}

// One remote speaker: a jitter-buffered ring of samples played through a streaming AudioClip whose
// reader callback runs on Unity's audio thread.
sealed class VoicePlayer
{
    const int RingSamples = VoiceChat.SampleRate * 2;
    // Wait for this much speech before playing after silence, so network jitter does not chop words.
    const int Prebuffer = VoiceChat.SampleRate * 80 / 1000;
    // More than this queued means the speaker's clock or the network ran ahead; drop the oldest.
    const int MaxQueued = VoiceChat.SampleRate * 400 / 1000;
    const float HeadHeight = 1.65f;

    readonly object gate = new();
    readonly float[] ring = new float[RingSamples];
    readonly GameObject go;
    readonly AudioSource source;
    readonly AudioLowPassFilter lowPass;
    readonly AudioHighPassFilter highPass;
    readonly AudioDistortionFilter distortion;
    int readPos;
    int count;
    bool playing;
    ushort lastSeq;
    bool hasSeq;
    float lastHeard = -100f;
    float drunk;
    float wobble;

    public bool Radio { get; private set; }

    VoicePlayer(GameObject go)
    {
        this.go = go;
        source = go.AddComponent<AudioSource>();
        lowPass = go.AddComponent<AudioLowPassFilter>();
        highPass = go.AddComponent<AudioHighPassFilter>();
        distortion = go.AddComponent<AudioDistortionFilter>();
        lowPass.cutoffFrequency = 3200;
        highPass.cutoffFrequency = 500;
        distortion.distortionLevel = 0.35f;
        SetRadioFilters(false);

        source.clip = AudioClip.Create("MSCCoop Voice", RingSamples, 1, VoiceChat.SampleRate, true, OnRead);
        source.loop = true;
        source.dopplerLevel = 0;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 1.5f;
        source.maxDistance = 40f;
        source.spatialBlend = 1f;
        source.priority = 0;
        source.Play();
    }

    public static VoicePlayer Create(int slot)
    {
        var go = new GameObject("MSCCoop Voice " + slot);
        return new VoicePlayer(go);
    }

    public void Push(byte[] pcm, int samples, ushort seq, bool radio, float drunkLevel)
    {
        // Unreliable frames can arrive late; one older than the last played is dropped.
        if (hasSeq && !Seq.IsNewer(seq, lastSeq))
            return;
        hasSeq = true;
        lastSeq = seq;
        Radio = radio;
        drunk = drunkLevel;
        lastHeard = Time.unscaledTime;
        lock (gate)
        {
            for (var i = 0; i < samples; i++)
            {
                var sample = (short)(pcm[2 * i] | (pcm[2 * i + 1] << 8)) / 32768f;
                var write = (readPos + count) % RingSamples;
                ring[write] = sample;
                if (count < RingSamples)
                    count++;
                else
                    readPos = (readPos + 1) % RingSamples;
            }
            if (count > MaxQueued)
            {
                var drop = count - Prebuffer;
                readPos = (readPos + drop) % RingSamples;
                count -= drop;
            }
        }
    }

    void OnRead(float[] data)
    {
        lock (gate)
        {
            if (!playing && count >= Prebuffer)
                playing = true;
            for (var i = 0; i < data.Length; i++)
            {
                if (playing && count > 0)
                {
                    data[i] = ring[readPos];
                    readPos = (readPos + 1) % RingSamples;
                    count--;
                }
                else
                {
                    data[i] = 0;
                    playing = false;
                }
            }
        }
    }

    public void Follow(Transform head)
    {
        if (Radio || head == null)
        {
            // Radio, or nobody to stand at yet: heard in the listener's head.
            var listener = Camera.main;
            if (listener != null)
                go.transform.position = listener.transform.position;
            return;
        }
        go.transform.position = head.position;
        if (head.name != "head")
            go.transform.position += Vector3.up * HeadHeight;
    }

    public void Configure(float volume, float range, bool drunkVoice)
    {
        source.volume = Mathf.Clamp(volume, 0f, 2f);
        source.maxDistance = Mathf.Max(5f, range);
        source.spatialBlend = Radio ? 0f : 1f;
        SetRadioFilters(Radio);
        if (drunkVoice && drunk > 0.3f)
        {
            // A slow wobble that grows with how drunk the speaker is.
            wobble += Time.deltaTime * (1.3f + drunk * 0.4f);
            var depth = Mathf.Clamp01(drunk / 4f) * 0.12f;
            source.pitch = 1f - depth * 0.5f + Mathf.Sin(wobble) * depth;
        }
        else
            source.pitch = 1f;
    }

    void SetRadioFilters(bool on)
    {
        lowPass.enabled = on;
        highPass.enabled = on;
        distortion.enabled = on;
    }

    public bool Speaking(float hold) => Time.unscaledTime - lastHeard < hold;

    public void Destroy() => UnityEngine.Object.Destroy(go);
}
