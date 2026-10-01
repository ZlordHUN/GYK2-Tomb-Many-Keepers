using System.Collections.Generic;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.Chat;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// Every player reads the tutorial pages any player's game shows, as GYK1 showed its tutorials to everyone: the story's,
// such as the energy page once Larry is dug up, and those a first try at something shows. Rereading a page from the
// tutorial list is for its reader alone. Another player's page waits until this player is free, with no window open,
// nothing holding their keeper and no line being typed, and behind the pages before it; a page this player has read
// is not shown again. As in GYK1, the first player to close a page moves on the story waiting for it, in the game of
// the player whose page it is, where the page stays open for its reader; a page that game shows while another is open
// waits behind it. A window a player's own action opens after the page, as the fishing window does, still opens as
// that player closes theirs.
[HarmonyPatch]
internal static class SharedTutorials
{
    private const int LongestPage = 128, Remembered = 64;

    // A page as the players share it: whose game showed it, its number there, its id, and in that game, the page with
    // the story waiting for it.
    private sealed class Page
    {
        internal int Owner;
        internal int Number;
        internal string Id;
        internal UITutorialWindowData Data;

        internal (int, int) Key => (Owner, Number);
    }

    // Pages to show here, in the order they came: other players', and this game's own shown while another was open.
    private static readonly List<Page> Waiting = new List<Page>();
    // Pages someone has closed, whose story has moved on, the newest last.
    private static readonly List<(int, int)> FirstClosed = new List<(int, int)>();
    // The shared page on this game's tutorial window, and a waiting one being shown.
    private static Page shown, opening;
    private static int numbered;
    private static bool rereading;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UITutorialListWindow), "OpenTutorial")]
    private static void Rereading() => rereading = true;

    [HarmonyFinalizer]
    [HarmonyPatch(typeof(UITutorialListWindow), "OpenTutorial")]
    private static void Reread() => rereading = false;

    // This game's own page, shown while a shared page is open, waits behind it.
    [HarmonyPrefix]
    [HarmonyPatch(typeof(UITutorialWindow), nameof(UITutorialWindow.Open), typeof(UITutorialWindowData))]
    private static bool Opening(UITutorialWindow __instance, UITutorialWindowData data, out bool __state)
    {
        __state = opening == null && shown != null && __instance.IsShown && Shares(data);
        if (__state)
        {
            var page = Own(data);
            Waiting.Add(page);
            Send(page);
            return false;
        }
        shown = null;
        return true;
    }

    // A page the window does not have closes it at once.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UITutorialWindow), nameof(UITutorialWindow.Open), typeof(UITutorialWindowData))]
    private static void Opened(UITutorialWindow __instance, UITutorialWindowData data, bool __state)
    {
        if (__state || !__instance.IsShown)
            return;
        if (opening != null)
        {
            shown = opening;
            return;
        }
        if (!Shares(data))
            return;
        shown = Own(data);
        Send(shown);
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(UITutorialWindow), nameof(UITutorialWindow.Close))]
    private static void Closing(UITutorialWindow __instance, out object __state)
    {
        __state = __instance.IsShown ? shown : null;
        shown = null;
    }

    // The first to close a page tells everyone; this game's own page has moved its story on as it closed.
    [HarmonyPostfix]
    [HarmonyPatch(typeof(UITutorialWindow), nameof(UITutorialWindow.Close))]
    private static void Closed(object __state)
    {
        if (__state is Page page && First(page.Key))
        {
            SharedPresentation.Send(SharedPresentation.Cue.TutorialClosed, writer =>
            {
                writer.Write((byte)page.Owner);
                writer.Write(page.Number);
            });
        }
    }

    // Another player's page, from its own player's game.
    internal static void Arrive(int slot, BinaryReader reader)
    {
        string id = reader.ReadString();
        int number = reader.ReadInt32();
        if (id.Length > 0 && id.Length <= LongestPage && !Waiting.Exists(page => page.Key.Equals((slot, number))))
            Waiting.Add(new Page { Owner = slot, Number = number, Id = id });
    }

    // Another player closed a page first: this game's own page's story moves on, open or waiting as the page stays.
    internal static void Advance(BinaryReader reader)
    {
        var key = ((int)reader.ReadByte(), reader.ReadInt32());
        if (!First(key) || key.Item1 != CoopSession.Current?.LocalSlot)
            return;
        var page = shown != null && shown.Key.Equals(key) ? shown : Waiting.Find(waiting => waiting.Key.Equals(key));
        var story = page?.Data?.OnCompleteCallback;
        if (story == null)
            return;
        page.Data.OnCompleteCallback = null;
        // What the story shows next is this game's own, and shared.
        SharedPresentation.Unmirrored(story);
    }

    // This game's own waiting pages show as soon as the window is free of pages, as its story showed them; another
    // player's wait for this player to be free too.
    internal static void Update()
    {
        if (Waiting.Count == 0)
            return;
        var window = LazyUI.GetWindow<UITutorialWindow>();
        var page = Waiting[0];
        if (window == null || window.IsShown || page.Data == null && !Free())
            return;
        Waiting.RemoveAt(0);
        if (page.Data == null && MainGame.Instance.GameSave.knowledgeSystem.viewedTutorials?.Contains(page.Id) == true)
            return;
        opening = page;
        try
        {
            if (page.Data != null)
                window.Open(page.Data);
            else
                // Its unlock came with its own player's; reading it here is this player's alone.
                SharedPresentation.Mirror(() => WorldSync.Apply(() => window.Open(new UITutorialWindowData(page.Id))));
        }
        finally
        {
            opening = null;
        }
    }

    // A page this game shows of its own: another player's page or change shows none here, nor does rereading.
    private static bool Shares(UITutorialWindowData data) => CoopSession.SharesWorld && !rereading && !WorldSync.Applying &&
        !SharedPresentation.Applying && !string.IsNullOrEmpty(data?.Page);

    private static Page Own(UITutorialWindowData data) =>
        new Page { Owner = CoopSession.Current.LocalSlot, Number = ++numbered, Id = data.Page, Data = data };

    private static void Send(Page page) => SharedPresentation.Send(SharedPresentation.Cue.Tutorial, writer =>
    {
        writer.Write(page.Id);
        writer.Write(page.Number);
    });

    // Whether this is the first close of the page this game has heard of.
    private static bool First((int, int) key)
    {
        if (FirstClosed.Contains(key))
            return false;
        FirstClosed.Add(key);
        if (FirstClosed.Count > Remembered)
            FirstClosed.RemoveAt(0);
        return true;
    }

    private static bool Free()
    {
        var game = MainGame.Instance;
        return game != null && game.gameState == MainGame.GameState.InGame && game.GameSave != null && MainGame.PlayerController != null &&
            MainGame.PlayerController.IsControlsEnabled && LazyWindowsStackController.ActiveWindow == null &&
            !LazyUI.Get<UILoadingOverlay>().IsShown && !ChatOverlay.Typing();
    }

    internal static void Clear()
    {
        Waiting.Clear();
        FirstClosed.Clear();
        shown = opening = null;
        numbered = 0;
    }
}
