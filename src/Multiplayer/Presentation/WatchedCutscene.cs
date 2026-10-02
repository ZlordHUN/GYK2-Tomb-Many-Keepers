using System;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Multiplayer.Session;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// Another player's cutscene plays for everyone in its scene, as GYK1's did: its lines, characters and effects show in
// the world to players roaming freely. Its letterbox, the view of its camera, its fades and its sounds take over only
// for a player who comes close to the keeper whose cutscene it is, about a third of a screen, as GYK1's did; that
// player's keeper is then held until the cutscene ends, walks over beside that keeper, on the side it does not face,
// faces as it faces and keeps by it if it moves. A keeper still in its chains, which cannot come closer, watches from
// its bay. A cutscene stays in its scene: a player taking part stops once the keeper whose cutscene it is leaves it, and
// a cutscene that begins as a keeper takes an exit, such as the prison's door, is theirs alone, since only they leave.
// The others follow by taking the exit themselves, and a player arriving in a scene, by an exit or by joining the game,
// joins a cutscene running there wherever their keeper stands, where it has got to.
[HarmonyPatch]
internal static class WatchedCutscene
{
    private enum Fade : byte
    {
        In,
        Out,
        InInstant,
        OutInstant
    }

    private const float CameraInterval = 0.05f;
    // A player who joins or arrives during a cutscene starts watching it at the next reminder.
    private const float CinematicInterval = 1f;
    // Whether the story keeps a screen dark, repeated for players who arrive or missed its end.
    private const float ScreenInterval = 1f;
    private const float BrightenTime = 0.3f;
    private static readonly AccessTools.FieldRef<UIBasicFade, MultiFlagAND<FadeFlag>> FadeFlags =
        AccessTools.FieldRefAccess<UIBasicFade, MultiFlagAND<FadeFlag>>("fadeFlags");
    private static readonly AccessTools.FieldRef<UIBasicFade, CanvasGroup> Blackout =
        AccessTools.FieldRefAccess<UIBasicFade, CanvasGroup>("blackoutCanvas");
    // The watcher's camera flies to the cutscene and back, as a native camera fly does.
    private const float FlyTime = 0.5f;
    // How quickly the watched view closes in on each shared camera point.
    private const float Smoothing = 12f;
    // GYK1's distances, in its tiles of about two of this game's units: the cutscene takes over within 3.6 tiles, the
    // keeper stands half a tile beside the other, walks after it once a tile away, and hurries past 1.25 tiles.
    private const float JoinDistance = 7f, Beside = 1f, FollowFrom = 1.8f, HurryFrom = 2.4f;
    // The story's own walking pace for a keeper in a cutscene, and a hurried one.
    private const float WalkSpeed = 1.5f, HurrySpeed = 2.2f;
    private const float FollowInterval = 0.25f, WalkTimeout = 10f, WalkGrace = 0.25f, PathGrace = 1f;
    // An exit's cutscene that never leads out keeps its keeper's cutscenes their own no longer than this.
    private const float LeavingTimeout = 60f;
    // How long after arriving in a scene a player joins a cutscene running there from anywhere in it.
    private const float ArrivalWindow = 5f;
    private static readonly AccessTools.FieldRef<WGOInteractionHandlerBase, Wgo> Assigned =
        AccessTools.FieldRefAccess<WGOInteractionHandlerBase, Wgo>("assignedWgo");
    // Players whose cutscene runs, which this player joins on coming close, and those whose cutscene is an exit's.
    private static readonly bool[] Running = new bool[CoopSession.MaxPlayers + 1];
    private static readonly bool[] Leaving = new bool[CoopSession.MaxPlayers + 1];
    private static bool walking, walkMoved, walkTried;
    private static float walkedAt, nextFollow;
    // The scene this game's keeper took an exit from, until it has left it or the exit's cutscene has ended.
    private static string leavingScene;
    private static float leavingSince;
    // The scene this game's keeper was last seen in, and when it arrived there once the teleport let go.
    private static string sceneSeen;
    private static bool arriving;
    private static float arrivedAt = float.NegativeInfinity;
    private static float nextCamera;
    private static float nextCinematic;
    private static float nextScreen;
    private static bool ownCinematic;
    private static int watched;
    private static Transform view;
    private static Vector3 point;
    private static bool pointKnown;
    // The player whose fade darkened this screen.
    private static int darkenedBy;

