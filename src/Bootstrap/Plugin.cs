using System.Linq;
using BepInEx;
using HarmonyLib;

namespace GYK2.TombManyKeepers;

[BepInPlugin("gyk2.tombmanykeepers", "Graveyard Keeper 2: Tomb Many Keepers", "0.1")]
[BepInProcess("GraveyardKeeper2.exe")]
public sealed class Plugin : BaseUnityPlugin
{
    // Each class of patches loads with its feature, unless the player turned the feature off.
    private void Awake()
    {
        FeatureSwitches.Bind(Config);
        string[] off = FeatureSwitches.Off.ToArray();
        if (off.Length > 0)
            Logger.LogInfo("Loading without " + string.Join(", ", off));
        var harmony = new Harmony("gyk2.tombmanykeepers");
        foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly).Where(FeatureSwitches.Loads))
            harmony.CreateClassProcessor(type).Patch();
    }
}
