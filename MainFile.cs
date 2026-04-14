using Godot;
using HarmonyLib;
using MegaCrit.Sts2.Core.Modding;
using MegaCrit.Sts2.Core.Saves;

namespace dubiousQOL;

[ModInitializer(nameof(Initialize))]
public partial class MainFile : Node
{
    internal const string ModId = "dubiousQOL";

    public static MegaCrit.Sts2.Core.Logging.Logger Logger { get; } =
        new(ModId, MegaCrit.Sts2.Core.Logging.LogType.Generic);

    public static void Initialize()
    {
        DubiousConfig.Load();

        Harmony harmony = new(ModId);
        harmony.PatchAll();

        // Force unified save path immediately in case it was already set
        if (DubiousConfig.UnifiedSavePath)
            UserDataPathProvider.IsRunningModded = false;
    }
}