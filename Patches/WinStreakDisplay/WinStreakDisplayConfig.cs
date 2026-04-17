using dubiousQOL.Config;

namespace dubiousQOL.Patches;

internal class WinStreakDisplayConfig : FeatureConfig
{
    public static WinStreakDisplayConfig Instance => ConfigRegistry.Get<WinStreakDisplayConfig>();

    public override string Id => "WinStreakDisplay";
    public override string Name => "Win Streak Display";
    public override string Description => "Win-streak flame badge on the top bar.";
    public override bool EnabledByDefault => true;

    protected override void DefineEntries(EntryBuilder b) { }
}
