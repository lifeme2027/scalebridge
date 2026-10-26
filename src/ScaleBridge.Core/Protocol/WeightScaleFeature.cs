namespace ScaleBridge.Core.Protocol;

/// <summary>Weight Scale Feature（0x2A9E，uint32）。位布局见 NOTES.md「协议信息来源」。</summary>
public sealed record WeightScaleFeature(
    uint Raw,
    bool TimeStampSupported,
    bool MultipleUsersSupported,
    bool BmiSupported,
    int WeightResolutionCode,
    int HeightResolutionCode)
{
    public decimal? WeightResolutionKg => FeatureResolutions.MassKg(WeightResolutionCode);
    public decimal? WeightResolutionLb => FeatureResolutions.MassLb(WeightResolutionCode);
    public decimal? HeightResolutionMeters => FeatureResolutions.HeightMeters(HeightResolutionCode);
    public decimal? HeightResolutionInches => FeatureResolutions.HeightInches(HeightResolutionCode);
}
