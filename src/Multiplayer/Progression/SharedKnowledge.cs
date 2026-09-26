using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using GYK2.TombManyKeepers.Multiplayer.World;
using HarmonyLib;
using LazyBearTechnology;

namespace GYK2.TombManyKeepers.Multiplayer.Progression;

// Campaign knowledge is shared: technologies, crafts, buildings, phrases and every other unlock a
// player's game makes reaches everyone, replayed through the same native call. Which tutorials a
// player has read stays theirs. Reputation with townsfolk is kept per character natively but
// belongs to the campaign: every change reaches everyone, and a joining character takes on the
// campaign's standing.
[HarmonyPatch]
internal static class SharedKnowledge
{
    // Recomputations every game makes for itself, and each player's own reading.
    private static readonly string[] Local =
    {
        nameof(KnowledgeSystem.PrepareForGame), nameof(KnowledgeSystem.UnPrepareForGame),
        nameof(KnowledgeSystem.RevealTechsFromCompletedQuests), nameof(KnowledgeSystem.CatchUpRevealedTechsFromTree),
        nameof(KnowledgeSystem.FillRevealedTechsFromTreeState), nameof(KnowledgeSystem.RevealHiddenTechsIfParentVisibleInTree),
        nameof(KnowledgeSystem.AddViewedTutorial), nameof(KnowledgeSystem.AddDelayedDemoTechUnlock),
        nameof(KnowledgeSystem.ApplyDelayedDemoTechUnlocks)
    };

    private const string ReputationSuffix = "_REP";
    private static readonly AccessTools.FieldRef<PlayerData, GameRes> Resources =
        AccessTools.FieldRefAccess<PlayerData, GameRes>("res");

    private static IEnumerable<MethodInfo> Changes() => AccessTools.GetDeclaredMethods(typeof(KnowledgeSystem))
        .Where(method => method.IsPublic && !method.IsStatic && method.ReturnType == typeof(void) &&
            method.GetParameters().Length > 0 && !Local.Contains(method.Name));

    internal static void Apply(BinaryReader reader)
    {
        string name = reader.ReadString();
        int count = reader.ReadByte();
        var method = Changes().FirstOrDefault(candidate => candidate.Name == name && candidate.GetParameters().Length == count);
        var args = new object[count];
        for (int i = 0; i < count; i++)
            args[i] = Read(reader);
        var knowledge = MainGame.Instance.GameSave.knowledgeSystem;
        switch (name)
        {
            // Definitions travel by id; their unlocks arrive as their own changes.
            case nameof(KnowledgeSystem.CompleteOneTimeCraft):
                if (!knowledge.oneTimeCompletedCrafts.Contains((string)args[0]))
                    knowledge.oneTimeCompletedCrafts.Add((string)args[0]);
                return;
            case nameof(KnowledgeSystem.DiscoverAlchemyMix):
                if (!knowledge.knownMixCrafts.Contains((string)args[0]))
                    knowledge.knownMixCrafts.Add((string)args[0]);
                return;
        }
        if (method == null)
            return;
        var parameters = method.GetParameters();
        for (int i = 0; i < count; i++)
        {
            if (parameters[i].ParameterType.IsEnum)
                args[i] = Enum.ToObject(parameters[i].ParameterType, args[i]);
        }
        method.Invoke(knowledge, args);
    }

    internal static void ApplyReputation(BinaryReader reader)
    {
        string reputation = reader.ReadString();
        bool set = reader.ReadBoolean();
        int value = reader.ReadInt32();
        if (set)
            MainGame.PlayerData.SetNPCRep(reputation, value);
        else
            MainGame.PlayerData.AddNPCRep(reputation, value);
    }

    // Host: a joining character takes on the campaign's standing with every townsperson.
    internal static void CopyStanding(PlayerData character)
    {
        var standing = Resources(character);
        foreach (var atom in Resources(MainGame.PlayerData).List)
        {
            if (atom.type.EndsWith(ReputationSuffix, StringComparison.Ordinal))
                standing.SetWithoutSystemsCheck(atom.type, atom.value);
        }
    }

    private static void Write(BinaryWriter writer, object value)
    {
        switch (value)
        {
            case string text:
                writer.Write((byte)0);
                writer.Write(text);
                break;
            case bool flag:
                writer.Write((byte)1);
                writer.Write(flag);
                break;
            case BalanceBaseObject definition:
                writer.Write((byte)0);
                writer.Write(definition.id);
                break;
            default:
                writer.Write((byte)2);
                writer.Write(Convert.ToInt32(value));
                break;
        }
    }

    private static object Read(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => reader.ReadString(),
        1 => reader.ReadBoolean(),
        _ => reader.ReadInt32()
    };

    [HarmonyPatch]
    private static class Unlocks
    {
        private static IEnumerable<MethodBase> TargetMethods() => Changes();

        private static void Postfix(KnowledgeSystem __instance, MethodBase __originalMethod, object[] __args)
        {
            // Every craft finished reports itself; only a one-time craft is remembered.
            if (!WorldSync.Sharing || __instance != MainGame.Instance.GameSave.knowledgeSystem ||
                __originalMethod.Name == nameof(KnowledgeSystem.CompleteOneTimeCraft) &&
                !(__args[0] is CraftDefBase craft && craft.IsOneTimeCraft()))
                return;
            string name = __originalMethod.Name;
            var args = (object[])__args.Clone();
            WorldSync.Queue(WorldSync.Change.Knowledge, Guid.Empty, writer =>
            {
                writer.Write(name);
                writer.Write((byte)args.Length);
                foreach (var value in args)
                    Write(writer, value);
            });
        }
    }

    [HarmonyPatch]
    private static class Reputation
    {
        private static IEnumerable<MethodBase> TargetMethods() => new[]
        {
            AccessTools.Method(typeof(PlayerData), nameof(PlayerData.SetNPCRep)),
            AccessTools.Method(typeof(PlayerData), nameof(PlayerData.AddNPCRep))
        };

        // Changes travel as made, so changes made together all count.
        private static void Postfix(PlayerData __instance, MethodBase __originalMethod, string repRes, int value)
        {
            if (!WorldSync.Sharing || __instance != MainGame.PlayerData)
                return;
            bool set = __originalMethod.Name == nameof(PlayerData.SetNPCRep);
            WorldSync.Queue(WorldSync.Change.Reputation, Guid.Empty, writer =>
            {
                writer.Write(repRes);
                writer.Write(set);
                writer.Write(value);
            });
        }
    }
}
