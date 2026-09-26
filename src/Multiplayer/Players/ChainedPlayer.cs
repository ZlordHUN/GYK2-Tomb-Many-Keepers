using System.Collections.Generic;
using GYK2.TombManyKeepers.Features.MultiplayerKeepers;
using GYK2.TombManyKeepers.Network.Session;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Players;

// A joined player's keeper waits chained in its bay until the host frees it. It holds the
// keeper's control meanwhile; the pause key still works, and once the host cuts a shackle
// the interaction key struggles against the last one.
internal sealed class ChainedPlayer : MonoBehaviour
{
    // Collisions the freed keeper skips until it has walked off the prop it stood in.
    private readonly List<(Collider keeper, Collider prop)> steppingOff = new List<(Collider, Collider)>();
    private KeeperChains chains;
    private Transform overlay;
    private Vector3 anchor;
    private bool hinted;
    private bool released;

    internal static void Attach(KeeperChains chains, Vector3 anchor)
    {
        var player = chains.gameObject.AddComponent<ChainedPlayer>();
        player.chains = chains;
        player.anchor = anchor;
        player.overlay = MainGame.PlayerController.View.PlayerAnimation.Animator.transform.Find("gfx/bdy_over");
        MainGame.PlayerController.SetControlTakenType(TakenControlType.ByFlow, isEnabled: false);
    }

    private void Update()
    {
        if (released)
            return;
        if (chains.IsFree)
        {
            Release();
            return;
        }
        // Windows and scene changes take control for themselves.
        if (!MainGame.PlayerController.IsControlsEnabledExcept(TakenControlType.ByFlow))
            return;
        if (LazyInput.GetKeyDown(GameKey.InGameMenu))
        {
            LazyUI.GetWindow<UIGamePauseWindow>().Open(null);
            return;
        }
        if (chains.RemainingShackles != 1 || chains.IsBusy)
            return;
        if (!hinted)
        {
            hinted = true;
            LazySingleton<UINotificator>.Instance.ShowSimpleTextNotification(
                ControllerIconLibrary.GetIconId(GameKey.Interaction) + "Struggle against the last shackle");
        }
        if (LazyInput.GetKeyDown(GameKey.Interaction))
            CoopSession.RequestStruggle();
    }

    // A bay can hold a native prop where the keeper stands, such as a chain pile. The chained keeper
    // is kinematic; once free it steps off such props instead of being pushed out of them.
    private void Release()
    {
        released = true;
        var controller = MainGame.PlayerController;
        foreach (var keeper in controller.PhysicalBody.GetComponentsInChildren<Collider>())
        {
            if (!keeper.enabled || keeper.isTrigger)
                continue;
            var bounds = keeper.bounds;
            foreach (var prop in Physics.OverlapBox(bounds.center, bounds.extents, Quaternion.identity, Physics.AllLayers,
                         QueryTriggerInteraction.Ignore))
            {
                if (prop.attachedRigidbody == keeper.attachedRigidbody ||
                    Physics.GetIgnoreLayerCollision(keeper.gameObject.layer, prop.gameObject.layer) || !Pushes(keeper, prop))
                    continue;
                Physics.IgnoreCollision(keeper, prop, true);
                steppingOff.Add((keeper, prop));
            }
        }
        controller.SetControlTakenType(TakenControlType.ByFlow, isEnabled: true);
        if (steppingOff.Count == 0)
            Destroy(this);
    }

    private void FixedUpdate()
    {
        for (int i = steppingOff.Count - 1; i >= 0; i--)
        {
            var (keeper, prop) = steppingOff[i];
            if (keeper != null && prop != null && Pushes(keeper, prop))
                continue;
            if (keeper != null && prop != null)
                Physics.IgnoreCollision(keeper, prop, false);
            steppingOff.RemoveAt(i);
        }
        if (released && steppingOff.Count == 0)
            Destroy(this);
    }

    // A prop pushes a keeper standing in it sideways; the floor only holds it up.
    private static bool Pushes(Collider keeper, Collider prop) =>
        Physics.ComputePenetration(keeper, keeper.transform.position, keeper.transform.rotation,
            prop, prop.transform.position, prop.transform.rotation, out var direction, out float distance) &&
        distance > 0.01f && Mathf.Abs(direction.y) < 0.5f;

    // The animator writes the chain's native offset every frame; this bay's wall is elsewhere,
    // where the released shackles will hang.
    private void LateUpdate()
    {
        if (!chains.IsFree)
            overlay.position = anchor;
    }

    private void OnDestroy()
    {
        foreach (var (keeper, prop) in steppingOff)
        {
            if (keeper != null && prop != null)
                Physics.IgnoreCollision(keeper, prop, false);
        }
        if (!released && MainGame.Instance != null && MainGame.PlayerController != null)
            MainGame.PlayerController.SetControlTakenType(TakenControlType.ByFlow, isEnabled: true);
    }
}
