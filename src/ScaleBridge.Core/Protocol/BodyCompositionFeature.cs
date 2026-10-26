namespace ScaleBridge.Core.Protocol;

/// <summary>Body Composition Feature（0x2A9B，uint32）。位布局见 NOTES.md「协议信息来源」。</summary>
public sealed record BodyCompositionFeature(
    uint Raw,
    bool TimeStampSupported,
    bool MultipleUsersSupported,
    bool BasalMetabolismSupported,
    bool MusclePercentageSupported,
    bool MuscleMassSupported,
    bool FatFreeMassSupported,
    bool SoftLeanMassSupported,
    bool BodyWaterMassSupported,
    bool ImpedanceSupported,
    bool WeightSupported,
    bool HeightSupported,
    int MassResolutionCode,
    int HeightResolutionCode)
{
    public decimal? MassResolutionKg => FeatureResolutions.MassKg(MassResolutionCode);
    public decimal? MassResolutionLb => FeatureResolutions.MassLb(MassResolutionCode);
    public decimal? HeightResolutionMeters => FeatureResolutions.HeightMeters(HeightResolutionCode);
    public decimal? HeightResolutionInches => FeatureResolutions.HeightInches(HeightResolutionCode);
}
