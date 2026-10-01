using System.Collections.Generic;
using GYK2.TombManyKeepers.Network.Session;
using UnityEngine;

namespace GYK2.TombManyKeepers.Multiplayer.Chat;

// The host keeps any player from flooding everyone's chat. Every line said for a player counts, what they type and
// what their readying up or taking it back tells the lobby: whoever has ten said within ten seconds waits thirty from
// the tenth, and is told so. Meanwhile their lines are refused, each with how long they still wait, and their
// readiness changes unheard.
internal static class ChatCooldown
{
    internal const int Burst = 10;
    internal const float Window = 10f, Wait = 30f;
    // When each player's recent lines were said, oldest first, and until when each player waits.
    private static readonly Queue<float>[] Said = Slots();
    private static readonly float[] Until = new float[CoopSession.MaxPlayers + 1];

    internal static string Started => $"You sent {Burst} messages within {Seconds(Window)}. You can send messages again in {Seconds(Wait)}.";

    // Whole seconds, rounded up past what adding the wait to the clock rounds away.
    internal static string Waiting(int slot) => $"You can send messages again in {Seconds(Mathf.Max(1f, Mathf.Ceil(Remaining(slot) - 0.01f)))}.";

    // Seconds the player still waits, none once they may be heard.
    internal static float Remaining(int slot) => Mathf.Max(0f, Until[slot] - Time.unscaledTime);

    // A line said for the player; whether it was the last their burst allows, which starts their wait.
    internal static bool Count(int slot)
    {
        var said = Said[slot];
        float now = Time.unscaledTime;
        while (said.Count > 0 && now - said.Peek() >= Window)
            said.Dequeue();
        said.Enqueue(now);
        if (said.Count < Burst)
            return false;
        said.Clear();
        Until[slot] = now + Wait;
        return true;
    }

    // A player who leaves takes their count with them, so whoever takes their place starts afresh.
    internal static void Forget(int slot)
    {
        Said[slot].Clear();
        Until[slot] = 0f;
    }

    internal static void Clear()
    {
        for (int slot = 0; slot <= CoopSession.MaxPlayers; slot++)
            Forget(slot);
    }

    private static string Seconds(float seconds) => seconds == 1f ? "1 second" : $"{seconds:0} seconds";

    private static Queue<float>[] Slots()
    {
        var slots = new Queue<float>[CoopSession.MaxPlayers + 1];
        for (int slot = 0; slot < slots.Length; slot++)
            slots[slot] = new Queue<float>();
        return slots;
    }
}
