using dubiousQOL.Config;

namespace dubiousQOL.Patches;

internal class IncomingDamageDisplayConfig : FeatureConfig
{
    public static IncomingDamageDisplayConfig Instance => ConfigRegistry.Get<IncomingDamageDisplayConfig>();

    public override string Id => "IncomingDamageDisplay";
    public override string Name => "Incoming Damage Display";
    public override string Description => "Predicted incoming damage and HP loss next to the player's health bar.";
    public override bool EnabledByDefault => true;

    protected override void DefineEntries(EntryBuilder b) { }
}
