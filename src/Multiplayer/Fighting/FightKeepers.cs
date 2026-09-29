using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using GYK2.TombManyKeepers.Multiplayer.Players;
using GYK2.TombManyKeepers.Multiplayer.World;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using LazyBearTechnology;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Fighting;

// In a battle every keeper shows what the game shows for one's own: the armour, and the health
// and stamina bars above the head. Each player sends their own keeper's; the others draw it.
[HarmonyPatch]
internal static class FightKeepers
{
    private const float Interval = 0.2f;

    private static readonly AccessTools.FieldRef<PlayerController, bool> ArmorView =
        AccessTools.FieldRefAccess<PlayerController, bool>("isArmorViewActive");
    private static readonly AccessTools.FieldRef<PlayerController, bool> ArmorHelmet =
        AccessTools.FieldRefAccess<PlayerController, bool>("armorViewUsesHelmet");
    private static readonly MethodInfo SetStaminaSystem =
        AccessTools.PropertySetter(typeof(StaminaBarPlayerWidgetData), nameof(StaminaBarPlayerWidgetData.StaminaResSystem));
    private static readonly AccessTools.FieldRef<StaminaBarPlayerWidgetData, bool> StaminaSubscribed =
        AccessTools.FieldRefAccess<StaminaBarPlayerWidgetData, bool>("subscribedToDataChanges");

    private static readonly Dictionary<int, Bars> shown = new Dictionary<int, Bars>();
    private static float nextSend;

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), "Update")]
    private static void Update(FightingGameController __instance)
    {
        var session = CoopSession.Current;
        if (session == null || !WorldSync.Sharing || __instance.CurrentFightState == FightState.Disabled ||
            Time.unscaledTime < nextSend)
            return;
        nextSend = Time.unscaledTime + Interval;
        var player = MainGame.PlayerController;
        var health = player.PlayerData.hpComponent;
        var stamina = PlayerStaminaGameResSystem.GetSystem();
        int slot = session.LocalSlot, hp = health.Hp, maxHp = health.MaxHpValue;
        float energy = stamina != null ? stamina.Get() : 0f;
        bool armor = ArmorView(player), helmet = ArmorHelmet(player);
        var layer = !helmet ? AnimationComponent.Layers.ArmorNoHelmet
            : player.Sword.id != "empty" ? AnimationComponent.Layers.ArmorWithSword
            : player.Bow.id != "empty" ? AnimationComponent.Layers.ArmorWithBow
            : AnimationComponent.Layers.Armor;
        WorldSync.Queue(WorldSync.Change.FightKeeper, Guid.Empty, writer =>
        {
            writer.Write((byte)slot);
            writer.Write(hp);
            writer.Write(maxHp);
            writer.Write(energy);
            writer.Write(armor);
            writer.Write(helmet);
            writer.Write((int)layer);
        });
    }

    [HarmonyPostfix]
    [HarmonyPatch(typeof(FightingGameController), nameof(FightingGameController.Stop))]
    private static void Stopped()
    {
        foreach (var pair in shown)
        {
            UIObjectBubbleManager.Instance?.Hide(pair.Value);
            if (RemoteKeeper.Talking(pair.Key) is PlayerAnimation animation && pair.Value.armor)
            {
                animation.ChangeSkinPreset(PlayerSkinHelper.CurrentPreset);
                animation.ResetArmorLayers();
            }
        }
        shown.Clear();
    }

    internal static void Apply(BinaryReader reader)
    {
        int slot = reader.ReadByte(), hp = reader.ReadInt32(), maxHp = reader.ReadInt32();
        float energy = reader.ReadSingle();
        bool armor = reader.ReadBoolean(), helmet = reader.ReadBoolean();
        var layer = (AnimationComponent.Layers)reader.ReadInt32();
        var session = CoopSession.Current;
        if (session == null || slot == session.LocalSlot ||
            LazySingleton<FightingGameController>.Instance.CurrentFightState == FightState.Disabled)
            return;
        if (!shown.TryGetValue(slot, out var bars))
            shown[slot] = bars = new Bars(slot);
        bars.health.SetCustomHpValue(Math.Max(maxHp, 1));
        bars.health.SetCustomHpValue(hp, overrideMaxHpValue: false);
        bars.energy.value = energy;
        if (RemoteKeeper.Talking(slot) is PlayerAnimation animation && (armor != bars.armor || helmet != bars.helmet || layer != bars.layer))
        {
            if (armor)
            {
                animation.ChangeSkinPreset(helmet ? PlayerSkinHelper.ArmorPreset : PlayerSkinHelper.GetArmorNoHelmetPreset());
                animation.ResetArmorLayers();
                animation.SetLayerWeight(layer, 1f);
            }
            else if (bars.armor)
            {
                animation.ChangeSkinPreset(PlayerSkinHelper.CurrentPreset);
                animation.ResetArmorLayers();
            }
            bars.armor = armor;
            bars.helmet = helmet;
            bars.layer = layer;
        }
        if (RemoteKeeper.BubblePoint(slot) != null)
            UIObjectBubbleManager.Instance?.Display(bars);
        else
            UIObjectBubbleManager.Instance?.Hide(bars);
    }

    // Another player's stamina, read by the game's own stamina bar in place of this player's.
    private sealed class RemoteStamina : PlayerStaminaGameResSystem
    {
        internal float value;

        internal RemoteStamina() : base("stamina", null) { }

        public override float Get() => value;
    }

    private sealed class Bars : IBubbleDrawable
    {
        private readonly int slot;
        internal readonly HPComponent health = new HPComponent(1, 1);
        internal readonly RemoteStamina energy = new RemoteStamina();
        internal bool armor, helmet;
        internal AnimationComponent.Layers layer = (AnimationComponent.Layers)(-1);

        internal Bars(int slot)
        {
            this.slot = slot;
            BubbleDrawableUniqueId = new SGuid(new Guid(0x746d6b00 + slot, 0x6261, 0x7273, new byte[8]));
        }

        public SGuid BubbleDrawableUniqueId { get; }

        public List<LazyWidgetDataBase> BubbleDrawableWidgets
        {
            get
            {
                var stamina = new StaminaBarPlayerWidgetData();
                SetStaminaSystem?.Invoke(stamina, new object[] { energy });
                StaminaSubscribed(stamina) = true; // this player's stamina warnings are not theirs
                return new List<LazyWidgetDataBase> { new HpBarPlayerWidgetData(health), stamina };
            }
        }

        public Vector3 BubbleDrawablePosition
        {
            get
            {
                var point = RemoteKeeper.BubblePoint(slot);
                return point != null ? point.position : Vector3.zero;
            }
        }
    }
}
