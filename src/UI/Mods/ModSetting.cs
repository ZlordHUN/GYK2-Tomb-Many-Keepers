using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;

namespace GYK2.TombManyKeepers.UI.Mods;

// One setting of a mod as the Mods window shows it: an entry of the mod's BepInEx configuration, which every
// BepInEx mod keeps. On and off, one of a few named values, a value from the mod's own list or a small range of whole
// numbers is switched with arrows; a longer range is slid; anything else, such as one of every key, is typed as the
// configuration file writes it. A change
// is kept at once, as BepInEx saves a mod's configuration. Mods mark settings for menus as BepInEx's own
// configuration manager reads them: hidden, read-only, with another name or in another order.
internal sealed class ModSetting
{
    internal enum Kinds
    {
        Switched,
        Slid,
        Typed,
        Shown
    }

    // The most whole numbers switched with arrows, and slid, and the most named values switched.
    private const int MostSwitched = 21, MostSlid = 101, MostNamed = 50;
    // A slid fraction takes about this many steps from end to end.
    private const int FractionSteps = 100;

    private readonly ConfigEntryBase entry;
    private readonly object[] choices;

    internal string Section => entry.Definition.Section;
    internal string Name { get; }
    internal Kinds Kind { get; }
    internal string[] Labels { get; }
    internal double Minimum { get; }
    internal double Maximum { get; }
    // How far one step of a slid setting moves it.
    internal double Step { get; }
    private int Order { get; }

    private ModSetting(ConfigEntryBase entry, string name, bool readOnly, int order)
    {
        this.entry = entry;
        Name = name;
        Order = order;
        var type = entry.SettingType;
        var acceptable = entry.Description.AcceptableValues;
        if (readOnly)
        {
            Kind = Kinds.Shown;
        }
        else if (type == typeof(bool))
        {
            choices = new object[] { false, true };
        }
        else if (acceptable != null && Listed(acceptable) is Array listed)
        {
            choices = listed.Cast<object>().ToArray();
        }
        else if (type.IsEnum && !type.IsDefined(typeof(FlagsAttribute), false) && Enum.GetValues(type).Length <= MostNamed)
        {
            choices = Enum.GetValues(type).Cast<object>().ToArray();
        }
        else if (acceptable != null && Ranged(acceptable, out double minimum, out double maximum) && Numeric(type, out bool whole))
        {
            Minimum = minimum;
            Maximum = maximum;
            double count = maximum - minimum + 1;
            if (whole && count <= MostSwitched)
                choices = Enumerable.Range(0, (int)count).Select(step => Convert.ChangeType(minimum + step, type, CultureInfo.InvariantCulture)).ToArray();
            else if (whole && count <= MostSlid)
            {
                Kind = Kinds.Slid;
                Step = 1;
            }
            else if (!whole)
            {
                Kind = Kinds.Slid;
                Step = Rounded((maximum - minimum) / FractionSteps);
            }
            else
                Kind = Kinds.Typed;
        }
        else
            Kind = Kinds.Typed;
        if (choices != null)
        {
            Kind = Kinds.Switched;
            Labels = choices.Select(Shown).ToArray();
        }
    }

    // The settings of a mod in the order its configuration file lists them: its sections by name, and within each,
    // those marked first as their mods order them, then as the mod made them.
    internal static List<ModSetting> Of(PluginInfo plugin)
    {
        var settings = new List<ModSetting>();
        var config = plugin?.Instance == null ? null : plugin.Instance.Config;
        if (config == null)
            return settings;
        foreach (var entry in config.Keys.Select(key => config[key]))
        {
            var marks = entry.Description.Tags?.FirstOrDefault(tag => tag?.GetType().Name == "ConfigurationManagerAttributes");
            if (Mark<bool?>(marks, "Browsable") == false)
                continue;
            string name = Mark<string>(marks, "DispName");
            settings.Add(new ModSetting(entry, string.IsNullOrEmpty(name) ? entry.Definition.Key : name,
                Mark<bool?>(marks, "ReadOnly") == true, Mark<int?>(marks, "Order") ?? 0));
        }
        return settings.GroupBy(setting => setting.Section).OrderBy(section => section.Key)
            .SelectMany(section => section.OrderByDescending(setting => setting.Order)).ToList();
    }

    // Where a switched setting's value stands among its choices.
    internal int Index => Math.Max(0, Array.FindIndex(choices, choice => Equals(choice, entry.BoxedValue)));

    internal void Choose(int index) => entry.BoxedValue = choices[index];

    // A slid setting's value, and its position on the slider from the start.
    internal double Number => Convert.ToDouble(entry.BoxedValue, CultureInfo.InvariantCulture);

