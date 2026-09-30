namespace VIBN_Tools.GlobalClasses;

/// <summary>
/// Stable wire names used by the FEE property API. These names are part of
/// the serialized project contract even when a selected SDK version does not
/// expose matching CLR properties.
/// </summary>
internal static class FeeSdkPropertyNames
{
    public const string UseDetectMark = "UseDetectMark";
    public const string DetectMark = "DetectMark";
}
