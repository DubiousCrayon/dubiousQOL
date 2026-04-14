using HarmonyLib;
using MegaCrit.Sts2.Core.Saves;

namespace dubiousQOL.Patches;

/// <summary>
/// Forces the game to use the same save directory regardless of whether mods are loaded.
/// Without this, modded runs save to "modded/profile0/" instead of "profile0/",
/// keeping all progression separate.
/// </summary>
[HarmonyPatch(typeof(UserDataPathProvider), nameof(UserDataPathProvider.IsRunningModded), MethodType.Getter)]
public static class PatchGetIsRunningModded
{
    [HarmonyPrefix]
    public static bool Prefix(ref bool __result)
    {
        if (!DubiousConfig.UnifiedSavePath) return true;
        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(UserDataPathProvider), nameof(UserDataPathProvider.IsRunningModded), MethodType.Setter)]
public static class PatchSetIsRunningModded
{
    [HarmonyPrefix]
    public static bool Prefix(ref bool value)
    {
        if (!DubiousConfig.UnifiedSavePath) return true;
        value = false;
        return true;
    }
}

[HarmonyPatch(typeof(UserDataPathProvider), nameof(UserDataPathProvider.GetProfileDir))]
public static class PatchGetProfileDir
{
    [HarmonyPrefix]
    public static bool Prefix(int profileId, ref string __result)
    {
        if (!DubiousConfig.UnifiedSavePath) return true;
        __result = $"profile{profileId}";
        return false;
    }
}
