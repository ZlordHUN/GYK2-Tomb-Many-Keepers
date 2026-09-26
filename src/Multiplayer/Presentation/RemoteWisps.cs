using System.IO;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// Another player's wisp, such as the opening's fairy, shows where theirs is for the players in the same
// scene: a copy of this game's own wisp that only takes on their wisp's place, facing and look.
internal static class RemoteWisps
{
    private const float ShareInterval = 0.1f;
    private static readonly int Appear = Animator.StringToHash("fairy_appear");
    private static readonly int Disappear = Animator.StringToHash("fairy_disappear");
    private static readonly AccessTools.FieldRef<WispController, WispType> TypeOf =
        AccessTools.FieldRefAccess<WispController, WispType>("wispType");
    private static readonly Copy[] Copies = new Copy[CoopSession.MaxPlayers + 1];
    private static GameObject holder;
    private static float nextShare;
    private static bool sharedActive;
    private static WispType sharedType;

    // Where another player's wisp shows its speech, while it is shown here.
    internal static Transform BubblePoint(int slot)
    {
        var copy = Copies[slot];
        return copy != null && copy.gameObject.activeInHierarchy ? copy.BubblePoint : null;
    }

    // This game's wisp: whether it is out and in which form at once, its place with the frame's changes.
    internal static void Share()
    {
        var wisp = MainGame.PlayerController != null ? MainGame.PlayerController.WispController : null;
        bool active = wisp != null && wisp.GetActiveState();
        var type = active ? TypeOf(wisp) : sharedType;
        if (active != sharedActive || type != sharedType)
        {
            sharedActive = active;
            sharedType = type;
            SharedPresentation.Send(SharedPresentation.Cue.WispType, writer =>
            {
                writer.Write(active);
                writer.Write((byte)type);
            });
        }
        if (!active || Time.unscaledTime < nextShare)
            return;
        nextShare = Time.unscaledTime + ShareInterval;
        var position = wisp.transform.position;
        float facing = wisp.transform.localScale.x;
        SharedPresentation.Send(SharedPresentation.Cue.Wisp, writer =>
        {
            writer.Write((byte)type);
            writer.Write(position.x);
            writer.Write(position.y);
            writer.Write(position.z);
            writer.Write(facing);
        });
    }

    internal static void Apply(int slot, SharedPresentation.Cue cue, BinaryReader reader)
    {
        if (cue == SharedPresentation.Cue.WispType)
        {
            bool active = reader.ReadBoolean();
            var type = (WispType)reader.ReadByte();
            var copy = active && SharedPresentation.Watches(slot) ? Copies[slot] ?? Create(slot) : Copies[slot];
            copy?.Show(active && SharedPresentation.Watches(slot), type);
            return;
        }
        var form = (WispType)reader.ReadByte();
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        float facing = reader.ReadSingle();
        bool watched = SharedPresentation.Watches(slot);
        var shown = watched ? Copies[slot] ?? Create(slot) : Copies[slot];
        if (shown == null)
            return;
        shown.Show(watched, form);
        shown.MoveTo(position, facing);
    }

    internal static void Forget(int slot)
    {
        var copy = Copies[slot];
        Copies[slot] = null;
        if (copy != null)
            Object.Destroy(copy.gameObject);
    }

    // A new session hears about this game's wisp from the start.
    internal static void Reset()
    {
        sharedActive = false;
        sharedType = WispType.Normal;
        nextShare = 0f;
    }

    private static Copy Create(int slot)
    {
        var own = MainGame.PlayerController != null ? MainGame.PlayerController.WispController : null;
        if (own == null)
            return null;
        if (holder == null)
        {
            holder = new GameObject("Multiplayer Wisps");
            Object.DontDestroyOnLoad(holder);
        }
        string bubble = PathTo(own.transform, own.BubblePoint);
        var copy = Object.Instantiate(own.gameObject, holder.transform);
        copy.name = $"Keeper {slot} Wisp";
        copy.SetActive(false);
        // Only the look stays: the copy neither follows this game's keeper nor collides.
        Object.DestroyImmediate(copy.GetComponent<WispController>());
        var body = copy.GetComponent<Rigidbody>();
        if (body != null)
            body.isKinematic = true;
        foreach (var collider in copy.GetComponentsInChildren<Collider>(true))
            collider.enabled = false;
        var shown = copy.AddComponent<Copy>();
        shown.BubblePoint = bubble == null ? copy.transform : copy.transform.Find(bubble) ?? copy.transform;
        shown.Animator = copy.GetComponentInChildren<Animator>(true);
        return Copies[slot] = shown;
    }

    private static string PathTo(Transform root, Transform child)
    {
        if (child == null)
            return null;
        string path = null;
        for (var part = child; part != null && part != root; part = part.parent)
            path = path == null ? part.name : part.name + "/" + path;
        return path;
    }

    private sealed class Copy : MonoBehaviour
    {
        internal Transform BubblePoint;
        internal Animator Animator;
        private Vector3 target;
        private bool placed;
        private WispType type;

        internal void Show(bool visible, WispType next)
        {
            bool was = gameObject.activeSelf;
            if (was != visible)
                gameObject.SetActive(visible);
            // A copy shown again starts from the animator's first state, so the fairy appears again.
            if (visible && Animator != null && (next != type || !was))
            {
                if (next == WispType.Fairy)
                    Animator.SetTrigger(Appear);
                else if (type == WispType.Fairy && was)
                    Animator.SetTrigger(Disappear);
            }
            type = next;
            if (!visible)
                placed = false;
        }

        internal void MoveTo(Vector3 position, float facing)
        {
            target = position;
            if (!placed)
            {
                transform.position = position;
                placed = true;
            }
            transform.localScale = new Vector3(facing, 1f, 1f);
        }

        private void LateUpdate()
        {
            if (placed)
                transform.position = Vector3.Lerp(transform.position, target, 1f - Mathf.Exp(-15f * Time.deltaTime));
        }
    }
}
