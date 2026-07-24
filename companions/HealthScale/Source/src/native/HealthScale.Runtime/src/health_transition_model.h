#pragma once

#include <cstdint>

namespace hs {

struct HealthTransitionRatioResult {
    double ratio = 0.0;
    double deltaRatio = 0.0;
};

enum class HealthTransitionFrameKind : std::uint8_t {
    Normal = 0,
    TemporaryZero,
    RecoveryBaseline,
};

// Classifies a current-HP sample before any transition ratio, baseline, domain,
// last-observed value, or stabilization counter is allowed to change.
[[nodiscard]] HealthTransitionFrameKind ClassifyHealthTransitionFrame(
    float preTransitionCurrentHp,
    bool sawTemporaryZero,
    float observedCurrentHp) noexcept;

enum class HealthScaleValueDomain : std::uint8_t {
    Invalid = 0,
    CanonicalScale,
    TargetScale,
    Ambiguous,
};

struct HealthScaleDomainObservation {
    HealthScaleValueDomain domain = HealthScaleValueDomain::Invalid;
    double ratio = 0.0;
    bool abruptCanonicalRelapse = false;
};

// Classifies a current-HP sample while maximum HP is scaled away from the
// fighter's canonical/original maximum. Xenoverse and transformation mods may
// later rewrite current HP in the canonical domain even though maximum HP
// remains in the target domain. Those scale relapses must be mapped back to the
// target domain without mistaking them for catastrophic damage.
[[nodiscard]] HealthScaleDomainObservation ClassifyHealthScaleValueDomain(
    float canonicalMaximumHp,
    float targetMaximumHp,
    float previousObservedCurrentHp,
    double trackedRatio,
    float observedCurrentHp,
    bool maximumJustChanged) noexcept;

// Returns the authoritative percentage immediately before a distinct
// maximum-health change. This intentionally uses only the live current and
// maximum values; historical transition/domain state must not override it.
[[nodiscard]] double ComputeLiveHealthRatio(
    float currentHp,
    float maximumHp) noexcept;

// Validates only the numeric shape of a Battle_Mob health pair. Transformation
// magnitude is deliberately unbounded: mixed-domain frames may expose current
// HP from any prior scale while maximum HP already belongs to the new scale.
// Object ownership, vtable/range checks, and the transition state machine form
// the safety boundary instead of an arbitrary current/max multiplier ceiling.
[[nodiscard]] bool IsPlausibleHealthPair(
    float currentHp,
    float maximumHp) noexcept;

// Detects the first target-domain sample after a large maximum-health decrease.
// That sample is a rebase/clamp baseline and must not be counted as damage or
// accepted as a new full-health percentage.
[[nodiscard]] bool IsTargetScaleRebaseAfterDecrease(
    float previousScaleHp,
    float targetMaximumHp,
    float observedCurrentHp) noexcept;

[[nodiscard]] float SelectTransitionCurrentScale(
    bool increase,
    float oldMaximumHp,
    float observedCurrentHp,
    float newMaximumHp) noexcept;

[[nodiscard]] HealthTransitionRatioResult ComputeTransitionRatio(
    double preservedRatio,
    float baselineCurrentHp,
    float baselineScaleHp,
    float observedCurrentHp) noexcept;

[[nodiscard]] float ComputeCorrectedTransitionCurrent(
    double preservedRatio,
    float baselineCurrentHp,
    float baselineScaleHp,
    float observedCurrentHp,
    float observedMaximumHp) noexcept;


[[nodiscard]] bool IsHudLanePresentationLatched(
    std::uint64_t readyTick) noexcept;

[[nodiscard]] bool IsHudLaneSubmissionActive(
    std::uint64_t currentHits,
    std::uint64_t maximumHits,
    std::uint64_t lastCurrentTick,
    std::uint64_t lastMaximumTick,
    std::uint64_t now,
    std::uint64_t maximumAgeMilliseconds,
    std::uint64_t pairSkewMilliseconds,
    std::uint64_t requiredHits) noexcept;

} // namespace hs
