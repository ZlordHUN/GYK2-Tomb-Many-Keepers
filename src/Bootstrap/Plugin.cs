using BepInEx;
using HarmonyLib;

namespace GYK2.TombManyKeepers;

[BepInPlugin("gyk2.tombmanykeepers", "Graveyard Keeper 2: Tomb Many Keepers", "0.1")]
[BepInProcess("GraveyardKeeper2.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    private void Awake() => Harmony.CreateAndPatchAll(typeof(Plugin).Assembly, "gyk2.tombmanykeepers");
}
