using System;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// Players in the same scene watch another player's cutscene: its letterbox, the view of its camera
// and its fades, with their own keeper held still until the cutscene ends.
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
    }

    private static void ShareCinematic(bool on, bool instant) => SharedPresentation.Send(SharedPresentation.Cue.Cinematic, writer =>
    {
        writer.Write(on);
        writer.Write(instant);
    });

    // The point this game's camera follows during its own cutscene, shared with the frame's changes.
    internal static void Share()
    {
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
                bool on = reader.ReadBoolean(), instant = reader.ReadBoolean();
                if (on && watched == 0 && SharedPresentation.Watches(slot) && !LazyUI.Get<UICinematic>().gameObject.activeSelf)
                    Begin(slot, instant);
                else if (!on && watched == slot)
                    End(instant);
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
                if (SharedPresentation.Watches(slot) || brightens && darkenedBy == slot)
                    ShowFade(slot, kind, time);
                break;
            case SharedPresentation.Cue.Screen:
                bool dark = reader.ReadBoolean();
                if (!dark && darkenedBy == slot)
                    ShowFade(slot, Fade.Out, BrightenTime);
                else if (dark && darkenedBy == 0 && SharedPresentation.Watches(slot))
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
    }

    internal static void Forget(int slot)
    {
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
