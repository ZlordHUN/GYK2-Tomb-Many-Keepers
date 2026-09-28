using System;
using System.IO;
using System.Security.Cryptography;
using UnityEngine;

namespace GYK2.TombManyKeepers.Network.Session;

// Which build of the mod a game runs: the SHA-256 of its DLL, as the validation notes record each build's. The
// version stays the same from build to build, and two builds that differ in anything may send, read and simulate
// the game differently, so games play together only on the same build: a joining game tells the host its build, the
// host refuses another, and the browser marks a game of another build, as GYK1's marked a game of another version.
// Players are shown the hash's first letters.
internal static class ModBuild
{
    private const int Shown = 8;
    private static string id;

    // The whole hash in lower-case hex. A DLL the game cannot read is known by its module's id instead, which
    // changes with every build as well.
    internal static string Id => id ??= Read();

    internal static bool Matches(string build) => string.Equals(build, Id, StringComparison.Ordinal);

    // Why a player cannot join the host's game, as GYK1 said a version mismatch, for the player joining.
    internal static string Mismatch(string yours, string host) =>
        "Cannot join: mod build mismatch.\n" +
        $"Your build: {Short(yours)}\n" +
        $"Host build: {Short(host)}\n" +
        "Both players need the same GYK2.TombManyKeepers.dll.";

    // An older game than those that tell their build said nothing of it.
    private static string Short(string build) =>
        string.IsNullOrEmpty(build) ? "unavailable (older mod)" : build.Length <= Shown ? build : build.Substring(0, Shown);

    private static string Read()
    {
        var assembly = typeof(ModBuild).Assembly;
        try
        {
            using var sha = SHA256.Create();
            using var file = File.OpenRead(assembly.Location);
            string hash = BitConverter.ToString(sha.ComputeHash(file)).Replace("-", string.Empty).ToLowerInvariant();
            Debug.Log($"[Multiplayer] This game runs build {Short(hash)} of the mod, SHA-256 {hash}");
            return hash;
        }
        catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException ||
                                          exception is ArgumentException || exception is NotSupportedException)
        {
            string module = assembly.ManifestModule.ModuleVersionId.ToString("N");
            Debug.LogWarning($"[Multiplayer] Could not read the mod's DLL ({exception.Message}); its build is known by its module id {module}");
            return module;
        }
    }
}
