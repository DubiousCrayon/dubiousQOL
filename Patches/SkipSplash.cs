using HarmonyLib;
using MegaCrit.Sts2.Core.Nodes;

namespace dubiousQOL.Patches;

/// <summary>
/// Skips the MegaCrit/Slay the Spire 2 logo splash on launch, dropping the player
/// straight into the main menu. Forces NGame.LaunchMainMenu(skipLogo:true).
/// </summary>
[HarmonyPatch(typeof(NGame), "LaunchMainMenu")]
public static class PatchSkipSplash
{
    [HarmonyPrefix]
    public static void Prefix(ref bool skipLogo)
    {
        if (!DubiousConfig.SkipSplash) return;
        skipLogo = true;
    }
}
