using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Features.MultiplayerKeepers;

[HarmonyPatch]
internal static class KeeperSpawn
{
    // Wall bays by keeper slot, relative to the native wake position.
    private static readonly Vector3[] SlotOffsets =
    {
        Vector3.zero,
        Vector3.left * 4.42f,
        Vector3.left * 2.18f,
        Vector3.right * 14.14f
    };
    private static readonly int ReleasedPose = Animator.StringToHash("Static Tree");
    private static readonly AccessTools.FieldRef<PlayerController, PlayerData> ControllerData =
        AccessTools.FieldRefAccess<PlayerController, PlayerData>("playerData");
    private static readonly AccessTools.FieldRef<PlayerPhysicalBody, PlayerData> BodyData =
        AccessTools.FieldRefAccess<PlayerPhysicalBody, PlayerData>("playerData");
    private static readonly AccessTools.FieldRef<GenericSprite, bool> CastShadows =
        AccessTools.FieldRefAccess<GenericSprite, bool>("castShadows");
    private static readonly AccessTools.FieldRef<ObjectLinkedToDefinition<WGODef>, WGODef> Definition =
        AccessTools.FieldRefAccess<ObjectLinkedToDefinition<WGODef>, WGODef>("definition");
    private static readonly AccessTools.FieldRef<ObjectLinkedToDefinition<WGODef>, string> CachedId =
        AccessTools.FieldRefAccess<ObjectLinkedToDefinition<WGODef>, string>("cachedId");

    private static readonly KeeperChains[] chainSequences = new KeeperChains[SlotOffsets.Length];
    // Lobby slots whose keeper waits chained in a bay; players joining later may start free.
    private static readonly bool[] chainedMembers = new bool[SlotOffsets.Length];
    private static GameObject keepers;
    private static SpriteRenderer releasedShackles;
    private static bool localBound;
    private static bool hostsRescues;
    private static int localSlot;

    // The campaign is running, so players can be shown.
    internal static bool Active { get; private set; }
    // The opening's prison bays are loaded.
    internal static bool BaysOpen => keepers != null;
    // The bays were loaded this campaign; once they unload, keepers still chained are freed.
    internal static bool BaysWereOpen { get; private set; }
    // A new campaign's opening has yet to place the bays.
    internal static bool BaysAhead { get; private set; }
    // A joined keeper waits for the host's opening to place it chained in its bay.
    internal static bool LocalChainedPending { get; set; }
    internal static string BayScene { get; private set; }
    internal static Vector3 Wake { get; private set; }
    internal static Vector3 ChainAnchor { get; private set; }
    internal static event System.Action<int, KeeperChains> KeeperAdded;
    internal static event System.Action BaysOpened;
    // A joined player's pickaxe hit another keeper's shackles; the host applies it.
    internal static event System.Action<int> CutRequested;

    // A new campaign opens the bays when the native opening teleports its keeper.
    internal static void Enable(int slot)
    {
        EnableContinued(slot);
        BaysAhead = true;
        PlayerController.OnPlayerTeleported += Spawn;
    }

    // A saved campaign has no opening; its keepers are shown once it runs.
    internal static void EnableContinued(int slot)
    {
        Reset(slot);
        hostsRescues = true;
        MainGame.OnGameStarted += Activate;
    }

    // A joined campaign places its keepers after the host's game has loaded.
    internal static void EnableJoined(int slot) => Reset(slot);

    internal static void Activate()
    {
        MainGame.OnGameStarted -= Activate;
        Active = true;
    }

    internal static void Disable() => Clear();

    internal static KeeperChains Keeper(int slot) => chainSequences[slot - 1];

    internal static Vector3 Bay(int slot) => RaycastUtils.TrySnapToTheGround(Wake + SlotOffsets[slot - 1], 1f, 10f);

    internal static Vector3 Anchor(int slot) => ChainAnchor + SlotOffsets[slot - 1];

    // A chained player's keeper waits in its own bay while the bays are open.
    internal static KeeperChains Join(int slot, bool chained)
    {
        chainedMembers[slot - 1] = chained;
        if (chained && BaysOpen && slot != localSlot && chainSequences[slot - 1] == null)
            chainSequences[slot - 1] = SpawnKeeper(slot);
        return chainSequences[slot - 1];
    }

    internal static void Leave(int slot)
    {
        chainedMembers[slot - 1] = false;
        var chains = chainSequences[slot - 1];
        chainSequences[slot - 1] = null;
        if (chains == null)
            return;
        chains.gameObject.SetActive(false);
        Object.Destroy(chains.gameObject);
    }

