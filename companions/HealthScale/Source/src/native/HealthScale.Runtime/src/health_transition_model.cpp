#include "health_transition_model.h"

#include <algorithm>
#include <cmath>

namespace hs {
namespace {

double ClampRatio(double value) noexcept {
    if (!std::isfinite(value)) return 0.0;
    return std::clamp(value, 0.0, 1.0);
}

} // namespace

HealthTransitionFrameKind ClassifyHealthTransitionFrame(
    float preTransitionCurrentHp,
    bool sawTemporaryZero,
    float observedCurrentHp) noexcept {
    if (std::isfinite(preTransitionCurrentHp) && preTransitionCurrentHp > 0.0f &&
        std::isfinite(observedCurrentHp) && observedCurrentHp <= 0.0f) {
        return HealthTransitionFrameKind::TemporaryZero;
    }
    if (sawTemporaryZero && std::isfinite(observedCurrentHp) &&
        observedCurrentHp > 0.0f) {
        return HealthTransitionFrameKind::RecoveryBaseline;
    }
    return HealthTransitionFrameKind::Normal;
}

HealthScaleDomainObservation ClassifyHealthScaleValueDomain(
    float canonicalMaximumHp,
    float targetMaximumHp,
    float previousObservedCurrentHp,
    double trackedRatio,
    float observedCurrentHp,
    bool maximumJustChanged) noexcept {
    HealthScaleDomainObservation result;
    if (!std::isfinite(canonicalMaximumHp) || canonicalMaximumHp <= 0.0f ||
        !std::isfinite(targetMaximumHp) || targetMaximumHp <= 0.0f ||
        !std::isfinite(observedCurrentHp) || observedCurrentHp <= 0.0f) {
        return result;
    }

    const double canonical = static_cast<double>(canonicalMaximumHp);
    const double target = static_cast<double>(targetMaximumHp);
    const double observed = static_cast<double>(observedCurrentHp);
    const double scaleFactor = target / canonical;
    const double targetRatio = ClampRatio(observed / target);

    // When the scales are effectively the same, there is no alternate domain.
    if (scaleFactor < 1.5 && scaleFactor > (1.0 / 1.5)) {
        result.domain = HealthScaleValueDomain::TargetScale;
        result.ratio = targetRatio;
        return result;
    }

    const double canonicalRatio = ClampRatio(observed / canonical);
    const bool insideCanonicalRange = observed <= canonical * 1.25;
    const bool previousWasTargetScale =
        std::isfinite(previousObservedCurrentHp) &&
        static_cast<double>(previousObservedCurrentHp) > canonical * 1.5;
    const bool abruptRelapse = previousWasTargetScale && insideCanonicalRange &&
        (static_cast<double>(previousObservedCurrentHp) - observed) > target * 0.25;

    // At the maximum-change edge, current HP commonly remains in the canonical
    // domain. After a correction, a later transformation-owned write can cause
    // the same domain relapse. Accept only a bounded ratio movement so a real
    // catastrophic hit is not silently healed.
    const double held = ClampRatio(trackedRatio);
    const bool canonicalRatioPlausible = maximumJustChanged ||
        canonicalRatio + 0.10 >= held;
    if (insideCanonicalRange && canonicalRatioPlausible &&
        (maximumJustChanged || abruptRelapse)) {
        result.domain = HealthScaleValueDomain::CanonicalScale;
        result.ratio = canonicalRatio;
        result.abruptCanonicalRelapse = abruptRelapse;
        return result;
    }

    if (observed <= target * 1.01) {
        result.domain = HealthScaleValueDomain::TargetScale;
        result.ratio = targetRatio;
        return result;
    }

    result.domain = HealthScaleValueDomain::Ambiguous;
    result.ratio = held;
    return result;
}

double ComputeLiveHealthRatio(
    float currentHp,
    float maximumHp) noexcept {
    if (!std::isfinite(currentHp) || !std::isfinite(maximumHp) ||
        maximumHp <= 0.0f) {
        return 0.0;
    }
    return ClampRatio(
        static_cast<double>(currentHp) / static_cast<double>(maximumHp));
}

bool IsPlausibleHealthPair(
    float currentHp,
    float maximumHp) noexcept {
    // Do not compare current HP with maximum HP here. During transformation and
    // detransformation those values may temporarily belong to different health
    // domains, and the ratio can legitimately exceed any fixed multiplier.
    return std::isfinite(currentHp) && std::isfinite(maximumHp) &&
           maximumHp > 0.0f && currentHp >= -1.0f;
}

bool IsTargetScaleRebaseAfterDecrease(
    float previousScaleHp,
    float targetMaximumHp,
    float observedCurrentHp) noexcept {
    if (!std::isfinite(previousScaleHp) || previousScaleHp <= 0.0f ||
        !std::isfinite(targetMaximumHp) || targetMaximumHp <= 0.0f ||
        !std::isfinite(observedCurrentHp) || observedCurrentHp <= 0.0f) {
        return false;
    }
    const double scaleRatio = static_cast<double>(previousScaleHp) /
        static_cast<double>(targetMaximumHp);
    if (scaleRatio < 1.5) return false;
    return static_cast<double>(observedCurrentHp) <=
        static_cast<double>(targetMaximumHp) * 1.01;
}

float SelectTransitionCurrentScale(
    bool increase,
    float oldMaximumHp,
    float observedCurrentHp,
    float newMaximumHp) noexcept {
    const bool oldValid = std::isfinite(oldMaximumHp) && oldMaximumHp > 0.0f;
    const bool newValid = std::isfinite(newMaximumHp) && newMaximumHp > 0.0f;
    if (!oldValid) return newValid ? newMaximumHp : 1.0f;
    if (!newValid) return oldMaximumHp;

    // Maximum-health increases normally leave current HP on the old scale until
    // the game or HealthScale corrects it. On decreases, values above the new
    // maximum are also unambiguously still on the old scale. If Xenoverse has
    // already clamped/rebased current HP, measure subsequent movement against
    // the new scale instead of counting the clamp itself as damage or healing.
    if (increase || observedCurrentHp > newMaximumHp * 1.01f) {
        return oldMaximumHp;
    }
    return newMaximumHp;
}

HealthTransitionRatioResult ComputeTransitionRatio(
    double preservedRatio,
    float baselineCurrentHp,
    float baselineScaleHp,
    float observedCurrentHp) noexcept {
    HealthTransitionRatioResult result;
    result.ratio = ClampRatio(preservedRatio);
    if (!std::isfinite(baselineCurrentHp) ||
        !std::isfinite(baselineScaleHp) || baselineScaleHp <= 0.0f ||
        !std::isfinite(observedCurrentHp)) {
        return result;
    }

    // During transformations Xenoverse can expose one or more zero-current-HP
    // frames while the fighter is still alive. A zero sample is never allowed
    // to consume the preserved ratio. The runtime separately waits for a
    // nonzero recovery sample and uses that sample only as a fresh baseline.
    // A genuine death remains at zero because the runtime never writes while
    // the observed current HP is zero and eventually cancels the transition.
    if (baselineCurrentHp > 0.0f && observedCurrentHp <= 0.0f) {
        return result;
    }

    result.deltaRatio =
        (static_cast<double>(observedCurrentHp) -
         static_cast<double>(baselineCurrentHp)) /
        static_cast<double>(baselineScaleHp);
    result.ratio = ClampRatio(result.ratio + result.deltaRatio);
    return result;
}

float ComputeCorrectedTransitionCurrent(
    double preservedRatio,
    float baselineCurrentHp,
    float baselineScaleHp,
    float observedCurrentHp,
    float observedMaximumHp) noexcept {
    if (!std::isfinite(observedMaximumHp) || observedMaximumHp <= 0.0f) {
        return 0.0f;
    }
    const auto result = ComputeTransitionRatio(
        preservedRatio,
        baselineCurrentHp,
        baselineScaleHp,
        observedCurrentHp);
    return static_cast<float>(result.ratio * observedMaximumHp);
}

bool IsHudLanePresentationLatched(std::uint64_t readyTick) noexcept {
    return readyTick != 0;
}

bool IsHudLaneSubmissionActive(
    std::uint64_t currentHits,
    std::uint64_t maximumHits,
    std::uint64_t lastCurrentTick,
    std::uint64_t lastMaximumTick,
    std::uint64_t now,
    std::uint64_t maximumAgeMilliseconds,
    std::uint64_t pairSkewMilliseconds,
    std::uint64_t requiredHits) noexcept {
    if (currentHits < requiredHits || maximumHits < requiredHits ||
        lastCurrentTick == 0 || lastMaximumTick == 0 ||
        now < lastCurrentTick || now < lastMaximumTick) {
        return false;
    }
    const auto currentAge = now - lastCurrentTick;
    const auto maximumAge = now - lastMaximumTick;
    const auto skew = lastCurrentTick > lastMaximumTick
        ? lastCurrentTick - lastMaximumTick
        : lastMaximumTick - lastCurrentTick;
    return currentAge <= maximumAgeMilliseconds &&
           maximumAge <= maximumAgeMilliseconds &&
           skew <= pairSkewMilliseconds;
}

} // namespace hs
