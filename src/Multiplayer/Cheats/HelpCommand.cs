using System;
using System.Collections.Generic;

namespace GYK2.TombManyKeepers.Multiplayer.Cheats;

// /help [command]: what the commands do, as GYK1's help said it, one line each.
internal static partial class ChatCommands
{
    private static void Help(List<string> args, Action<string> respond)
    {
        switch (args.Count > 1 ? Normalize(args[1]) : string.Empty)
        {
            case "time":
                respond("/time set <time> — set the time: night, dawn, morning, day, noon, evening, HH:mm, an hour 0-24 or 0-1");
                respond("/time set <day> <time> — set the day and the time");
                respond("/time status — show the day and the time");
                break;
            case "weather":
                respond("/weather set <state> — hold one of the game's weather states");
                respond("/weather clear — hold clean weather");
                respond("/weather reset — clean weather that changes on its own again");
                respond("/weather status — show the weather");
                break;
            case "give":
                respond("/give <item> [amount] — give yourself an item, by its name or id");
                respond("/give <player> <item> [amount] — give a player an item");
                respond("/give money <amount> — add to your money");
                respond("/give energy <amount> — change your energy, less with a minus");
                respond("Item suggestions use the game's names, with stars for an item's quality.");
                break;
            case "spawn":
                respond("/spawn corpse [amount] — spawn bodies of your current tier by you");
                respond($"The amount is at most {MaxBodies}.");
                break;
            case "tp":
                respond("/tp <player> — teleport yourself to that player, in their scene");
                break;
            default:
                respond("/help [command] — what a command does");
                respond("/time — set or show the time of day");
                respond("/weather — set or show the weather");
                respond("/give — give items to yourself or others");
                respond("/spawn — spawn bodies by you");
                respond("/tp — teleport yourself to another player");
                respond("Tab completes a command, Up and Down pick a suggestion. The host turns cheats on in the campaign's host settings.");
                break;
        }
    }
}
