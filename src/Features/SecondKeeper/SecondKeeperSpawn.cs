using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Features.SecondKeeper;

[HarmonyPatch]
internal static class SecondKeeperSpawn
{
    private static readonly Vector3 SpawnOffset = Vector3.left * 4.42f;
    private static readonly AccessTools.FieldRef<PlayerController, PlayerData> ControllerData =
        AccessTools.FieldRefAccess<PlayerController, PlayerData>("playerData");
    private static readonly AccessTools.FieldRef<PlayerPhysicalBody, PlayerData> BodyData =
        AccessTools.FieldRefAccess<PlayerPhysicalBody, PlayerData>("playerData");
    private static readonly AccessTools.FieldRef<GenericSprite, bool> CastShadows =
        AccessTools.FieldRefAccess<GenericSprite, bool>("castShadows");

    private static GameObject keeper;
    private static GameObject shackles;
    private static PlayerController primary;
    private static PlayerAnimation animation;

    internal static void Enable()
    {
        Clear();
        PlayerController.OnPlayerTeleported += Spawn;
        MainGame.OnGoToMainMenu += Clear;
    }

    private static void Spawn()
    {
        // The opening teleport places both keepers before the prison fade clears.
        PlayerController.OnPlayerTeleported -= Spawn;
        primary = MainGame.PlayerController;
        var data = PlayerData.CreatePlayerData();
        data.Guid.SetGuid(new SGuid());
        data.currentGameSceneId = primary.PlayerData.currentGameSceneId;
        data.Direction = Vector2.down;
        data.charState.Value = AnimationState.Idle;

        // Configure the clone before OnEnable can replace shared player references.
        keeper = new GameObject("Second Keeper");
        keeper.SetActive(false);
        var body = Object.Instantiate(primary.PhysicalBody, keeper.transform);
        body.name = "Second Keeper";
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
        animation = view.GetComponent<PlayerAnimation>();
        var chains = animation.Animator.transform.Find("gfx/bdy_over").GetComponent<GenericSprite>();
        // Preserve the original's applied shadow state through the clone's delayed Awake.
        CastShadows(chains) = chains.SpriteRenderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;
        chains.gameObject.SetActive(false);
        // Align with the empty shackles in the prison's left wall bay.
        body.SetPosition(RaycastUtils.TrySnapToTheGround(primary.PhysicalBody.transform.position + SpawnOffset, 1f, 10f));
        view.UpdatePosition(view.RoundedPosition);
        // Keep the animated and released chains on the same wall plane for consistent lighting.
        chains.transform.position = MainGame.WorldData.gdPointsData.GetGDPointDataById("prison_wake_chain").Position + SpawnOffset;
        keeper.SetActive(true);
        body.Init();
        body.Rb.isKinematic = true;
        view.GetComponent<PlayerMovementAdjustComponent>().Init(controller);
        animation.ChangeSkinPreset(PlayerSkinHelper.CurrentPreset);
        animation.SetDirection(data.Direction);
        animation.SetState(AnimationState.Idle);
        animation.SetTrigger("mc_chained_start");
        LazySingleton<Microphone>.Instance.SetTarget(primary.View.transform);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(AnimationComponentBase), nameof(AnimationComponentBase.SetTrigger), new[] { typeof(string) })]
    private static void MirrorChainAnimation(AnimationComponentBase __instance, string trigger)
    {
        if (animation != null && __instance == primary.View.PlayerAnimation &&
            trigger.StartsWith("mc_chained_", System.StringComparison.Ordinal))
            animation.SetTrigger(trigger);
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(Wgo), nameof(Wgo.CompleteVisualPartsLoad))]
    private static void LeaveShackles(Wgo __instance)
    {
        if (keeper == null || shackles != null || __instance.Data?.id != "intro_prison_chain" ||
            __instance.MainWgoPart == null)
            return;

        // The game leaves a separate wall visual after the release animation finishes.
        var source = __instance.MainWgoPart.GetComponentInChildren<SpriteRenderer>(true);
        // Retire both renderers atomically; the skin updater can re-enable them later this frame.
        var animatedView = animation.Animator.transform;
        animatedView.Find("gfx/bdy_over").GetComponent<SpriteRenderer>().forceRenderingOff = true;
        animatedView.Find("gfx (90 rotated)/-bdy_over").GetComponent<SpriteRenderer>().forceRenderingOff = true;
        shackles = Object.Instantiate(source.gameObject, keeper.transform, true);
        shackles.name = "Second Keeper Shackles";
        shackles.transform.position += SpawnOffset;
    }

    private static void Clear()
    {
        PlayerController.OnPlayerTeleported -= Spawn;
        MainGame.OnGoToMainMenu -= Clear;
        primary = null;
        animation = null;
        shackles = null;
        if (keeper != null)
        {
            keeper.SetActive(false);
            Object.Destroy(keeper);
            keeper = null;
        }
    }
}
