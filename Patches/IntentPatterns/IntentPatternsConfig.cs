using dubiousQOL.Config;

namespace dubiousQOL.Patches;

internal class IntentPatternsConfig : FeatureConfig
{
    public static IntentPatternsConfig Instance => ConfigRegistry.Get<IntentPatternsConfig>();

    public override string Id => "IntentPatterns";
    public override string Name => "Enemy Intent";
    public override string Description => "Middle-click an enemy during combat to view their move patterns.";
    public override bool EnabledByDefault => true;

    protected override void DefineEntries(EntryBuilder b) { }
}
