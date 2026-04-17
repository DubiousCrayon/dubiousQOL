using dubiousQOL.Config;

namespace dubiousQOL.Patches;

internal class RarityDisplayConfig : FeatureConfig
{
    public static RarityDisplayConfig Instance => ConfigRegistry.Get<RarityDisplayConfig>();

    public override string Id => "RarityDisplay";
    public override string Name => "Rarity Display";
    public override string Description => "Color-coded rarity on relic/potion headers and hover tips.";
    public override bool EnabledByDefault => true;

    protected override void DefineEntries(EntryBuilder b) { }
}
