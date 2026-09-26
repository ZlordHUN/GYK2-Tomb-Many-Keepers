using System;
using System.IO;
using GYK2.TombManyKeepers.Network.Session;
using HarmonyLib;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.World;

// The host's day, time of day, pace and weather. A joined player's clock runs on between the
// host's updates, but only the host starts a new day or changes the weather.
[HarmonyPatch]
internal static class WorldClock
{
    private const float ShareInterval = 1f;
    // The last moment of a day; the host's update brings the next one.
    private const float DayEnd = 0.9999f;
    private static readonly AccessTools.FieldRef<EnvironmentEngine, float> EngineTime =
        AccessTools.FieldRefAccess<EnvironmentEngine, float>("timeOfDay");
    private static readonly AccessTools.FieldRef<EnvironmentData, int> Day =
        AccessTools.FieldRefAccess<EnvironmentData, int>("day");
    private static readonly AccessTools.FieldRef<Action<int>> NewDayStarted =
        AccessTools.StaticFieldRefAccess<Action<int>>(AccessTools.Field(typeof(EnvironmentEngine), nameof(EnvironmentEngine.OnNewDayStarted)));
    private static float nextShare;
    private static string sharedWeather;
    private static float sharedPace = 1f;
    private static float pace = 1f;
    private static float time;
    private static bool paused;
    private static bool received;

    // How fast the shared world's time runs.
    internal static float Pace => CoopSession.IsGuest ? pace : MainGame.UpdateManager.TimeMultiplier;

    [HarmonyPrefix]
    [HarmonyPatch(typeof(EnvironmentEngine), nameof(EnvironmentEngine.CustomUpdate))]
    private static bool HostTime() => !CoopSession.IsGuest;

    internal static void Share()
    {
        if (!CoopSession.Current.IsHost)
            return;
        string weather = MainGame.Instance.GameSave.weatherData.stateName ?? string.Empty;
        float currentPace = MainGame.UpdateManager.TimeMultiplier;
        if (Time.unscaledTime < nextShare && weather == sharedWeather && Mathf.Approximately(currentPace, sharedPace))
            return;
        nextShare = Time.unscaledTime + ShareInterval;
        sharedWeather = weather;
        sharedPace = currentPace;
        var engine = EnvironmentEngine.Instance;
        var packet = new MemoryStream();
        using (var writer = new BinaryWriter(packet))
        {
            writer.Write(engine.Data.Day);
            writer.Write(EngineTime(engine));
            writer.Write(engine.IsPaused);
            writer.Write(currentPace);
            writer.Write(weather);
        }
        CoopSession.ShareClock(packet.ToArray());
    }

    internal static void Apply(BinaryReader reader)
    {
        int day = reader.ReadInt32();
        time = reader.ReadSingle();
        paused = reader.ReadBoolean();
        pace = reader.ReadSingle();
        string weather = reader.ReadString();
        received = true;
        var engine = EnvironmentEngine.Instance;
        var data = engine.Data;
        if (data.Day != day)
        {
            Day(data) = day;
            NewDayStarted()?.Invoke(day);
        }
        if (paused)
        {
            engine.SetTimeOfDayFake(time);
        }
        else
        {
            engine.IsPaused = false;
            engine.SetTimeOfDay(time);
        }
        if (weather.Length > 0 && MainGame.Instance.GameSave.weatherData.stateName != weather)
            WeatherSystem.Instance.SetWeatherState(weather);
    }

    // A joined player's time moves on at the host's pace until its next update.
    internal static void Advance()
    {
        var energy = MainGame.PlayerData.energySystem;
        // Natively, sleeping resumes a paused clock; a joined keeper sleeps on while the host's stays paused.
        if (!received || paused && !energy.IsSleeping || Time.deltaTime <= 0f)
            return;
        var engine = EnvironmentEngine.Instance;
        float delta = engine.ConvertDeltaTimeToGameplayTime01(Time.deltaTime * pace);
        if (!paused)
        {
            time = Mathf.Min(time + delta, DayEnd);
            engine.SetTimeOfDay(time);
        }
        // The keeper's own tiredness follows the clock, as the native update does.
        if (MainGame.UpdateManager.IsActive)
            energy.UpdateSleepLogic(delta);
    }

    internal static void Clear()
    {
        received = false;
        sharedWeather = null;
        sharedPace = 1f;
        pace = 1f;
        nextShare = 0f;
    }
}