    internal int Position => (int)Math.Round((Number - Minimum) / Step);

    internal int Positions => (int)Math.Round((Maximum - Minimum) / Step);

    internal void Slide(int position) => entry.BoxedValue = At(position);

    // The value a position on the slider stands for, as a player reads it.
    internal string ValueAt(int position) => Shown(At(position));

    private object At(int position) =>
        Convert.ChangeType(Math.Round(Math.Min(Maximum, Minimum + position * Step), 6), entry.SettingType, CultureInfo.InvariantCulture);

    // The value as it is typed: a text as it is, anything else as the configuration file writes it.
    internal string Text => entry.BoxedValue is string text ? text : TomlTypeConverter.ConvertToString(entry.BoxedValue, entry.SettingType);

    // Keeps a typed value the mod takes; whatever it cannot read leaves the setting as it was.
    internal bool Type(string text)
    {
        if (entry.SettingType == typeof(string))
        {
            entry.BoxedValue = text;
            return true;
        }
        try
        {
            object value = TomlTypeConverter.ConvertToValue(text, entry.SettingType);
            // A reader that makes nothing of a text, as a key shortcut's does of one it cannot read, keeps the setting.
            if (text.Trim().Length > 0 && TomlTypeConverter.ConvertToString(value, entry.SettingType).Length == 0)
                return false;
            entry.BoxedValue = value;
            return true;
        }
        catch (Exception exception) when (!(exception is OutOfMemoryException))
        {
            return false;
        }
    }

    // What the setting is for, as its mod describes it, and its default.
    internal string Description
    {
        get
        {
            string about = entry.Description.Description?.Trim();
            string range = Kind == Kinds.Typed && entry.Description.AcceptableValues != null
                ? $"  ({Shown(Convert.ChangeType(Minimum, entry.SettingType, CultureInfo.InvariantCulture))} to " +
                  $"{Shown(Convert.ChangeType(Maximum, entry.SettingType, CultureInfo.InvariantCulture))})"
                : string.Empty;
            string fallback = "Default: " + Shown(entry.DefaultValue) + range;
            return string.IsNullOrEmpty(about) ? fallback : about + "\n" + fallback;
        }
    }

    // The value as a player reads it.
    internal string Value => Shown(entry.BoxedValue);

    private static string Shown(object value)
    {
        switch (value)
        {
            case bool on:
                return on ? "On" : "Off";
            case float _:
            case double _:
            case decimal _:
                return Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("0.##", CultureInfo.InvariantCulture);
            case string text:
                return text;
            case null:
                return string.Empty;
            default:
                return value is Enum ? value.ToString() : TomlTypeConverter.ConvertToString(value, value.GetType());
        }
    }

    // A step of about the length given, rounded up to one, two or five times a power of ten.
    private static double Rounded(double length)
    {
        if (length <= 0)
            return 1;
        double power = Math.Pow(10, Math.Floor(Math.Log10(length)));
        foreach (double multiple in new[] { 1d, 2d, 5d, 10d })
            if (length <= multiple * power * 1.0000001)
                return multiple * power;
        return 10 * power;
    }

    private static Array Listed(AcceptableValueBase acceptable) =>
        acceptable.GetType().GetProperty("AcceptableValues")?.GetValue(acceptable) as Array;

    private static bool Ranged(AcceptableValueBase acceptable, out double minimum, out double maximum)
    {
        minimum = maximum = 0;
        var type = acceptable.GetType();
        object low = type.GetProperty("MinValue")?.GetValue(acceptable), high = type.GetProperty("MaxValue")?.GetValue(acceptable);
        if (low == null || high == null)
            return false;
        try
        {
            minimum = Convert.ToDouble(low, CultureInfo.InvariantCulture);
            maximum = Convert.ToDouble(high, CultureInfo.InvariantCulture);
            return maximum > minimum;
        }
        catch (Exception exception) when (exception is InvalidCastException || exception is FormatException || exception is OverflowException)
        {
            return false;
        }
    }

    private static bool Numeric(Type type, out bool whole)
    {
        whole = type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte) || type == typeof(sbyte) ||
                type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort);
        return whole || type == typeof(float) || type == typeof(double) || type == typeof(decimal);
    }

    // A mark a mod gave the setting for menus, by its name, or none.
    private static T Mark<T>(object marks, string name)
    {
        if (marks == null)
            return default;
        var type = marks.GetType();
        object value = type.GetField(name)?.GetValue(marks) ?? type.GetProperty(name)?.GetValue(marks);
        return value is T mark ? mark : default;
    }
}