    private static CameraController Controller => CameraSystem.Instance.GetCameraController(CameraType.Main);

    // This game shows a cutscene of its own, not another player's mirrored.
    internal static bool InOwnCutscene => ownCinematic;

    // This player takes part in the cutscene of the player given.
    internal static bool Watching(int slot) => slot != 0 && watched == slot;

    // This player takes part in what the player given plays out: their cutscene, or a conversation near them. A player
    // roaming far away sees it only, as does one staying behind while that player takes an exit.
    internal static bool Joins(int slot) => Watching(slot) || SharedPresentation.Watches(slot) && !Leaving[slot] && (Chained || Near(slot));

    // The local keeper is still in its chains.
    private static bool Chained => RemoteKeeper.LocalShackles() > 0;

    private static bool Near(int slot) => Apart(slot) is float apart && apart <= JoinDistance;

    private static bool JustArrived => Time.unscaledTime - arrivedAt <= ArrivalWindow;

    // This game's cutscene began as its keeper took an exit, which it has not left by yet.
    private static bool LeavingNow => leavingScene != null && MainGame.PlayerData != null &&
        MainGame.PlayerData.currentGameSceneId == leavingScene && Time.unscaledTime - leavingSince <= LeavingTimeout;

    // How far apart this player's keeper and another's stand on the ground, where both are known.
    private static float? Apart(int slot)
    {
        var other = RemoteKeeper.PositionOf(slot);
        var player = MainGame.PlayerController;
        if (other == null || player == null)
            return null;
        var own = player.PhysicalBody.transform.position;
        return Vector2.Distance(new Vector2(own.x, own.z), new Vector2(other.Value.x, other.Value.z));
    }

    // Nothing of the world shows: the camera may cut where it would otherwise fly.
    private static bool ScreenBlack
    {
        get
        {
            var fade = LazyUI.Get<UIFade>();
            return fade != null && fade.IsFadeShowing && Blackout(fade).alpha >= 0.99f;
        }
    }

    // A watch begins and ends with a camera fly, or a cut while the screen is black.
    private static float Fly => ScreenBlack ? 0f : FlyTime;

