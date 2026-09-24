using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Features.MultiplayerKeepers;

[HarmonyPatch]
internal static class KeeperSpawn
{
    private static readonly Vector3[] SpawnOffsets =
    {
        Vector3.left * 4.42f,
        Vector3.left * 2.18f,
        Vector3.right * 14.14f
    };
    private static readonly AccessTools.FieldRef<PlayerController, PlayerData> ControllerData =
        AccessTools.FieldRefAccess<PlayerController, PlayerData>("playerData");
    private static readonly AccessTools.FieldRef<PlayerPhysicalBody, PlayerData> BodyData =
        AccessTools.FieldRefAccess<PlayerPhysicalBody, PlayerData>("playerData");
    private static readonly AccessTools.FieldRef<GenericSprite, bool> CastShadows =
        AccessTools.FieldRefAccess<GenericSprite, bool>("castShadows");

    private static GameObject keepers;
    private static KeeperChains[] chainSequences;

    internal static void Enable()
    {
        Clear();
        PlayerController.OnPlayerTeleported += Spawn;
        MainGame.OnGoToMainMenu += Clear;
    }

    private static void Spawn()
    {
        // The opening teleport places every keeper before the prison fade clears.
        PlayerController.OnPlayerTeleported -= Spawn;
        var primary = MainGame.PlayerController;
        keepers = new GameObject("Multiplayer Keepers");
        keepers.transform.SetParent(primary.CurrentGameScene.transform, true);
        chainSequences = new KeeperChains[SpawnOffsets.Length];
        for (int i = 0; i < SpawnOffsets.Length; i++)
            chainSequences[i] = SpawnKeeper(primary, i + 2, SpawnOffsets[i]);
        LazySingleton<Microphone>.Instance.SetTarget(primary.View.transform);
    }

    private static KeeperChains SpawnKeeper(PlayerController primary, int number, Vector3 offset)
    {
        var data = PlayerData.CreatePlayerData();
        data.Guid.SetGuid(new SGuid());
        data.currentGameSceneId = primary.PlayerData.currentGameSceneId;
        data.Direction = Vector2.down;
        data.charState.Value = AnimationState.Idle;

        // Configure the clone before OnEnable can replace shared player references.
        var keeper = new GameObject($"Keeper {number}");
        keeper.transform.SetParent(keepers.transform, false);
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
        // This stationary prototype needs only independent data, physics and visuals.
        var view = body.PlayerView;
        view.enabled = false;
        view.ControllingWispView = false;
        // The primary is already chained; the clone's default pose must hide the chain overlay.
        var animation = view.GetComponent<PlayerAnimation>();
        var chains = animation.Animator.transform.Find("gfx/bdy_over").GetComponent<GenericSprite>();
        // Preserve the original's applied shadow state through the clone's delayed Awake.
        CastShadows(chains) = chains.SpriteRenderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;
        chains.gameObject.SetActive(false);
        body.SetPosition(RaycastUtils.TrySnapToTheGround(primary.PhysicalBody.transform.position + offset, 1f, 10f));
        view.UpdatePosition(view.RoundedPosition);
        // Keep the animated and released chains on the same wall plane for consistent lighting.
        chains.transform.position = MainGame.WorldData.gdPointsData.GetGDPointDataById("prison_wake_chain").Position + offset;
        keeper.SetActive(true);
        body.Init();
        body.Rb.isKinematic = true;
        view.GetComponent<PlayerMovementAdjustComponent>().Init(controller);
        animation.ChangeSkinPreset(PlayerSkinHelper.CurrentPreset);
        animation.SetDirection(data.Direction);
        animation.SetState(AnimationState.Idle);
        animation.SetTrigger("mc_chained_start");
        var chainSequence = keeper.AddComponent<KeeperChains>();
        chainSequence.Init(animation, chains.transform.position);
        KeeperRescueTarget.Create(chainSequence, keeper.transform, body.transform.position);
        return chainSequence;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Wgo), nameof(Wgo.CompleteVisualPartsLoad))]
    private static void CacheReleasedShackles(Wgo __instance)
    {
        if (keepers == null || __instance.Data?.id != "intro_prison_chain" ||
            __instance.MainWgoPart == null)
            return;

        var source = __instance.MainWgoPart.GetComponentInChildren<SpriteRenderer>(true);
        foreach (var chainSequence in chainSequences)
            chainSequence.SetReleasedShacklesSource(source);
    }

    private static void Clear()
    {
        PlayerController.OnPlayerTeleported -= Spawn;
        MainGame.OnGoToMainMenu -= Clear;
        chainSequences = null;
        if (keepers != null)
        {
            keepers.SetActive(false);
            Object.Destroy(keepers);
            keepers = null;
        }
    }
}
