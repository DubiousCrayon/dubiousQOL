using dubiousQOL.Config;

namespace dubiousQOL.Patches;

internal class ActNameDisplayConfig : FeatureConfig
{
    public static ActNameDisplayConfig Instance => ConfigRegistry.Get<ActNameDisplayConfig>();

    public override string Id => "ActNameDisplay";
    public override string Name => "Act Name Display";
    public override string Description => "Styled act name label next to the top-bar boss icon.";
    public override bool EnabledByDefault => true;

    protected override void DefineEntries(EntryBuilder b) { }
}