    // This game's own story keeps its screen dark, as a new campaign's does until its opening reveals the
    // prison; another player's darkness shown here is theirs.
    internal static bool DarkenedByStory
    {
        get
        {
            var fade = LazyUI.Get<UIFade>();
            return darkenedBy == 0 && fade != null && !FadeFlags(fade).GetFlag(FadeFlag.FlowScript);
        }
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UICinematic), nameof(UICinematic.EnableCinematic))]
    private static void Began(bool instant) => OwnCinematic(true, instant);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UICinematic), nameof(UICinematic.DisableCinematic))]
    private static void Ended(bool instant) => OwnCinematic(false, instant);

    private static void OwnCinematic(bool on, bool instant)
    {
        if (SharedPresentation.Applying)
            return;
        ownCinematic = on;
        nextCinematic = Time.unscaledTime + CinematicInterval;
        ShareCinematic(on, instant);
        // The exit's cutscene is over; what this game shows next is its own again.
        if (!on)
            leavingScene = null;
    }

    private static void ShareCinematic(bool on, bool instant)
    {
        bool leaving = on && LeavingNow;
        SharedPresentation.Send(SharedPresentation.Cue.Cinematic, writer =>
        {
            writer.Write(on);
            writer.Write(instant);
            writer.Write(leaving);
        });
    }

    // This game's keeper takes an exit: a door, a ladder or a passage that moves its keeper elsewhere.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(WGOInteractionHandlerBase), nameof(WGOInteractionHandlerBase.Interact))]
    private static void Exiting(WGOInteractionHandlerBase __instance, PlayerController interactor) => TakeExit(__instance, interactor);

    [HarmonyPrefix]
    [HarmonyPatch(typeof(WGOInteractionHandlerBase), nameof(WGOInteractionHandlerBase.Interact2))]
    private static void ExitingOther(WGOInteractionHandlerBase __instance, PlayerController interactor) => TakeExit(__instance, interactor);

    private static void TakeExit(WGOInteractionHandlerBase handler, PlayerController interactor)
    {
        if (SharedPresentation.Applying || interactor == null || interactor != MainGame.PlayerController || MainGame.PlayerData == null)
            return;
        var exits = Assigned(handler)?.Data?.Definition?.teleportDestinationWgoIds;
        if (exits == null || exits.Count == 0)
            return;
        leavingScene = MainGame.PlayerData.currentGameSceneId;
        leavingSince = Time.unscaledTime;
    }

    // The point this game's camera follows during its own cutscene, shared with the frame's changes.
    internal static void Share()
    {
        if (leavingScene != null && !LeavingNow)
            leavingScene = null;
        if (Time.unscaledTime >= nextScreen)
        {
            nextScreen = Time.unscaledTime + ScreenInterval;
            bool dark = DarkenedByStory;
            SharedPresentation.Send(SharedPresentation.Cue.Screen, writer => writer.Write(dark));
        }
        if (!ownCinematic)
            return;
        if (Time.unscaledTime >= nextCinematic)
        {
            nextCinematic = Time.unscaledTime + CinematicInterval;
            ShareCinematic(true, instant: true);
        }
        if (Time.unscaledTime < nextCamera)
            return;
        var follow = Controller.VirtualCamera.Follow;
        if (follow == null)
            return;
        nextCamera = Time.unscaledTime + CameraInterval;
        var position = follow.position;
        SharedPresentation.Send(SharedPresentation.Cue.Camera, writer =>
        {
            writer.Write(position.x);
            writer.Write(position.y);
            writer.Write(position.z);
        });
    }

    // A cutscene's own fades, not those of loading or sleeping.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIBasicFade), nameof(UIBasicFade.FadeIn), typeof(float), typeof(Action), typeof(FadeFlag), typeof(bool))]
    private static void Darkened(UIBasicFade __instance, float fadeTime, FadeFlag fadeFlag) => ShareFade(__instance, fadeFlag, Fade.In, fadeTime);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIBasicFade), nameof(UIBasicFade.FadeOut), typeof(float), typeof(Action), typeof(FadeFlag))]
    private static void Brightened(UIBasicFade __instance, float fadeTime, FadeFlag fadeFlag) => ShareFade(__instance, fadeFlag, Fade.Out, fadeTime);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIBasicFade), nameof(UIBasicFade.FadeInInstant))]
    private static void DarkenedAtOnce(UIBasicFade __instance, FadeFlag fadeFlag) => ShareFade(__instance, fadeFlag, Fade.InInstant, 0f);

    [HarmonyPostfix]
    [HarmonyPatch(typeof(UIBasicFade), nameof(UIBasicFade.FadeOutInstant))]
    private static void BrightenedAtOnce(UIBasicFade __instance, FadeFlag fadeFlag) => ShareFade(__instance, fadeFlag, Fade.OutInstant, 0f);

    private static void ShareFade(UIBasicFade fade, FadeFlag flag, Fade kind, float time)
    {
        if (flag != FadeFlag.FlowScript || fade != LazyUI.Get<UIFade>())
            return;
        SharedPresentation.Send(SharedPresentation.Cue.Fade, writer =>
        {
            writer.Write((byte)kind);
            writer.Write(time);
        });
    }

    internal static void Apply(int slot, SharedPresentation.Cue cue, BinaryReader reader)
    {
        switch (cue)
        {
            case SharedPresentation.Cue.Cinematic:
                bool on = reader.ReadBoolean(), instant = reader.ReadBoolean(), leaving = reader.ReadBoolean();
                Running[slot] = on;
                Leaving[slot] = on && leaving;
                if (watched == slot && (!on || leaving))
                    End(instant);
                else if (on && !leaving && watched == 0 && CanJoin(slot))
                    Join(slot, instant);
                break;
            case SharedPresentation.Cue.Camera:
                var position = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                if (watched != slot)
                    return;
                point = position;
                pointKnown = true;
                break;
            case SharedPresentation.Cue.Fade:
                var kind = (Fade)reader.ReadByte();
                float time = reader.ReadSingle();
                // The screen that player darkened brightens with theirs, wherever this player has gone since.
                bool brightens = kind == Fade.Out || kind == Fade.OutInstant;
                if (watched == slot || brightens && darkenedBy == slot)
                    ShowFade(slot, kind, time);
                break;
            case SharedPresentation.Cue.Screen:
                bool dark = reader.ReadBoolean();
                if (!dark && darkenedBy == slot)
                    ShowFade(slot, Fade.Out, BrightenTime);
                else if (dark && darkenedBy == 0 && watched == slot)
                    ShowFade(slot, Fade.InInstant, 0f);
                break;
        }
    }

    // A player joining a game whose story keeps the host's screen dark starts in the same dark, and sees
    // the scene when the host's story reveals it, not what the dark hides from the host.
    internal static void Adopt(int slot, bool dark)
    {
        if (dark)
            SharedPresentation.Mirror(() => ShowFade(slot, Fade.InInstant, 0f));
    }

    // Each frame: a player not yet taking part joins a cutscene running near them, or anywhere in the scene they have just
    // arrived in; one taking part keeps by its keeper while both stay in that scene.
    internal static void Update()
    {
        NoteArrival();
        if (watched != 0)
        {
            if (!SharedPresentation.Watches(watched))
                SharedPresentation.Mirror(() => End(instant: false));
            else
                Follow();
            return;
        }
        for (int slot = 1; slot < Running.Length; slot++)
        {
            if (Running[slot] && !Leaving[slot] && CanJoin(slot))
            {
                SharedPresentation.Mirror(() => Join(slot, instant: false));
                return;
            }
        }
    }

    // A new scene for this game's keeper, which it arrives in once its teleport lets go of it.
    private static void NoteArrival()
    {
        var player = MainGame.PlayerController;
        string scene = MainGame.PlayerData?.currentGameSceneId;
        if (scene != sceneSeen)
        {
            sceneSeen = scene;
            arriving = true;
        }
        if (arriving && player != null && player.IsControlEnabledByType(TakenControlType.ByTeleport))
        {
            arriving = false;
            arrivedAt = Time.unscaledTime;
        }
    }

    // In its scene, near its keeper, chained or just arrived, awake and not in a cutscene of this game's own.
    private static bool CanJoin(int slot) => SharedPresentation.Watches(slot) && !LazyUI.Get<UICinematic>().gameObject.activeSelf &&
        MainGame.PlayerController != null && !MainGame.PlayerData.energySystem.IsSleeping && (Chained || Near(slot) || JustArrived);

    private static void Join(int slot, bool instant)
    {
        Begin(slot, instant);
        walking = walkTried = false;
        nextFollow = 0f;
        Follow();
    }

    // The keeper walks over beside the other at the story's pace, or hurries after it, and faces as it faces once there.
    private static void Follow()
    {
        if (walking)
        {
            float walked = Time.unscaledTime - walkedAt;
            var movement = MainGame.PlayerController.MovementComponent;
            walkMoved |= movement.IsMoving;
            // A stopped path calls back no more, and one the game could not find never moves or calls back: the game
            // holds the keeper for a path until it ends, so ending it hands the keeper back to its player's keys.
            if (walked > WalkTimeout || walkMoved && walked > WalkGrace && !movement.IsMoving || !walkMoved && walked > PathGrace)
            {
                movement.ForceStop();
                Arrived();
            }
            return;
        }
        if (Time.unscaledTime < nextFollow || Chained)
            return;
        nextFollow = Time.unscaledTime + FollowInterval;
        if (Apart(watched) is float apart && apart > FollowFrom)
            Walk(apart > HurryFrom ? HurrySpeed : WalkSpeed);
    }

    // Half a tile beside the other keeper, on the side it does not face: right of it unless it faces right.
    private static void Walk(float speed)
    {
        var other = RemoteKeeper.PositionOf(watched);
        var facing = RemoteKeeper.FacingOf(watched) ?? Vector2.down;
        if (other == null || !SharedPresentation.Watches(watched))
            return;
        var target = other.Value + Vector3.right * (facing.ConvertFromVector2() == Direction.Right ? -Beside : Beside);
        var player = MainGame.PlayerController;
        string scene = MainGame.PlayerData.currentGameSceneId;
        walking = walkTried = true;
        walkMoved = false;
        walkedAt = Time.unscaledTime;
        if (player.MovementComponent.StartPath(target, scene, scene, MovementType.Recast, speed, string.Empty, Arrived,
                player.PlayerLocalAreaMovement.Seeker) != MovementComponent.StartPathResult.Started)
            Arrived();
    }

    private static void Arrived()
    {
        if (!walking)
            return;
        walking = false;
        var facing = watched != 0 ? RemoteKeeper.FacingOf(watched) : null;
        if (facing != null)
            MainGame.PlayerController.PhysicalBody.SetFacingDirection(facing.Value);
    }

    private static void Begin(int slot, bool instant)
    {
        watched = slot;
        pointKnown = false;
        LazyUI.Get<UICinematic>().EnableCinematic(null, instant);
        MainGame.PlayerController.SetControlTakenType(TakenControlType.ByCinematics, isEnabled: false);
        var controller = Controller;
        var follow = controller.VirtualCamera.Follow;
        view = new GameObject("Watched Cutscene View").transform;
        view.SetParent(CameraSystem.Instance.transform, false);
        view.position = follow != null ? follow.position : controller.transform.position;
        view.gameObject.AddComponent<View>();
        controller.SetTarget(view, Fly);
    }

    private static void End(bool instant)
    {
        // Whatever became of the walks, the keeper goes back to its player's keys.
        if (walkTried && MainGame.PlayerController != null)
            MainGame.PlayerController.MovementComponent.ForceStop();
        walking = walkTried = false;
        watched = 0;
        pointKnown = false;
        if (view != null)
            UnityEngine.Object.Destroy(view.gameObject);
        view = null;
        if (MainGame.Instance.gameState != MainGame.GameState.InGame)
            return;
        LazyUI.Get<UICinematic>().DisableCinematic(null, instant);
        // The start barrier holds the keeper the same way until everyone has loaded.
        if (!EntryBarrier.Waiting)
            MainGame.PlayerController.SetControlTakenType(TakenControlType.ByCinematics, isEnabled: true);
        Controller.SetTarget(MainGame.PlayerController.View.transform, Fly);
    }

    private static void ShowFade(int slot, Fade kind, float time)
    {
        var fade = LazyUI.Get<UIFade>();
        switch (kind)
        {
            case Fade.In:
                fade.FadeIn(time, null, FadeFlag.FlowScript);
                darkenedBy = slot;
                break;
            case Fade.Out:
                fade.FadeOut(time, null, FadeFlag.FlowScript);
                darkenedBy = 0;
                break;
            case Fade.InInstant:
                fade.FadeInInstant(FadeFlag.FlowScript);
                darkenedBy = slot;
                break;
            case Fade.OutInstant:
                fade.FadeOutInstant(FadeFlag.FlowScript);
                darkenedBy = 0;
                break;
        }
    }

    // This game's own cutscene ends with its session.
    internal static void Reset()
    {
        ownCinematic = false;
        nextScreen = 0f;
        leavingScene = null;
        sceneSeen = null;
        arriving = false;
        arrivedAt = float.NegativeInfinity;
        Array.Clear(Running, 0, Running.Length);
        Array.Clear(Leaving, 0, Leaving.Length);
    }

    internal static void Forget(int slot)
    {
        Running[slot] = false;
        Leaving[slot] = false;
        if (watched == slot)
            End(instant: false);
        if (darkenedBy != slot)
            return;
        darkenedBy = 0;
        if (MainGame.Instance.gameState == MainGame.GameState.InGame)
            LazyUI.Get<UIFade>().FadeOut(BrightenTime, null, FadeFlag.FlowScript);
    }

    // The watched view follows the other player's camera between its shared points, and the keeper
    // stays held while the cutscene lasts, also when the start barrier lets go of it. A view whose
    // watch has ended lives on until the frame's end and holds nothing.
    private sealed class View : MonoBehaviour
    {
        private void LateUpdate()
        {
            if (transform != view)
                return;
            var player = MainGame.PlayerController;
            if (player != null && player.IsControlEnabledByType(TakenControlType.ByCinematics))
                player.SetControlTakenType(TakenControlType.ByCinematics, isEnabled: false);
            if (!pointKnown)
                return;
            // Unseen, the view keeps up with the other player's camera at once, as theirs does in its own dark.
            if (ScreenBlack)
            {
                if ((transform.position - point).sqrMagnitude > 0.0001f)
                {
                    transform.position = point;
                    Controller.UpdateTargetPosInstant();
                }
                return;
            }
            transform.position = Vector3.Lerp(transform.position, point, 1f - Mathf.Exp(-Smoothing * Time.unscaledDeltaTime));
        }
    }
}