    // Bays follow the wake position; a joined campaign uses the host's.
    internal static void OpenBays(Vector3 wake, Vector3 anchor)
    {
        Wake = wake;
        ChainAnchor = anchor;
        var scene = MainGame.PlayerController.CurrentGameScene;
        BayScene = scene.Id;
        keepers = new GameObject("Multiplayer Keepers");
        keepers.transform.SetParent(scene.transform, true);
        Active = true;
        BaysWereOpen = true;
        if (!hostsRescues && releasedShackles == null)
            LoadReleasedShackles();
        for (int slot = 1; slot <= chainedMembers.Length; slot++)
        {
            if (chainedMembers[slot - 1])
                Join(slot, true);
        }
        BaysOpened?.Invoke();
    }

    // The local keeper waits in its own bay with the native chained pose.
    internal static KeeperChains BindLocal()
    {
        var primary = MainGame.PlayerController;
        var animation = primary.View.PlayerAnimation;
        primary.SetPosition(Bay(localSlot));
        localBound = true;
        var holder = new GameObject($"Keeper {localSlot}");
        holder.transform.SetParent(keepers.transform, false);
        var chains = holder.AddComponent<KeeperChains>();
        chains.Init(animation, Anchor(localSlot));
        chains.SetReleasedShacklesSource(releasedShackles);
        animation.SetTrigger("mc_chained_start");
        return chainSequences[localSlot - 1] = chains;
    }

    // Clones the primary with independent data; the clone takes no input and has no collisions.
    internal static PlayerPhysicalBody CreateKeeper(Transform parent, int slot, Vector3 position,
        Vector3? chainAnchor = null)
    {
        var primary = MainGame.PlayerController;
        var data = PlayerData.CreatePlayerData();
        data.Guid.SetGuid(new SGuid());
        data.currentGameSceneId = primary.PlayerData.currentGameSceneId;
        data.Direction = Vector2.down;
        data.charState.Value = AnimationState.Idle;

        // Configure the clone before OnEnable can replace shared player references.
        var keeper = new GameObject($"Keeper {slot}");
        keeper.transform.SetParent(parent, false);
        keeper.SetActive(false);
        var body = Object.Instantiate(primary.PhysicalBody, keeper.transform);
        body.name = keeper.name;
        var controller = body.GetComponent<PlayerController>();
        ControllerData(controller) = data;
        BodyData(body) = data;
        foreach (var behaviour in body.GetComponents<MonoBehaviour>())
            behaviour.enabled = false;
        controller.PlayerInteractionComponent.enabled = false;
        body.IsActiveCombatant = false;
        foreach (var collider in keeper.GetComponentsInChildren<Collider>(true))
            collider.enabled = false;
        foreach (var tester in keeper.GetComponentsInChildren<PlayerColliderTester>(true))
            tester.enabled = false;

        // The native remote-player initializer also binds primary-player UI and input.
        // Keepers need only independent data, physics and visuals.
        var view = body.PlayerView;
        view.enabled = false;
        view.ControllingWispView = false;
        var animation = view.GetComponent<PlayerAnimation>();
        var chains = animation.Animator.transform.Find("gfx/bdy_over").GetComponent<GenericSprite>();
        // Preserve the original's applied shadow state through the clone's delayed Awake.
        CastShadows(chains) = chains.SpriteRenderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;
        // Only chained states show this overlay; others restore the pose the animator binds with,
        // so every clone binds with it hidden, even while the primary is chained.
        chains.gameObject.SetActive(false);
        body.SetPosition(RaycastUtils.TrySnapToTheGround(position, 1f, 10f));
        view.UpdatePosition(view.RoundedPosition);
        // Keep the animated and released chains on the same wall plane for consistent lighting.
        if (chainAnchor.HasValue)
            chains.transform.position = chainAnchor.Value;
        keeper.SetActive(true);
        body.Init();
        body.Rb.isKinematic = true;
        view.GetComponent<PlayerMovementAdjustComponent>().Init(controller);
        animation.ChangeSkinPreset(PlayerSkinHelper.CurrentPreset);
        animation.SetDirection(data.Direction);
        animation.SetState(AnimationState.Idle);
        if (chainAnchor.HasValue)
            animation.SetTrigger("mc_chained_start");
        LazySingleton<Microphone>.Instance.SetTarget(primary.View.transform);
        return body;
    }

