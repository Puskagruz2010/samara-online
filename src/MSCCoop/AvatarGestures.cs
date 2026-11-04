using MSCLoader;
using MSCCoop.Net;
using UnityEngine;

namespace MSCCoop;

// The walkers' rig has a single bone for all fingers, so no hand shape (a raised thumb or a middle
// finger) can be posed on it. The arm movements come from other NPCs on the same rig instead: the
// game animates an NPC's arm from an Animation on that arm's collar bone, or both arms from the upper
// spine, and a clip taken from there plays the same way on the same bone of the avatar. Props (a
// cigarette, the stream when peeing) are copies of the ones the game shows on NPCs and on the player.
sealed class AvatarGestures
{
    const float Fade = 0.15f;
    const string Spine = "pelvis/spine_middle/spine_upper";
    const string RightArm = Spine + "/collar_right";
    const string LeftArm = Spine + "/collar_left";
    const string LeftFingers = LeftArm + "/shoulder_left/arm_left/hand_left/finger_left";
    const string SmokerArm = "JOBS/HouseShit1/LOD/ShitNPC/ShitMan1/skeleton/pelvis/RotationPivot/spine_middle/spine_upper/collar_left";
    const string DrinkerArm = "JOBS/HouseShit1/LOD/ShitNPC/ShitMan1/skeleton/pelvis/RotationPivot/spine_middle/spine_upper/collar_right";
    const string RightHand = RightArm + "/shoulder_right/arm_right/hand_right";
    const string PlayerCamera = "PLAYER/Pivot/AnimPivot/Camera/FPSCamera";
    const float StreamAhead = 0.15f;

    sealed class Move
    {
        public Animation Target;
        public string On;
        public string Off;
        public string Idle;
        public bool Hold;
        public GameObject Prop;
    }

    readonly Move[] moves = new Move[(int)Gesture.Drink + 1];
    Gesture current;

    public bool Has(Gesture gesture) => moves[(int)gesture]?.Target != null;

    public static AvatarGestures Create(Transform root, Transform skeleton, Quaternion modelRotation)
    {
        var gestures = new AvatarGestures();
        // An old man shaking his fist from a window is the nearest the game has to an insult.
        gestures.Add(Gesture.Finger, skeleton, LeftArm, "PERAJARVI/HouseRintamaCrazy/Watcher/Oldman/Man/skeleton/" + LeftArm, "oldman_fist", null, WrapMode.Once);
        gestures.Add(Gesture.Hitchhike, skeleton, RightArm, "DRAGRACE/LOD/DRAGSTRIP/Sender/Pivot/Char/skeleton/" + RightArm, "drag_worker_pointright_on", "drag_worker_pointright_off", WrapMode.ClampForever);
        gestures.Add(Gesture.Hit, skeleton, RightArm, "DANCEHALL/Functions/FIGHTER/Fighter/Pivot/Char/skeleton/" + RightArm, "fighter_right_hit_jaw", null, WrapMode.Once);
        gestures.Add(Gesture.Push, skeleton, Spine, "DANCEHALL/Functions/GUARD/Guard/Pivot/Char/skeleton/" + Spine, "guard_hands_grab", "guard_hands_throw", WrapMode.ClampForever);
        gestures.Add(Gesture.Smoke, skeleton, LeftArm, SmokerArm, "fat_handsleft_smoke", null, WrapMode.Loop);
        gestures.AddProp(Gesture.Smoke, CopyProp(SmokerArm + "/shoulder_left/arm_left/hand_left/finger_left/SmokingLeft", skeleton.Find(LeftFingers)));
        gestures.AddProp(Gesture.Urinate, CreateStream(root, skeleton, modelRotation));
        // The men waiting at the septic tank jobs drink beer with their right hand.
        gestures.Add(Gesture.Drink, skeleton, RightArm, DrinkerArm, "fat_handsright_drink", null, WrapMode.Loop);
        gestures.AddProp(Gesture.Drink, CopyProp(DrinkerArm + "/shoulder_right/arm_right/hand_right/BottleBeerFly", skeleton.Find(RightHand)));
        return gestures;
    }

    void Add(Gesture gesture, Transform skeleton, string bone, string sourcePath, string on, string off, WrapMode wrap)
    {
        var target = skeleton.Find(bone);
        var source = FindInScene(sourcePath);
        var sourceAnimation = source == null ? null : source.GetComponent<Animation>();
        if (target == null || sourceAnimation == null || sourceAnimation[on] == null || off != null && sourceAnimation[off] == null)
            return;
        var animation = target.GetComponent<Animation>();
        if (animation == null)
            animation = target.gameObject.AddComponent<Animation>();
        var idle = animation.clip == null ? null : animation.clip.name;
        animation.AddClip(sourceAnimation[on].clip, on);
        animation[on].wrapMode = wrap;
        if (off != null)
        {
            animation.AddClip(sourceAnimation[off].clip, off);
            animation[off].wrapMode = WrapMode.Once;
        }
        moves[(int)gesture] = new Move { Target = animation, On = on, Off = off, Idle = idle, Hold = wrap != WrapMode.Once };
    }

    void AddProp(Gesture gesture, GameObject prop)
    {
        if (prop == null)
            return;
        prop.SetActive(false);
        moves[(int)gesture] ??= new Move { Hold = true };
        moves[(int)gesture].Prop = prop;
    }

