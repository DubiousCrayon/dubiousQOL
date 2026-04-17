using dubiousQOL.Config;

namespace dubiousQOL.Patches;

internal class SkipSplashConfig : FeatureConfig
{
    public static SkipSplashConfig Instance => ConfigRegistry.Get<SkipSplashConfig>();

    public override string Id => "SkipSplash";
    public override string Name => "Skip Splash Screen";
    public override string Description => "Skips the MegaCrit intro video on startup.";
    public override bool EnabledByDefault => true;
    public override bool RequiresRestart => true;

    protected override void DefineEntries(EntryBuilder b) { }
}
