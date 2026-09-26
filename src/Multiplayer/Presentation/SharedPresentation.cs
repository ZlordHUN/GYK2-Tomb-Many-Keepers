using System;
using System.IO;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;

namespace GYK2.TombManyKeepers.Multiplayer.Presentation;

// What one player's game shows of its cutscenes and conversations, shown to the other players in the
// same scene. Only presentation travels: the story runs once, in the game of the player who acts.
internal static class SharedPresentation
{
    internal enum Cue : byte
    {
        Cinematic,
        Camera,
        Fade,
        Talk,
        TalkEnd,
        Answers,
        AnswerHover,
        AnswerChosen,
        AnswersClosed,
        Wisp,
        WispType,
        Sound,
        Effect,
        Lighting,
        LightOverride,
        Screen
    }

    private static readonly MemoryStream Packet = new MemoryStream();
    private static readonly BinaryWriter Writer = new BinaryWriter(Packet);

    // Showing another player's presentation here, which is never shared back.
    internal static bool Applying { get; private set; }

    // Continuous cues may be lost; a later one replaces them.
    internal static bool IsStream(byte cue) => cue == (byte)Cue.Camera || cue == (byte)Cue.Wisp || cue == (byte)Cue.Screen;

    internal static void Send(Cue cue, Action<BinaryWriter> write)
    {
        if (Applying || !CoopSession.SharesWorld)
            return;
        Packet.SetLength(0);
        Writer.Write((byte)cue);
        write(Writer);
        CoopSession.ShareCue(Packet.GetBuffer(), (int)Packet.Length, !IsStream((byte)cue));
    }

    // A character in the world, such as the one a line or answer is for. Scene content has its own
    // unique ids in each game, so a character this game knows by another id is found by what it is,
    // when that is unambiguous.
    internal static void WriteCharacter(BinaryWriter writer, WgoData character)
    {
        writer.Write(character != null);
        if (character == null)
            return;
        WorldSync.WriteId(writer, character.UniqueId.Guid);
        writer.Write(character.id ?? string.Empty);
    }

    internal static WgoData ReadCharacter(BinaryReader reader)
    {
        if (!reader.ReadBoolean())
            return null;
        var id = WorldSync.ReadId(reader);
        string kind = reader.ReadString();
        if (MainGame.WorldData == null || !MainGame.WorldData.HasCache)
            return null;
        var alike = MainGame.WorldData.GetWgoDataList(kind);
        return WorldSync.FindObject(id) ?? (alike.Count == 1 ? alike[0] : null);
    }

    // Another player watching this game's presentation: they are in the same scene.
    internal static bool Watches(int slot)
    {
        var session = CoopSession.Current;
        string scene = RemoteKeeper.SceneOf(slot);
        return session != null && slot != session.LocalSlot && scene != null && scene == MainGame.PlayerData.currentGameSceneId;
    }

    internal static void Apply(int slot, BinaryReader reader) => Mirror(() =>
    {
        var cue = (Cue)reader.ReadByte();
        switch (cue)
        {
            case Cue.Cinematic:
            case Cue.Camera:
            case Cue.Fade:
            case Cue.Screen:
                WatchedCutscene.Apply(slot, cue, reader);
                break;
            case Cue.Talk:
                SharedSpeech.Show(slot, reader);
                break;
            case Cue.TalkEnd:
                SharedSpeech.End(slot, reader);
                break;
            case Cue.Answers:
            case Cue.AnswerHover:
            case Cue.AnswerChosen:
            case Cue.AnswersClosed:
                SharedAnswers.Apply(slot, cue, reader);
                break;
            case Cue.Wisp:
            case Cue.WispType:
                RemoteWisps.Apply(slot, cue, reader);
                break;
            case Cue.Sound:
            case Cue.Effect:
                CutsceneSounds.Apply(slot, cue, reader);
                break;
            case Cue.Lighting:
            case Cue.LightOverride:
                SceneLighting.Apply(slot, cue, reader);
                break;
        }
    });

    // Native calls made to show another player's presentation, never shared back.
    internal static void Mirror(Action show)
    {
        bool was = Applying;
        Applying = true;
        try
        {
            show();
        }
        finally
        {
            Applying = was;
        }
    }

    // A player who leaves takes what they were showing with them.
    internal static void Forget(int slot) => Mirror(() =>
    {
        WatchedCutscene.Forget(slot);
        SharedSpeech.Forget(slot);
        SharedAnswers.Forget(slot);
        RemoteWisps.Forget(slot);
        SceneLighting.Forget(slot);
    });

    internal static void Clear()
    {
        for (int slot = 1; slot <= CoopSession.MaxPlayers; slot++)
            Forget(slot);
        RemoteWisps.Reset();
        WatchedCutscene.Reset();
    }
}