    public void Set(Gesture gesture)
    {
        if (gesture == current)
            return;
        var previous = moves[(int)current];
        if (previous != null)
        {
            if (previous.Prop != null)
                previous.Prop.SetActive(false);
            if (previous.Hold && previous.Target != null)
                End(previous);
        }
        current = gesture;
        var next = moves[(int)gesture];
        if (next == null)
            return;
        if (next.Prop != null)
        {
            next.Prop.SetActive(true);
            Wake(next.Prop);
        }
        if (next.Target == null)
            return;
        next.Target.CrossFade(next.On, Fade);
        if (!next.Hold && next.Idle != null)
            next.Target.CrossFadeQueued(next.Idle, Fade, QueueMode.CompleteOthers);
    }

    static void End(Move move)
    {
        if (move.Off == null)
        {
            if (move.Idle != null)
                move.Target.CrossFade(move.Idle, Fade);
            return;
        }
        move.Target.CrossFade(move.Off, Fade);
        if (move.Idle != null)
            move.Target.CrossFadeQueued(move.Idle, Fade, QueueMode.CompleteOthers);
    }

    // Placed where it sits on the NPC it is copied from, relative to the same bone.
    static GameObject CopyProp(string sourcePath, Transform bone)
    {
        var source = FindInScene(sourcePath);
        if (source == null || bone == null)
            return null;
        var copy = Strip((GameObject)Object.Instantiate(source.gameObject));
        copy.transform.SetParent(bone, false);
        copy.transform.localPosition = source.localPosition;
        copy.transform.localRotation = source.localRotation;
        copy.transform.localScale = source.localScale;
        return copy;
    }

    // The player's own stream and its sound, aimed from the avatar's hips the way the stream leaves
    // the player's camera when looking straight ahead.
    static GameObject CreateStream(Transform root, Transform skeleton, Quaternion modelRotation)
    {
        var camera = FindInScene(PlayerCamera);
        var fluid = camera == null ? null : camera.Find("Piss/Fluid/Fluid");
        var pelvis = skeleton.Find("pelvis");
        if (fluid == null || pelvis == null)
        {
            ModConsole.Warning("Co-op: the pee stream was not found, others will not see it.");
            if (camera != null && camera.Find("Piss") != null)
                Describe("Piss", camera.Find("Piss"));
            return null;
        }
        Describe("pee stream", fluid);
        var stream = Strip((GameObject)Object.Instantiate(fluid.gameObject));
        stream.transform.SetParent(root, false);
        var facing = Quaternion.Inverse(modelRotation);
        stream.transform.localPosition = root.InverseTransformPoint(pelvis.position) + facing * (Vector3.forward * StreamAhead);
        stream.transform.localRotation = facing * (Quaternion.Inverse(camera.rotation) * fluid.rotation);
        var sound = FindInScene("PLAYER/PeeSound");
        if (sound != null)
        {
            var copy = Strip((GameObject)Object.Instantiate(sound.gameObject));
            copy.transform.SetParent(stream.transform, false);
            copy.transform.localPosition = Vector3.zero;
            copy.SetActive(true);
        }
        return stream;
    }

    // The game starts the stream's particles from its own logic, which the copy no longer has, so
    // they are started here whenever the prop is shown.
    static void Wake(GameObject prop)
    {
        foreach (var system in prop.GetComponentsInChildren<ParticleSystem>(true))
        {
            system.gameObject.SetActive(true);
            system.enableEmission = true;
            system.loop = true;
            system.Play(true);
        }
        // The game drives the pee stream's emission rate from its Piss FSM (10 to 400 a second while
        // peeing, 0 at rest), and the copy is taken at rest, so the rate is set here.
        foreach (var emitter in prop.GetComponentsInChildren<ParticleEmitter>(true))
        {
            emitter.gameObject.SetActive(true);
            if (emitter.maxEmission < 1f)
            {
                emitter.minEmission = 150f;
                emitter.maxEmission = 250f;
            }
            emitter.emit = true;
        }
        foreach (var renderer in prop.GetComponentsInChildren<Renderer>(true))
            renderer.enabled = true;
        foreach (var audio in prop.GetComponentsInChildren<AudioSource>(true))
        {
            audio.enabled = true;
            audio.loop = true;
            audio.spatialBlend = 1f;
            if (!audio.isPlaying)
                audio.Play();
        }
    }

    static void Describe(string label, Transform t)
    {
        var sb = new System.Text.StringBuilder("Co-op: " + label + ":");
        foreach (var child in t.GetComponentsInChildren<Transform>(true))
        {
            sb.Append(" | ").Append(child.name).Append(child.gameObject.activeSelf ? "" : "(off)").Append(':');
            foreach (var c in child.GetComponents<Component>())
                sb.Append(' ').Append(c == null ? "?" : c.GetType().Name);
        }
        Debug.Log(sb.ToString());
    }

    // Only what can be seen and heard stays: the copies must not run the game's logic or collide.
    static GameObject Strip(GameObject copy)
    {
        foreach (var behaviour in copy.GetComponentsInChildren<MonoBehaviour>(true))
            Object.DestroyImmediate(behaviour);
        foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
            Object.DestroyImmediate(collider);
        foreach (var body in copy.GetComponentsInChildren<Rigidbody>(true))
            Object.DestroyImmediate(body);
        return copy;
    }

    // NPCs that only appear for events (the drag race, the window watcher) are switched off, and
    // GameObject.Find does not see inactive objects.
    static Transform FindInScene(string path)
    {
        var slash = path.IndexOf('/');
        var rootName = path.Substring(0, slash);
        foreach (Transform t in Resources.FindObjectsOfTypeAll(typeof(Transform)))
        {
            if (t.parent != null || t.name != rootName || t.gameObject.hideFlags != HideFlags.None)
                continue;
            var found = t.Find(path.Substring(slash + 1));
            if (found != null)
                return found;
        }
        return null;
    }
}
