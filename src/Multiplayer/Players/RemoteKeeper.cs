using System.IO;
using GYK2.TombManyKeepers.Features.MultiplayerKeepers;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Players;

// Drives another player's keeper from their shared position, facing, animator input and shackles.
[HarmonyPatch]
internal sealed class RemoteKeeper : MonoBehaviour
{
    private const float SnapDistance = 3f;
    // Chain triggers are reliable and normally arrive before the state that reflects them.
    private const float ShackleGrace = 1f;
    private static readonly RemoteKeeper[] Keepers = new RemoteKeeper[CoopSession.MaxPlayers + 1];
    private static readonly int[] reportedShackles = new int[CoopSession.MaxPlayers + 1];
    private static readonly string[] reportedScenes = new string[CoopSession.MaxPlayers + 1];
    // Keepers recreated after their prison scene unloads live outside game scenes, like the primary.
    private static GameObject wanderers;

    private PlayerPhysicalBody body;
    private PlayerAnimation animation;
    private KeeperChains chains;
    private SpriteRenderer[] overlays;
    private Vector3 position;
    private float direction;
    private int state;
    private int shackles;
    private float shacklesDiffer = -1f;
    private string scene;

    internal static bool CanShare => KeeperSpawn.Active;

    internal static int ReportedShackles(int slot) => reportedShackles[slot];

    // The scene another player's keeper was last reported in.
    internal static string SceneOf(int slot) => reportedScenes[slot];

    // Where another player's keeper shows speech and answers, while it is shown here.
    internal static Transform BubblePoint(int slot)
    {
        var keeper = KeeperSpawn.Active ? Find(slot) : null;
        return keeper == null || !keeper.body.gameObject.activeInHierarchy ? null : keeper.body.PlayerView.BubblePoint;
    }

    // The animation that talks for another player's keeper.
    internal static AnimationComponent Talking(int slot)
    {
        var keeper = KeeperSpawn.Active ? Find(slot) : null;
        return keeper == null ? null : keeper.animation;
    }

    // The native opening holds its keeper until the chain object is replaced by hanging shackles;
    // a joined keeper follows the host's rescue.
    internal static int LocalShackles()
    {
        var session = CoopSession.Current;
        if (session != null && !session.IsHost)
        {
            if (KeeperSpawn.LocalChainedPending)
                return 2;
            var local = KeeperSpawn.Keeper(session.LocalSlot);
            return local == null || local.IsFree ? 0 : local.RemainingShackles;
        }
        if (MainGame.WorldData.GetWgoDataByCustomTag("intro_prison_chain_player") == null)
            return 0;
        return KeeperChains.Shackles(MainGame.PlayerController.View.PlayerAnimation.Animator) == 1 ? 1 : 2;
    }

    internal static void WriteLocal(BinaryWriter writer)
    {
        var player = MainGame.PlayerController;
        var animator = player.View.PlayerAnimation.Animator;
        var position = player.PhysicalBody.transform.position;
        writer.Write(position.x);
        writer.Write(position.y);
        writer.Write(position.z);
        writer.Write(animator.GetFloat(AnimationComponentBase.idDirectionAnimator));
        writer.Write((short)animator.GetInteger(AnimationComponentBase.idStateAnimator));
        writer.Write((byte)LocalShackles());
        writer.Write(MainGame.PlayerData.currentGameSceneId ?? string.Empty);
    }

    internal static void Read(int slot, BinaryReader reader)
    {
        var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
        float direction = reader.ReadSingle();
        int state = reader.ReadInt16();
        int shackles = reader.ReadByte();
        string scene = reader.ReadString();
        reportedShackles[slot] = shackles;
        reportedScenes[slot] = scene;
        if (!KeeperSpawn.Active)
            return;
        var keeper = Find(slot);
        if (keeper == null && shackles == 0 && scene == MainGame.PlayerData.currentGameSceneId)
            keeper = Attach(slot, KeeperSpawn.CreateKeeper(Wanderers(), slot, position), null);
        if (keeper == null)
            return;
        keeper.position = position;
        keeper.direction = direction;
        keeper.state = state;
        keeper.shackles = shackles;
        keeper.scene = scene;
    }

    internal static void Trigger(int slot, int trigger)
    {
        var keeper = KeeperSpawn.Active ? Find(slot) : null;
        if (keeper == null || !keeper.body.gameObject.activeSelf)
            return;
        // Chain motions belong to a keeper still in its bay; a free keeper never replays them.
        if (KeeperChains.IsChainTrigger(trigger))
        {
            if (keeper.chains != null)
                keeper.chains.Mirror(trigger);
            return;
        }
        keeper.animation.SetTrigger(trigger);
    }

