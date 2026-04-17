using dubiousQOL.Config;

namespace dubiousQOL.Patches;

internal class MapHistoryConfig : FeatureConfig
{
    public static MapHistoryConfig Instance => ConfigRegistry.Get<MapHistoryConfig>();

    public override string Id => "MapHistory";
    public override string Name => "Map History";
    public override string Description => "Captures per-act maps and lets you replay them from run history.";
    public override bool EnabledByDefault => true;

    protected override void DefineEntries(EntryBuilder b) { }
}
