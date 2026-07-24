using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.Runtime;

internal static class TelemetryComparisonPolicy
{
    internal const string PolicyId = "compressed-scale-v1";
    internal const double AbsoluteTolerance = 1.0e-6;
    internal const double RelativeTolerance = 1.0e-6;

    internal static bool NumericEquivalent(double previous, double current)
    {
        if (!double.IsFinite(previous) || !double.IsFinite(current))
        {
            return BitConverter.DoubleToInt64Bits(previous) == BitConverter.DoubleToInt64Bits(current);
        }

        double scale = Math.Max(Math.Abs(previous), Math.Abs(current));
        double tolerance = Math.Max(AbsoluteTolerance, scale * RelativeTolerance);
        return Math.Abs(current - previous) <= tolerance;
    }

    internal static bool Changed(float previous, float current) =>
        !NumericEquivalent(previous, current);

    internal static ComparisonPolicyMessage Describe() => new(
        PolicyId,
        AbsoluteTolerance,
        RelativeTolerance,
        "Chronology uses exact raw-bit equality; semantic/scanner comparisons use compressed-scale-v1.",
        "Recognize 0.01-scale combat changes without treating floating-point noise as gameplay state.");
}
