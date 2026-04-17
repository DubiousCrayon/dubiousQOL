using dubiousQOL.Config;
using Godot;

namespace dubiousQOL.Patches;

internal class StatsTrackerConfig : FeatureConfig
{
    public static StatsTrackerConfig Instance => ConfigRegistry.Get<StatsTrackerConfig>();

    public override string Id => "StatsTracker";
    public override string Name => "Stats Tracker";
    public override string Description => "Per-player damage/block/taken stats overlay during runs.";
    public override bool EnabledByDefault => true;
    public override bool RequiresRestart => true;

    public float DefaultWidth => GetFloat("DefaultWidth");
    public float DefaultTopMargin => GetFloat("DefaultTopMargin");

    protected override void DefineEntries(EntryBuilder b)
    {
        b.Float("DefaultWidth", "Default Width", 340f, min: 200f, max: 600f);
        b.Float("DefaultTopMargin", "Top Margin", 85f, min: 0f, max: 300f);
    }
}
