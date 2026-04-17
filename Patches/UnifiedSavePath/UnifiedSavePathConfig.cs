using dubiousQOL.Config;

namespace dubiousQOL.Patches;

internal class UnifiedSavePathConfig : FeatureConfig
{
    public static UnifiedSavePathConfig Instance => ConfigRegistry.Get<UnifiedSavePathConfig>();

    public override string Id => "UnifiedSavePath";
    public override string Name => "Unified Save Path";
    public override string Description => "Forces modded and unmodded saves to share the same folder.";
    public override bool EnabledByDefault => true;
    public override bool RequiresRestart => true;

    protected override void DefineEntries(EntryBuilder b) { }
}