    internal static void Free(int slot)
    {
        var chains = KeeperSpawn.Keeper(slot);
        if (chains != null && !chains.IsFree)
            chains.ReleaseNow();
    }

    internal static void Release(int slot)
    {
        reportedShackles[slot] = 0;
        reportedScenes[slot] = null;
        var keeper = Keepers[slot];
        Keepers[slot] = null;
        if (keeper == null)
            return;
        // Bay keepers leave with the bay; recreated keepers leave with their player.
        if (keeper.chains == null)
            Destroy(keeper.gameObject);
        else
            Destroy(keeper);
    }

    internal static void Clear()
    {
        for (int slot = 1; slot < Keepers.Length; slot++)
            Release(slot);
        if (wanderers != null)
            Destroy(wanderers);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(AnimationComponentBase), nameof(AnimationComponentBase.SetTrigger), typeof(string))]
    private static void ShareTrigger(AnimationComponentBase __instance, string trigger)
    {
        if (IsLocal(__instance))
            CoopSession.SendTrigger(Animator.StringToHash(trigger));
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(AnimationComponentBase), nameof(AnimationComponentBase.SetTrigger), typeof(int))]
    private static void ShareTriggerHash(AnimationComponentBase __instance, int trigger)
    {
        if (IsLocal(__instance))
            CoopSession.SendTrigger(trigger);
    }

    private static bool IsLocal(AnimationComponentBase animation) =>
        CoopSession.Current != null && animation == MainGame.PlayerController.View.PlayerAnimation;

    private static RemoteKeeper Find(int slot)
    {
        if (Keepers[slot] != null)
            return Keepers[slot];
        var chains = KeeperSpawn.Keeper(slot);
        var body = chains == null ? null : chains.GetComponentInChildren<PlayerPhysicalBody>(true);
        return body == null ? null : Attach(slot, body, chains);
    }

    private static RemoteKeeper Attach(int slot, PlayerPhysicalBody body, KeeperChains chains)
    {
        var keeper = body.transform.parent.gameObject.AddComponent<RemoteKeeper>();
        keeper.body = body;
        keeper.animation = body.PlayerView.PlayerAnimation;
        keeper.chains = chains;
        var view = keeper.animation.Animator.transform;
        keeper.overlays = new[]
        {
            view.Find("gfx/bdy_over").GetComponent<SpriteRenderer>(),
            view.Find("gfx (90 rotated)/-bdy_over").GetComponent<SpriteRenderer>()
        };
        return Keepers[slot] = keeper;
    }

    private static Transform Wanderers()
    {
        if (wanderers == null)
        {
            wanderers = new GameObject("Multiplayer Players");
            DontDestroyOnLoad(wanderers);
        }
        return wanderers.transform;
    }

    private void Update()
    {
        // Nothing to show until the first state arrives.
        if (scene == null)
            return;
        // A chained keeper stays in its bay; a free one follows its player between scenes.
        bool chained = chains != null && !chains.IsFree;
        bool visible = chained || scene == MainGame.PlayerData.currentGameSceneId;
        if (body.gameObject.activeSelf != visible)
            body.gameObject.SetActive(visible);
        if (!visible)
            return;
        if (chained)
        {
            CatchUpShackles();
            return;
        }
        var current = body.transform.position;
        var next = (position - current).sqrMagnitude > SnapDistance * SnapDistance ? position
            : Vector3.Lerp(current, position, 1f - Mathf.Exp(-15f * Time.deltaTime));
        body.transform.position = next;
        body.Rb.position = next;
        body.PlayerView.UpdatePosition(body.PlayerView.RoundedPosition);
        animation.SetState((AnimationState)state);
        animation.Animator.SetFloat(AnimationComponentBase.idDirectionAnimator, direction);
    }

    // A player who lost shackles out of view, such as while this game loaded, is shown in
    // their current pose. The host's own keepers already follow its rescues.
    private void CatchUpShackles()
    {
        if (CoopSession.Current == null || CoopSession.Current.IsHost || shackles >= chains.RemainingShackles || chains.IsBusy)
        {
            shacklesDiffer = -1f;
            return;
        }
        if (shacklesDiffer < 0f)
            shacklesDiffer = Time.unscaledTime;
        else if (Time.unscaledTime - shacklesDiffer > ShackleGrace)
            chains.Restore(shackles);
    }

    private void LateUpdate()
    {
        // Only the native chained states draw the chain overlay; a walking keeper never shows it.
        if (!body.gameObject.activeInHierarchy || KeeperChains.IsChained(animation.Animator))
            return;
        foreach (var overlay in overlays)
            overlay.forceRenderingOff = true;
    }
}
