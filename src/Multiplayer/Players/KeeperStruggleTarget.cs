using System.Collections.Generic;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Players;

// The native opening's chain as a joined player's keeper meets it: where the host's keeper finds its chain, the
// game's own interaction offers "Try To Remove" over the last shackle, and the interaction key struggles.
[HarmonyPatch]
internal sealed class KeeperStruggleTarget : MonoBehaviour
{
    private const string TargetId = "tmk_keeper_struggle";
    // The native chain's place from the keeper it holds, as the opening sets them.
    private static readonly Vector3 ChainOffset = new Vector3(-0.04f, 0f, -0.325f);
    private static readonly AccessTools.FieldRef<ObjectLinkedToDefinition<WGODef>, WGODef> Definition =
        AccessTools.FieldRefAccess<ObjectLinkedToDefinition<WGODef>, WGODef>("definition");
    private static readonly AccessTools.FieldRef<ObjectLinkedToDefinition<WGODef>, string> CachedId =
        AccessTools.FieldRefAccess<ObjectLinkedToDefinition<WGODef>, string>("cachedId");
    private static readonly AccessTools.FieldRef<WGOInteractionHandlerBase, Wgo> AssignedWgo =
        AccessTools.FieldRefAccess<WGOInteractionHandlerBase, Wgo>("assignedWgo");

    private System.Action struggle;
    private Wgo target;

    internal static KeeperStruggleTarget Create(Transform parent, Vector3 keeper, System.Action struggle)
    {
        var definition = new WGODef
        {
            id = TargetId,
            interactionType = WGODef.InteractionType.Script,
            hasCustomAssetId = true,
            customInteraction = new CustomInteraction
            {
                condition = new LazyExpression(),
                execution = new List<LazyExpression>(),
                hint = "hint_try_to_remove"
            }
        };
        definition.customAssetId.FromString("intro_prison_chain_player", PureValueType.String);
        var data = new WgoData
        {
            id = TargetId,
            Position = keeper + ChainOffset,
            WorldId = MainGame.PlayerData.currentGameSceneId,
            isTempObject = true
        };
        Definition(data) = definition;
        CachedId(data) = TargetId;
        data.SetDataFromDefinition();
        data.TryCreateMainWgoPartData();
        data.PrepareForGame();

        var wgo = Wgo.Spawn(data, parent, ignoreChunkRegistration: true);
        wgo.name = parent.name + " Struggle Target";
        // Visible views load their parts synchronously, including uncached addressables.
        wgo.UpdateChunkVisibility(true);
        if (wgo.MainWgoPart == null)
        {
            Object.Destroy(wgo.gameObject);
            throw new System.InvalidOperationException("Could not load the keeper struggle target.");
        }
        var chain = wgo.gameObject.AddComponent<KeeperStruggleTarget>();
        chain.target = wgo;
        chain.struggle = struggle;
        return chain;
    }

    // The prompt leaves with the interaction before the chain goes.
    internal void Retire()
    {
        target.Data.IsInteractable = false;
        target.SetInteractableCollidersState(false);
        Destroy(gameObject);
    }

    // The native chain's interaction runs the host's story script; this one struggles instead.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(ScriptInteractionHandler), nameof(ScriptInteractionHandler.Interact))]
    private static bool Interact(ScriptInteractionHandler __instance, ref bool __result)
    {
        var wgo = AssignedWgo(__instance);
        if (wgo == null || !wgo.TryGetComponent(out KeeperStruggleTarget chain))
            return true;
        chain.struggle();
        __result = true;
        return false;
    }
}
