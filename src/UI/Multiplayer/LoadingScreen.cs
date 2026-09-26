using System;
using GYK2.TombManyKeepers.Multiplayer.Session;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using TMPro;
using UnityEngine;

namespace GYK2.TombManyKeepers.UI.Multiplayer;

// Everyone entering a multiplayer game shares the native loading screen from the moment the host
// starts or a later player asks to join. The native line under the bar says what is happening, and
// the bar runs through the host's loading, the download of its game, this game's own loading and
// the wait for everyone else.
[HarmonyPatch(typeof(UILoadingOverlay))]
internal static class LoadingScreen
{
    // A joined player's bar: the host's loading, then the download, then this game's loading.
    private const float HostShare = 0.4f;
    private const float DownloadEnd = 0.5f;
    // A later player's bar has no host loading ahead of the download.
    private const float LaterDownloadEnd = 0.2f;
    private const float DotInterval = 0.4f;
    private static readonly Action<UILoadingOverlay, UILoadingOverlay.SaveLoadProgressPhase> SetPhase =
        AccessTools.MethodDelegate<Action<UILoadingOverlay, UILoadingOverlay.SaveLoadProgressPhase>>(
            AccessTools.Method(typeof(UILoadingOverlay), "SetPhase"));
    private static UIMainMenuWindow menu;
    private static TextMeshProUGUI label;
    // Opened before this game had anything to load; a failure meanwhile closes it again.
    private static bool beforeLoading;
    private static int entry;
    // This game's own loading has begun, so the native measure describes it rather than an earlier load.
    private static bool measuring;
    private static float shown;

    // A joined player's loading screen opens as its entry begins, before the host's game arrives.
    internal static void Open(UIMainMenuWindow mainMenu)
    {
        menu = mainMenu;
        beforeLoading = true;
        CoopSession.Failed -= Abort;
        CoopSession.Failed += Abort;
        var overlay = LazyUI.Get<UILoadingOverlay>();
        if (!overlay.IsShown)
            overlay.Draw(new LoadingWindowData(MainGame.EntrySceneToLoad, null));
    }

    private static void Abort(string reason)
    {
        CoopSession.Failed -= Abort;
        if (!beforeLoading)
            return;
        beforeLoading = false;
        var overlay = LazyUI.Get<UILoadingOverlay>();
        if (overlay.IsShown)
            overlay.Hide();
        MultiplayerMenu.ShowError(menu, reason);
    }

    [HarmonyPostfix]
    [HarmonyPatch("EvaluateProgress")]
    private static void MapProgress(ref float __result)
    {
        Follow();
        float own = measuring ? __result : 0f;
        // The host shares its own progress with the players waiting for its game.
        EntryStatus.OwnProgress = own;
        var phase = EntryStatus.Current;
        if (phase == EntryStatus.Phase.None)
            return;
        float bar;
        if (EntryStatus.IsHost)
        {
            bar = phase == EntryStatus.Phase.Waiting ? 1f : own;
        }
        else
        {
            float downloadStart = EntryStatus.Later ? 0f : HostShare;
            float loadStart = EntryStatus.Later ? LaterDownloadEnd : DownloadEnd;
            bar = phase switch
            {
                EntryStatus.Phase.HostLoading => HostShare * EntryStatus.HostProgress,
                EntryStatus.Phase.Requesting => 0f,
                EntryStatus.Phase.Downloading => Mathf.Lerp(downloadStart, loadStart, EntryStatus.Received),
                EntryStatus.Phase.Loading => Mathf.Lerp(loadStart, 1f, own),
                _ => 1f
            };
        }
        // Registering a load's work can lower the native measure; the bar never falls back.
        shown = Mathf.Max(shown, bar);
        __result = shown;
    }

    // This game's own loading begins: a new game draws the screen afresh, while a continued game,
    // the host's saved campaign or a joined player's copy of the host's game, reuses the screen
    // that is up and restarts its native measure.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.CreateGameSaveAndStart))]
    private static void MeasureNewGame()
    {
        Follow();
        measuring = true;
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(MainGame), nameof(MainGame.ContinueGame))]
    private static void MeasureContinuedGame()
    {
        Follow();
        measuring = true;
        var overlay = LazyUI.Get<UILoadingOverlay>();
        if (EntryStatus.Current != EntryStatus.Phase.None && overlay.IsShown)
            SetPhase(overlay, UILoadingOverlay.SaveLoadProgressPhase.FadeIn);
    }

    // Each entry's bar starts from the beginning.
    private static void Follow()
    {
        if (entry == EntryStatus.Entry)
            return;
        entry = EntryStatus.Entry;
        measuring = false;
        shown = 0f;
    }

    [HarmonyPostfix]
    [HarmonyPatch("Update")]
    private static void ShowStatus(UILoadingOverlay __instance)
    {
        var phase = EntryStatus.Current;
        if (phase == EntryStatus.Phase.None || !__instance.IsShown)
            return;
        if (phase is EntryStatus.Phase.Loading or EntryStatus.Phase.Waiting)
            beforeLoading = false;
        var line = Line(__instance);
        if (line == null)
            return;
        string status = Describe(phase);
        if (line.text != status)
        {
            line.richText = true;
            line.text = status;
        }
    }

    // Native loading screens show the game's own line again.
    [HarmonyPostfix]
    [HarmonyPatch(nameof(UILoadingOverlay.Draw), typeof(LoadingWindowData))]
    private static void RestoreNativeLine(UILoadingOverlay __instance)
    {
        if (EntryStatus.Current == EntryStatus.Phase.None)
            Line(__instance)?.GetComponent<LocalizedLabel>()?.Localize();
    }

    private static string Describe(EntryStatus.Phase phase)
    {
        string host = "<noparse>" + EntryStatus.HostName + "</noparse>";
        return phase switch
        {
            EntryStatus.Phase.HostLoading => Dots($"Waiting for {host} to load the game"),
            EntryStatus.Phase.Requesting => Dots($"Requesting the save from {host}"),
            // Opening the received save is the first part of loading it.
            EntryStatus.Phase.Downloading => EntryStatus.Received < 1f
                ? $"Downloading the save: {Percent(EntryStatus.Received)}%"
                : Dots("Loading the game"),
            EntryStatus.Phase.Loading => Dots("Loading the game"),
            _ => EntryStatus.IsHost && EntryStatus.Sent >= 0f
                ? $"Sending the save: {Percent(EntryStatus.Sent)}%"
                : Dots($"Waiting for everyone to load ({EntryStatus.Loaded}/{EntryStatus.Total})")
        };
    }

    // The dots count on while a phase lasts; the hidden ones keep the line from shifting.
    private static string Dots(string text)
    {
        int shown = (int)(Time.unscaledTime / DotInterval) % 4;
        return text + new string('.', shown) + "<alpha=#00>" + new string('.', 3 - shown);
    }

    private static int Percent(float share) => Mathf.FloorToInt(Mathf.Clamp01(share) * 100f);

    // The native "Loading" line under the bar.
    private static TextMeshProUGUI Line(UILoadingOverlay overlay)
    {
        if (label == null)
            label = overlay.transform.Find("LoadingLabel")?.GetComponent<TextMeshProUGUI>();
        return label;
    }
}
