#include "../../src/native/HealthScale.Runtime/src/health_transition_model.h"

#include <cassert>
#include <cmath>
#include <iostream>
#include <limits>

namespace {

void ExpectNear(double actual, double expected, double tolerance = 1.0e-5) {
    if (std::fabs(actual - expected) > tolerance) {
        std::cerr << "expected " << expected << ", got " << actual << '\n';
        std::abort();
    }
}

} // namespace

int main() {
    using namespace hs;

    const float oldMaximum = 2056.356f;
    const float oldCurrent = 1985.548f;
    const double preserved = static_cast<double>(oldCurrent) / oldMaximum;
    const float increasedMaximum = 4112.713f;
    const float damagedOldScale = oldCurrent - 11.930f;

    const float increaseScale = SelectTransitionCurrentScale(
        true, oldMaximum, oldCurrent, increasedMaximum);
    ExpectNear(increaseScale, oldMaximum, 0.001);
    const auto increaseRatio = ComputeTransitionRatio(
        preserved, oldCurrent, increaseScale, damagedOldScale);
    ExpectNear(increaseRatio.ratio,
               static_cast<double>(damagedOldScale) / oldMaximum,
               1.0e-5);
    const float correctedIncrease = ComputeCorrectedTransitionCurrent(
        preserved, oldCurrent, increaseScale, damagedOldScale,
        increasedMaximum);
    ExpectNear(correctedIncrease,
               (static_cast<double>(damagedOldScale) / oldMaximum) *
                   increasedMaximum,
               0.01);

    const float decreaseOldMaximum = 4112.713f;
    const float decreaseNewMaximum = 2056.356f;
    const float decreaseBaseline = 3273.141f;
    const float decreaseObserved = decreaseBaseline - 106.213f;
    const double decreasePreserved = 0.861996423;
    const float decreaseScale = SelectTransitionCurrentScale(
        false, decreaseOldMaximum, decreaseBaseline, decreaseNewMaximum);
    ExpectNear(decreaseScale, decreaseOldMaximum, 0.001);
    const auto decreaseRatio = ComputeTransitionRatio(
        decreasePreserved, decreaseBaseline, decreaseScale, decreaseObserved);
    ExpectNear(decreaseRatio.deltaRatio,
               -106.213 / decreaseOldMaximum,
               1.0e-5);
    const float correctedDecrease = ComputeCorrectedTransitionCurrent(
        decreasePreserved, decreaseBaseline, decreaseScale, decreaseObserved,
        decreaseNewMaximum);
    ExpectNear(correctedDecrease,
               decreaseRatio.ratio * decreaseNewMaximum,
               0.01);

    assert(ClassifyHealthTransitionFrame(
        oldCurrent, false, oldCurrent) == HealthTransitionFrameKind::Normal);
    assert(ClassifyHealthTransitionFrame(
        oldCurrent, false, 0.0f) == HealthTransitionFrameKind::TemporaryZero);
    assert(ClassifyHealthTransitionFrame(
        oldCurrent, true, 0.0f) == HealthTransitionFrameKind::TemporaryZero);
    assert(ClassifyHealthTransitionFrame(
        oldCurrent, true, 3500.0f) ==
        HealthTransitionFrameKind::RecoveryBaseline);

    // A zero-current-HP transformation frame is transitional evidence, not
    // damage. It must leave the exact held percentage untouched.
    const auto zeroAtMaximumChange = ComputeTransitionRatio(
        preserved, oldCurrent, oldMaximum, 0.0f);
    ExpectNear(zeroAtMaximumChange.ratio, preserved, 1.0e-9);
    ExpectNear(zeroAtMaximumChange.deltaRatio, 0.0, 1.0e-12);

    // Repeated zero frames also hold the ratio. The first recovered nonzero
    // sample is a fresh baseline, so it cannot be counted as healing.
    const float recoveredCurrent = 3500.0f;
    const auto repeatedZero = ComputeTransitionRatio(
        zeroAtMaximumChange.ratio, oldCurrent, oldMaximum, 0.0f);
    ExpectNear(repeatedZero.ratio, preserved, 1.0e-9);
    const auto recoveredBaseline = ComputeTransitionRatio(
        repeatedZero.ratio, recoveredCurrent, increasedMaximum, recoveredCurrent);
    ExpectNear(recoveredBaseline.ratio, preserved, 1.0e-9);

    // Real movement after recovery is still counted normally.
    const auto damageAfterRecovery = ComputeTransitionRatio(
        recoveredBaseline.ratio, recoveredCurrent, increasedMaximum,
        recoveredCurrent - 75.0f);
    ExpectNear(damageAfterRecovery.ratio,
               preserved - 75.0 / increasedMaximum, 1.0e-5);

    // A staged maximum change carries forward the already-tracked ratio, then
    // starts a fresh baseline so the second rebase is not mistaken for healing.
    const auto stageOne = ComputeTransitionRatio(
        preserved, oldCurrent, oldMaximum, oldCurrent - 20.0f);
    const float stageTwoBaseline = 3900.0f;
    const auto stageTwo = ComputeTransitionRatio(
        stageOne.ratio, stageTwoBaseline, increasedMaximum,
        stageTwoBaseline - 40.0f);
    ExpectNear(stageTwo.ratio,
               stageOne.ratio - 40.0 / increasedMaximum,
               1.0e-5);

    // A decrease that has already clamped/rebased current HP uses the new scale;
    // the clamp is the baseline and is not counted as transition damage.
    const float clampedScale = SelectTransitionCurrentScale(
        false, decreaseOldMaximum, decreaseNewMaximum, decreaseNewMaximum);
    ExpectNear(clampedScale, decreaseNewMaximum, 0.001);


    // Rapid transformation/reversion chains carry one ratio across both
    // directions instead of cancelling when the maximum crosses its origin.
    const double chainRatio = 0.994200351;
    const float revertedCurrent = 2101.622f;
    const float revertedMaximum = 2101.622f;
    const float chainScale = SelectTransitionCurrentScale(
        false, 42032.445f, revertedCurrent, revertedMaximum);
    ExpectNear(chainScale, revertedMaximum, 0.001);
    const auto revertedStage = ComputeTransitionRatio(
        chainRatio, revertedCurrent, chainScale, revertedCurrent);
    ExpectNear(revertedStage.ratio, chainRatio, 1.0e-8);
    // A clamp to the new smaller maximum is a source baseline, not a full
    // heal. Holding the baseline must preserve the pre-reversion ratio.
    const auto clampedReversion = ComputeTransitionRatio(
        0.739557454, revertedCurrent, revertedMaximum, revertedCurrent);
    ExpectNear(clampedReversion.ratio, 0.739557454, 1.0e-9);
    const auto postReversionDamage = ComputeTransitionRatio(
        0.739557454, revertedCurrent, revertedMaximum, revertedCurrent - 100.0f);
    ExpectNear(postReversionDamage.ratio,
               0.739557454 - 100.0 / revertedMaximum, 1.0e-5);

    // Regression from the live 2026-07-11 report: a completed 2x health
    // transition left a domain lease at an older 62.5052203%, while the
    // fighter had since fallen to 1965.425 / 4203.245 = 46.7597083%. A new
    // detransformation must start from the live percentage, never the lease.
    const double liveDetransformRatio = ComputeLiveHealthRatio(
        1965.425f, 4203.245f);
    ExpectNear(liveDetransformRatio, 0.467597083, 1.0e-6);
    const double staleDetransformLease = 0.625052203;
    assert(std::fabs(liveDetransformRatio - staleDetransformLease) > 0.15);
    ExpectNear(liveDetransformRatio * 2101.622, 982.712, 0.01);

    // Regression from the same report: a later 2x -> 3x change occurred at
    // 1772.790 / 4203.245 = 42.1767126%, but the old lease still held
    // 46.0152476%. The new transition must preserve the live 42.1767%.
    const double liveChainRatio = ComputeLiveHealthRatio(
        1772.790f, 4203.245f);
    ExpectNear(liveChainRatio, 0.421767126, 1.0e-6);
    const double staleChainLease = 0.460152476;
    assert(std::fabs(liveChainRatio - staleChainLease) > 0.03);
    ExpectNear(liveChainRatio * 6304.867, 2659.186, 0.02);

    // Invalid live health never imports a historical ratio by accident.
    ExpectNear(ComputeLiveHealthRatio(100.0f, 0.0f), 0.0, 0.0);
    ExpectNear(ComputeLiveHealthRatio(-10.0f, 100.0f), 0.0, 0.0);
    ExpectNear(ComputeLiveHealthRatio(150.0f, 100.0f), 1.0, 0.0);

    // Kaioken x20 cancellation can expose old-scale current HP after maximum HP
    // already returned to base. Transformation magnitude is not a validity
    // criterion, so the numeric pair remains accepted without a ratio ceiling.
    assert(IsPlausibleHealthPair(1730925.0f, 90000.0f));

    // Future transformations and custom stacks may be many orders of magnitude
    // above the visible base maximum. Any finite nonnegative current HP remains
    // structurally valid; the transition state machine freezes the last coherent
    // percentage until the new domain stabilizes.
    assert(IsPlausibleHealthPair(9.0e12f, 90000.0f));
    assert(IsPlausibleHealthPair(
        std::numeric_limits<float>::max(), 1.0f));

    // Numeric corruption and impossible maximums are still rejected.
    assert(!IsPlausibleHealthPair(-2.0f, 90000.0f));
    assert(!IsPlausibleHealthPair(100.0f, 0.0f));
    assert(!IsPlausibleHealthPair(
        std::numeric_limits<float>::infinity(), 90000.0f));
    assert(!IsPlausibleHealthPair(
        std::numeric_limits<float>::quiet_NaN(), 90000.0f));

    // The first 90,000/90,000 sample after the x20 domain collapses is a target
    // rebase baseline, not proof that the fighter should heal to 100%.
    assert(IsTargetScaleRebaseAfterDecrease(
        1800000.0f, 90000.0f, 90000.0f));
    assert(IsTargetScaleRebaseAfterDecrease(
        1800000.0f, 90000.0f, 72000.0f));
    assert(!IsTargetScaleRebaseAfterDecrease(
        1800000.0f, 90000.0f, 1730925.0f));
    const double kaiokenHeldRatio = 1730925.0 / 1800000.0;
    ExpectNear(kaiokenHeldRatio, 0.961625, 1.0e-9);
    ExpectNear(kaiokenHeldRatio * 90000.0, 86546.25, 0.001);

    assert(!IsHudLanePresentationLatched(0));
    assert(IsHudLanePresentationLatched(1234));
    // A stale writer heartbeat does not clear the HUD-presence latch. The two
    // signals remain deliberately separate for the health presentation tracker.
    assert(!IsHudLaneSubmissionActive(4, 4, 1000, 1001, 2000, 250, 100, 4));
    assert(IsHudLanePresentationLatched(1001));

    assert(!IsHudLaneSubmissionActive(3, 4, 1000, 1001, 1010, 250, 100, 4));
    assert(IsHudLaneSubmissionActive(4, 4, 1000, 1001, 1010, 250, 100, 4));
    assert(!IsHudLaneSubmissionActive(4, 4, 1000, 1001, 1400, 250, 100, 4));
    assert(!IsHudLaneSubmissionActive(4, 4, 1000, 1200, 1210, 250, 100, 4));

    // A maximum-HP jump leaves current HP in the canonical/original domain.
    const auto canonicalAtChange = ClassifyHealthScaleValueDomain(
        2101.622f, 42032.445f, 2101.622f, 1.0, 2078.505f, true);
    assert(canonicalAtChange.domain == HealthScaleValueDomain::CanonicalScale);
    ExpectNear(canonicalAtChange.ratio, 2078.505 / 2101.622, 1.0e-5);

    // After a successful target-domain correction, a transformation-owned late
    // write can relapse current HP back into the canonical 2101-point domain.
    const auto lateRelapse = ClassifyHealthScaleValueDomain(
        2101.622f, 42032.445f, 27341.127f, 0.654870941,
        1624.568f, false);
    assert(lateRelapse.domain == HealthScaleValueDomain::CanonicalScale);
    assert(lateRelapse.abruptCanonicalRelapse);
    ExpectNear(lateRelapse.ratio, 1624.568 / 2101.622, 1.0e-5);

    // A late canonical-domain restart is continuity-anchored to the already
    // held target-domain ratio. The first canonical sample must not heal the
    // fighter merely because its absolute value is higher on the small scale.
    const double heldTargetRatio = 0.654870941;
    const float canonicalRestart = 1624.568f;
    const auto anchoredRestart = ComputeTransitionRatio(
        heldTargetRatio, canonicalRestart, 2101.622f, canonicalRestart);
    ExpectNear(anchoredRestart.ratio, heldTargetRatio, 1.0e-9);
    const auto canonicalDrain = ComputeTransitionRatio(
        heldTargetRatio, canonicalRestart, 2101.622f, 1527.896f);
    ExpectNear(canonicalDrain.ratio,
               heldTargetRatio + (1527.896 - canonicalRestart) / 2101.622,
               1.0e-5);
    assert(canonicalDrain.ratio < heldTargetRatio);

    // A truly catastrophic target-domain hit is ambiguous rather than being
    // automatically remapped/healed as canonical-domain current HP.
    const auto catastrophic = ClassifyHealthScaleValueDomain(
        2101.622f, 42032.445f, 27341.127f, 0.654870941,
        1000.0f, false);
    assert(catastrophic.domain != HealthScaleValueDomain::CanonicalScale);

    std::cout << "health_transition_model_tests: PASS\n";
    return 0;
}
