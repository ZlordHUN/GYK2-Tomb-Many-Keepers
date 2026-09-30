using System.Collections.Generic;
using GYK2.TombManyKeepers.Network.Session;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Players;

// A joined player's keeper waits chained in its bay until the host frees it. As the native opening does
// for the host's keeper, the chains lock only its movement: the player keeps the game's controls, and once
// the host cuts a shackle the game's own prompt offers the struggle against the last one. Each chain
// motion holds the keeper, as the opening's own struggles do.
internal sealed class ChainedPlayer : MonoBehaviour
{
    // A struggle the host never answers gives the keeper back.
    private const float AnswerTime = 3f;
    // Collisions the freed keeper skips until it has walked off the prop it stood in.
    private readonly List<(Collider keeper, Collider prop)> steppingOff = new List<(Collider, Collider)>();
    private KeeperChains chains;
    private KeeperStruggleTarget lastShackle;
    private Transform overlay;
    private Vector3 anchor;
    private Vector3 bay;
    private float askedAt = -1f;
    private bool offered;
    private bool holding;
    private bool released;

    internal static void Attach(KeeperChains chains, Vector3 anchor)
    {
        var controller = MainGame.PlayerController;
        var player = chains.gameObject.AddComponent<ChainedPlayer>();
        player.chains = chains;
        player.anchor = anchor;
        player.bay = controller.PhysicalBody.transform.position;
        player.overlay = controller.View.PlayerAnimation.Animator.transform.Find("gfx/bdy_over");
        // The native opening's chained keeper faces no way, so its interaction finds the chain before it
        // whichever way this player last walked; a locked keeper keeps its facing until it moves again.
        controller.PlayerData.Direction = Vector2.zero;
        controller.OnControlStateChanged += player.HoldStill;
        controller.PhysicalBody.LockMovement(true);
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
        if (chains.RemainingShackles == 1 && !offered)
        {
            offered = true;
            lastShackle = KeeperStruggleTarget.Create(transform, bay, Struggle);
        }
        else if (chains.RemainingShackles != 1 && lastShackle != null)
            Retire();
        // The host's answer starts the motion this struggle waits for.
        if (chains.IsBusy || Time.unscaledTime > askedAt + AnswerTime)
            askedAt = -1f;
        Hold(chains.IsBusy || askedAt >= 0f);
    }

    private void Struggle()
    {
        if (chains.RemainingShackles != 1 || chains.IsBusy || askedAt >= 0f)
            return;
        askedAt = Time.unscaledTime;
        Hold(true);
        CoopSession.RequestStruggle();
    }

    private void Hold(bool hold)
    {
        if (hold == holding)
            return;
        holding = hold;
        MainGame.PlayerController.SetControlTakenType(TakenControlType.ByFlow, isEnabled: !hold);
    }

    // Returned control makes the keeper's body dynamic again; the chains keep it kinematic, as a bay's
    // prop would push a dynamic keeper out of where it stands.
    private void HoldStill()
    {
        var body = MainGame.PlayerController.PhysicalBody;
        if (!body.Rb.isKinematic && body.gameObject.activeInHierarchy)
            body.LockMovement(true);
    }

    private void Retire()
    {
        lastShackle.Retire();
        lastShackle = null;
    }

    // A bay can hold a native prop where the keeper stands, such as a chain pile. The chained keeper
    // is kinematic; once free it steps off such props instead of being pushed out of them.
    private void Release()
    {
        released = true;
        if (lastShackle != null)
            Retire();
        var controller = MainGame.PlayerController;
        controller.OnControlStateChanged -= HoldStill;
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
        Hold(false);
        controller.PhysicalBody.LockMovement(false);
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
        if (released || MainGame.Instance == null || MainGame.PlayerController == null)
            return;
        var controller = MainGame.PlayerController;
        controller.OnControlStateChanged -= HoldStill;
        if (holding)
            controller.SetControlTakenType(TakenControlType.ByFlow, isEnabled: true);
        // Leaving for the menu has already unlocked the keeper, whose body is put away.
        if (controller.PhysicalBody.gameObject.activeInHierarchy)
            controller.PhysicalBody.LockMovement(false);
    }
}
