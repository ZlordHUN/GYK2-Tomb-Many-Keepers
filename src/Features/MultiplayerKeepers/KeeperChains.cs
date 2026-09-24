using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Features.MultiplayerKeepers;

internal sealed class KeeperChains : MonoBehaviour
{
    private enum Motion
    {
        None,
        FirstRelease,
        Struggle,
        FinalRelease
    }

    private static readonly int OneChainPose = Animator.StringToHash("chained_state_02_loop");
    private static readonly int StruggleAnimation = Animator.StringToHash("chained_state_02_try");
    private static readonly int FinalReleaseAnimation = Animator.StringToHash("chained_state_02_transit");

    private PlayerAnimation animation;
    private Motion motion;
    private bool motionStarted;
    private bool finalAnimationComplete;
    private int struggles;
    private Vector3 wallAnchor;
    private SpriteRenderer releasedShacklesSource;

    internal int RemainingShackles { get; private set; } = 2;
    internal bool IsBusy => motion != Motion.None;
    internal bool IsReleased { get; private set; }
    internal event System.Action ReleaseStarted;

    internal void Init(PlayerAnimation animation, Vector3 anchor)
    {
        this.animation = animation;
        wallAnchor = anchor;
        enabled = false;
    }

    internal void ApplyPickaxeHit()
    {
        if (RemainingShackles == 0)
            return;

        RemainingShackles--;
        // A second contact during a clip is handled when that clip reaches its resting pose.
        if (IsBusy)
            return;

        BeginMotion(RemainingShackles == 1 ? Motion.FirstRelease : Motion.FinalRelease);
    }

    internal bool TryStruggle()
    {
        if (RemainingShackles != 1 || IsBusy || IsReleased)
            return false;

        if (++struggles == 3)
        {
            RemainingShackles = 0;
            BeginMotion(Motion.FinalRelease);
        }
        else
        {
            BeginMotion(Motion.Struggle);
        }
        return true;
    }

    internal void SetReleasedShacklesSource(SpriteRenderer source)
    {
        releasedShacklesSource = source;
        if (finalAnimationComplete && !IsReleased && releasedShacklesSource != null)
            FinishRelease();
    }

    private void BeginMotion(Motion next)
    {
        motion = next;
        motionStarted = false;
        enabled = true;
        if (next == Motion.FinalRelease)
            ReleaseStarted?.Invoke();
        animation.SetTrigger(next switch
        {
            Motion.FirstRelease => "mc_chained_transit_01",
            Motion.Struggle => "mc_chained_try_02",
            _ => "mc_chained_transit_02"
        });
        if (next != Motion.FirstRelease)
            LazyAudio.Play(next == Motion.Struggle ? "intro_prison_chain_try" : "intro_prison_chain_final");
    }

    private void LateUpdate()
    {
        int state = animation.Animator.GetCurrentAnimatorStateInfo(0).shortNameHash;
        if (motion == Motion.FinalRelease)
        {
            if (state == FinalReleaseAnimation)
            {
                motionStarted = true;
                return;
            }
            if (!motionStarted)
                return;

            finalAnimationComplete = true;
            enabled = false;
            if (releasedShacklesSource != null)
                FinishRelease();
            return;
        }

        if (motion == Motion.Struggle && state == StruggleAnimation)
            motionStarted = true;
        if (state != OneChainPose || (motion == Motion.Struggle && !motionStarted))
            return;

        if (RemainingShackles == 0)
            BeginMotion(Motion.FinalRelease);
        else
        {
            motion = Motion.None;
            enabled = false;
        }
    }

    private void FinishRelease()
    {
        var shackles = Object.Instantiate(releasedShacklesSource.gameObject, transform, true);
        shackles.name = name + " Shackles";
        shackles.transform.position = wallAnchor;

        // The skin updater can re-enable these renderers after the native animation exits.
        var view = animation.Animator.transform;
        view.Find("gfx/bdy_over").GetComponent<SpriteRenderer>().forceRenderingOff = true;
        view.Find("gfx (90 rotated)/-bdy_over").GetComponent<SpriteRenderer>().forceRenderingOff = true;
        IsReleased = true;
        motion = Motion.None;
        enabled = false;
    }
}
