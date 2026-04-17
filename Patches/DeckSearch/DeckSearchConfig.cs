using dubiousQOL.Config;

namespace dubiousQOL.Patches;

internal class DeckSearchConfig : FeatureConfig
{
    public static DeckSearchConfig Instance => ConfigRegistry.Get<DeckSearchConfig>();

    public override string Id => "DeckSearch";
    public override string Name => "Deck Search";
    public override string Description => "Text search on the mid-run deck view.";
    public override bool EnabledByDefault => true;

    protected override void DefineEntries(EntryBuilder b) { }
}