    private static void Reset(int slot)
    {
        Clear();
        localSlot = slot;
        chainedMembers[slot - 1] = true;
        MainGame.OnGoToMainMenu += Clear;
    }

    private static void Spawn()
    {
        // The opening teleport places every keeper before the prison fade clears.
        PlayerController.OnPlayerTeleported -= Spawn;
        BaysAhead = false;
        OpenBays(MainGame.PlayerController.PhysicalBody.transform.position,
            MainGame.WorldData.gdPointsData.GetGDPointDataById("prison_wake_chain").Position);
    }

    private static KeeperChains SpawnKeeper(int slot)
    {
        var anchor = Anchor(slot);
        var body = CreateKeeper(keepers.transform, slot, Wake + SlotOffsets[slot - 1], anchor);
        var keeper = body.transform.parent.gameObject;
        var chainSequence = keeper.AddComponent<KeeperChains>();
        chainSequence.Init(body.PlayerView.PlayerAnimation, anchor);
        chainSequence.SetReleasedShacklesSource(releasedShackles);
        // Any free keeper with a pickaxe cuts another's shackles; the host applies every hit and
        // everyone sees the result. The host's own come off through the native opening.
        if (hostsRescues)
            KeeperRescueTarget.Create(chainSequence, keeper.transform, body.transform.position, chainSequence.ApplyPickaxeHit);
        else if (slot != 1)
            KeeperRescueTarget.Create(chainSequence, keeper.transform, body.transform.position, () => CutRequested?.Invoke(slot));
        KeeperAdded?.Invoke(slot, chainSequence);
        return chainSequence;
    }

    // The host's hanging shackles appear in its world when it escapes; a joined campaign
    // loaded before then gets its own hidden view of the native object instead.
    private static void LoadReleasedShackles()
    {
        const string id = "intro_prison_chain";
        var data = new WgoData
        {
            id = id,
            Position = ChainAnchor,
            WorldId = BayScene,
            isTempObject = true
        };
        Definition(data) = GameBalance.Me.GetData<WGODef>(id);
        CachedId(data) = id;
        data.SetDataFromDefinition();
        data.TryCreateMainWgoPartData();
        data.PrepareForGame();
        var wgo = Wgo.Spawn(data, keepers.transform, ignoreChunkRegistration: true);
        wgo.name = "Released Shackles";
        // Visible views load their parts synchronously; the loaded view caches its renderer.
        wgo.UpdateChunkVisibility(true);
        wgo.gameObject.SetActive(false);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Wgo), nameof(Wgo.CompleteVisualPartsLoad))]
    private static void CacheReleasedShackles(Wgo __instance)
    {
        // A joined campaign can load these before its bays open.
        if (localSlot == 0 || __instance.Data?.id != "intro_prison_chain" || __instance.MainWgoPart == null)
            return;

        releasedShackles = __instance.MainWgoPart.GetComponentInChildren<SpriteRenderer>(true);
        foreach (var chainSequence in chainSequences)
        {
            if (chainSequence != null)
                chainSequence.SetReleasedShacklesSource(releasedShackles);
        }
    }

    // The primary outlives the campaign, and the next opening needs its native chain overlay.
    private static void RestoreLocalKeeper()
    {
        if (!localBound)
            return;
        localBound = false;
        var animator = MainGame.PlayerController.View.PlayerAnimation.Animator;
        var view = animator.transform;
        view.Find("gfx/bdy_over").GetComponent<SpriteRenderer>().forceRenderingOff = false;
        view.Find("gfx (90 rotated)/-bdy_over").GetComponent<SpriteRenderer>().forceRenderingOff = false;
        // The menu rebinds the animator with its current pose as the default, so a chained one is left first.
        if (KeeperChains.IsChained(animator))
        {
            animator.Play(ReleasedPose);
            animator.Update(0f);
        }
    }

    private static void Clear()
    {
        PlayerController.OnPlayerTeleported -= Spawn;
        MainGame.OnGameStarted -= Activate;
        MainGame.OnGoToMainMenu -= Clear;
        RestoreLocalKeeper();
        Active = false;
        BaysWereOpen = false;
        BaysAhead = false;
        LocalChainedPending = false;
        hostsRescues = false;
        localSlot = 0;
        releasedShackles = null;
        System.Array.Clear(chainSequences, 0, chainSequences.Length);
        System.Array.Clear(chainedMembers, 0, chainedMembers.Length);
        if (keepers != null)
        {
            keepers.SetActive(false);
            Object.Destroy(keepers);
            keepers = null;
        }
    }
}
