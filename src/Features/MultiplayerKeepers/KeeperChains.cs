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
    // Native chained states by the shackles still held while they play.
    private static readonly int[] TwoShackleStates = Hashes("chained_state_01_loop", "chained_state_01_try");
    private static readonly int[] OneShackleStates = Hashes("chained_state_01_transit", "chained_state_02_loop", "chained_state_02_try");
    private static readonly int[] ChainTriggers = Hashes("mc_chained_start", "mc_chained_try_01", "mc_chained_transit_01",
        "mc_chained_try_02", "mc_chained_transit_02");
    private static readonly int StartTrigger = Animator.StringToHash("mc_chained_start");
    private static readonly int FirstStruggleTrigger = Animator.StringToHash("mc_chained_try_01");
    private static readonly int FirstReleaseTrigger = Animator.StringToHash("mc_chained_transit_01");
    private static readonly int StruggleTrigger = Animator.StringToHash("mc_chained_try_02");
    private static readonly int FinalReleaseTrigger = Animator.StringToHash("mc_chained_transit_02");

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
    // Free once the release clip ends, even while the wall shackles wait for their source.
    internal bool IsFree => IsReleased || finalAnimationComplete;
    internal event System.Action ReleaseStarted;
    // Each native chain trigger this keeper plays, so other players can replay it.
    internal event System.Action<int> MotionStarted;

    internal static bool IsChainTrigger(int trigger) => System.Array.IndexOf(ChainTriggers, trigger) >= 0;

    // Shackles shown by a native chained pose; the final release clip already counts as free.
    internal static int Shackles(Animator animator)
    {
        int state = animator.GetCurrentAnimatorStateInfo(0).shortNameHash;
        return System.Array.IndexOf(TwoShackleStates, state) >= 0 ? 2
            : System.Array.IndexOf(OneShackleStates, state) >= 0 ? 1 : 0;
    }

    internal static bool IsChained(Animator animator) =>
        Shackles(animator) > 0 || animator.GetCurrentAnimatorStateInfo(0).shortNameHash == FinalReleaseAnimation;

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

    // Another player's native chain triggers replay through the matching motions.
    internal bool Mirror(int trigger)
    {
        if (trigger == FirstStruggleTrigger)
        {
            // A struggle against both shackles changes nothing but the pose.
            if (RemainingShackles != 2 || IsBusy)
                return false;
            animation.SetTrigger(trigger);
            return true;
        }
        if (trigger == FirstReleaseTrigger && RemainingShackles == 2)
            RemainingShackles = 1;
        else if (trigger == FinalReleaseTrigger && RemainingShackles == 1)
            RemainingShackles = 0;
        else if (trigger != StruggleTrigger || RemainingShackles != 1)
            return false;
        BeginMotion(trigger == FirstReleaseTrigger ? Motion.FirstRelease
            : trigger == StruggleTrigger ? Motion.Struggle : Motion.FinalRelease);
        return true;
    }

    // A keeper whose rescue already progressed starts in the matching pose.
    internal void Restore(int shackles)
    {
        if (shackles >= RemainingShackles || IsBusy || IsFree)
            return;
        if (shackles == 0)
        {
            ReleaseNow();
            return;
        }
        RemainingShackles = 1;
        animation.Animator.ResetTrigger(StartTrigger);
        animation.Animator.Play(OneChainPose);
    }

    // A player already free in their own game skips the release clip.
    internal void ReleaseNow()
    {
        RemainingShackles = 0;
        ReleaseStarted?.Invoke();
        // A keeper spawned this frame still has its chained start pending.
        animation.Animator.ResetTrigger(StartTrigger);
        animation.SetTrigger(AnimationComponentBase.RESET_TO_IDLE_TRIGGER);
        finalAnimationComplete = true;
        enabled = false;
        if (releasedShacklesSource != null)
            FinishRelease();
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
        string trigger = next switch
        {
            Motion.FirstRelease => "mc_chained_transit_01",
            Motion.Struggle => "mc_chained_try_02",
            _ => "mc_chained_transit_02"
        };
        animation.SetTrigger(trigger);
        if (next != Motion.FirstRelease)
            LazyAudio.Play(next == Motion.Struggle ? "intro_prison_chain_try" : "intro_prison_chain_final");
        MotionStarted?.Invoke(Animator.StringToHash(trigger));
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

    private static int[] Hashes(params string[] names) => System.Array.ConvertAll(names, Animator.StringToHash);
}
