using System.Collections.Generic;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Features.MultiplayerKeepers;

internal sealed class KeeperRescueTarget : MonoBehaviour
{
    private const string TargetId = "tmk_keeper_shackles";
    private static readonly AccessTools.FieldRef<ObjectLinkedToDefinition<WGODef>, WGODef> Definition =
        AccessTools.FieldRefAccess<ObjectLinkedToDefinition<WGODef>, WGODef>("definition");
    private static readonly AccessTools.FieldRef<ObjectLinkedToDefinition<WGODef>, string> CachedId =
        AccessTools.FieldRefAccess<ObjectLinkedToDefinition<WGODef>, string>("cachedId");
    private static readonly AccessTools.FieldRef<WgoPart, List<DockPoint>> DockPoints =
        AccessTools.FieldRefAccess<WgoPart, List<DockPoint>>("dockPoints");

    private static readonly AccessTools.FieldRef<ToolComponent, bool> AnimationTakesControl =
        AccessTools.FieldRefAccess<ToolComponent, bool>("isControlTakenByAnimation");
    private static readonly AccessTools.FieldRef<AnimationComponentBase, System.Action<ItemType>> ToolLoopFinished =
        AccessTools.FieldRefAccess<AnimationComponentBase, System.Action<ItemType>>("OnToolLoopFinished");

    private KeeperChains chains;
    private Wgo target;
    private DockPoint dock;
    private ToolComponent finishingTool;
    private bool retiring;

    internal static void Create(KeeperChains chains, Transform parent, Vector3 position)
    {
        var definition = new WGODef
        {
            id = TargetId,
            interactionType = WGODef.InteractionType.Work,
            hp = 2,
            noMasteryLock = true,
            masteryLock = new LazyExpression("0"),
            talent = string.Empty,
            energyPerTick = new LazyExpression("0"),
            insanityPerTick = new LazyExpression("0"),
            hasCustomAssetId = true
        };
        definition.toolAction.actionableTool = ItemType.Pickaxe;
        definition.customAssetId.FromString("intro_prison_chain_player", PureValueType.String);
        var data = new WgoData
        {
            id = TargetId,
            Position = position,
            WorldId = MainGame.PlayerData.currentGameSceneId,
            isTempObject = true
        };
        Definition(data) = definition;
        CachedId(data) = TargetId;
        data.SetDataFromDefinition();
        data.TryCreateMainWgoPartData();
        data.PrepareForGame();
        // This view never enters WorldData; its zero-HP callback must not remove world objects.
        data.HpComponent.Init();

        var wgo = Wgo.Spawn(data, parent, ignoreChunkRegistration: true);
        wgo.name = parent.name + " Rescue Target";
        // Visible views load their parts synchronously, including uncached addressables.
        wgo.UpdateChunkVisibility(true);
        if (wgo.MainWgoPart == null)
        {
            Object.Destroy(wgo.gameObject);
            throw new System.InvalidOperationException("Could not load the keeper rescue target.");
        }
        var rescue = wgo.gameObject.AddComponent<KeeperRescueTarget>();
        rescue.chains = chains;
        rescue.target = wgo;
        var point = new GameObject("Rescue Work Point") { layer = 7 };
        point.transform.SetParent(wgo.MainWgoPart.transform, false);
        point.transform.position = RaycastUtils.TrySnapToTheGround(position + Vector3.back * 0.9f, 1f, 10f);
        var collider = point.AddComponent<SphereCollider>();
        collider.isTrigger = true;
        collider.radius = 0.1f;
        rescue.dock = point.AddComponent<DockPoint>();
        rescue.dock.direction = Direction.Up;
        rescue.dock.Init(wgo.MainWgoPart);
        DockPoints(wgo.MainWgoPart).Add(rescue.dock);
        data.OnToolTickApply += rescue.ApplyHit;
        chains.ReleaseStarted += rescue.Retire;
    }

    private void ApplyHit(bool isFirstHit)
    {
        chains.ApplyPickaxeHit();
        if (target.Data.HpComponent.Hp == 0)
            Retire();
    }

    private void Retire()
    {
        if (retiring)
            return;
        retiring = true;
        target.Data.IsInteractable = false;
        target.SetInteractableCollidersState(false);
        dock.gameObject.SetActive(false);
        // Self-release after the first hit must also stop a held native work action.
        target.Data.HpComponent.SetCustomHpValue(0, overrideMaxHpValue: false);
        var work = MainGame.PlayerController.PlayerWorkComponent;
        if (work.Wgo == target && work.ToolComponent.IsActionActive)
        {
            finishingTool = work.ToolComponent;
            finishingTool.OnInteractionStop += DestroyTarget;
        }
        else
            DestroyTarget();
    }

    private void DestroyTarget()
    {
        Object.Destroy(gameObject);
    }

    private void OnDisable()
    {
        if (finishingTool != null)
        {
            finishingTool.OnInteractionStop -= DestroyTarget;
            finishingTool = null;
        }
        var player = MainGame.PlayerController;
        if (ReferenceEquals(target, null) || player == null)
            return;
        var work = player.PlayerWorkComponent;
        var tool = work.ToolComponent;
        if (!ReferenceEquals(work.Wgo, target) &&
            !(tool.IsActionActive && tool.ToolActor is PlayerActivity activity &&
                ReferenceEquals(activity.WgoData, target.Data)))
            return;

        // Scene teardown will rebind the animator, so a deferred loop-end stop cannot finish.
        // Remove this tool's pending stops before another campaign can start a new work loop.
        var animation = player.View.PlayerAnimation;
        var callbacks = ToolLoopFinished(animation);
        if (callbacks != null)
        {
            foreach (System.Action<ItemType> callback in callbacks.GetInvocationList())
                if (ReferenceEquals(callback.Target, tool))
                    animation.OnToolLoopFinished -= callback;
        }
        AnimationTakesControl(tool) = false;
        work.StopInteraction();
    }

    private void OnDestroy()
    {
        // Sibling Unity components may already be destroyed; their managed events still need detaching.
        if (!ReferenceEquals(chains, null))
            chains.ReleaseStarted -= Retire;
        var data = target?.Data;
        if (data != null)
            data.OnToolTickApply -= ApplyHit;
    }
}
