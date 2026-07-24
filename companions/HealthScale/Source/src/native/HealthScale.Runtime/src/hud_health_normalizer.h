#pragma once

#include <windows.h>

#include <array>
#include <cstdint>
#include <string>

namespace hs {

inline constexpr std::size_t kHudHealthWriterInstructionLength = 9;
inline constexpr std::size_t kPlayerHudHealthLane = 0;
inline constexpr std::size_t kTargetHudHealthLane = 3;
inline constexpr std::size_t kHudHealthLaneCount = 5;

struct HudHealthHookSiteStatus {
    std::string hookId;
    std::uintptr_t rva = 0;
    std::array<std::uint8_t, kHudHealthWriterInstructionLength> expectedBytes{};
    std::array<std::uint8_t, kHudHealthWriterInstructionLength> observedBytes{};
    bool signatureMatched = false;
    bool observedReadable = false;
    DWORD observedProtection = 0;
    bool installed = false;
    std::uint64_t hits = 0;
    std::uint64_t normalizedHits = 0;
    std::uint64_t passthroughHits = 0;
    std::uint64_t writeFailures = 0;
};

struct HudHealthLaneTrackerStatus {
    bool currentSeen = false;
    bool maximumSeen = false;
    // True only while paired current/max writer submissions are still recent.
    // This is diagnostic evidence, not the health presentation visibility authority.
    bool submissionActive = false;
    // True only while HealthScale owns a live HUD epoch for
    // this lane. Native writer traffic cannot earn a presentation latch while
    // the lane is ineligible (for example, target cleared or between targets).
    bool presentationEligible = false;
    // Set after repeated paired native writer submissions prove that the lane's
    // native health HUD exists. Cleared only by the explicit battle-lifecycle
    // tracker reset.
    bool presentationLatched = false;
    bool transitionActive = false;
    std::uint64_t currentHitsSinceReset = 0;
    std::uint64_t maximumHitsSinceReset = 0;
    std::uint64_t lastCurrentTick = 0;
    std::uint64_t lastMaximumTick = 0;
    std::uint64_t readyTick = 0;
    std::uint32_t lastWriterThreadId = 0;
    float presentedRatio = 0.0f;
};

struct HudHealthPresentationTrackerStatus {
    std::array<HudHealthLaneTrackerStatus, kHudHealthLaneCount> lanes{};
};

struct HudHealthNormalizerStatus {
    bool initialized = false;
    bool active = false;
    std::uint64_t signatureProbeAttempts = 0;
    DWORD signatureWaitElapsedMs = 0;
    std::uint64_t unsupportedLaneHits = 0;
    std::uint64_t dynamicallyNormalizedLaneHits = 0;
    std::uint64_t discoveredLaneCapacityMisses = 0;
    std::uint64_t invalidValueHits = 0;
    std::array<HudHealthHookSiteStatus, 2> sites{};
};

// Installs two signature-checked breakpoint-emulation hooks at the confirmed
// HudCockpit current/max submission stores in DBXV2 1.25.02.0. The HUD mirror
// is normalized. The separate diagnostics worker may correct current HP after
// a verified maximum-HP transformation, but this hook never touches Battle_Mob.
bool InitializeNativeHudHealthNormalizer(HMODULE loaderModule);

// Restores the original first bytes and removes the VEH. The proxy is not
// normally unloaded during play, but the function keeps teardown explicit.
void ShutdownNativeHudHealthNormalizer() noexcept;

bool IsNativeHudHealthNormalizerActive() noexcept;
[[nodiscard]] HudHealthNormalizerStatus SnapshotHudHealthNormalizerStatus() noexcept;
[[nodiscard]] HudHealthPresentationTrackerStatus
SnapshotHudHealthPresentationTrackerStatus() noexcept;

// The fighter-health tracker owns transformation bridge lifetime. The HUD hook
// consumes the ratio and does not infer completion ahead of guarded readback.
void BeginHudHealthTransitionBridge(
    std::size_t lane,
    std::uintptr_t fighterAddress,
    float heldRatio,
    float baselineCurrentHp,
    float baselineScaleHp,
    float targetMaximumHp) noexcept;
void UpdateHudHealthTransitionBridge(
    std::size_t lane,
    std::uintptr_t fighterAddress,
    float heldRatio,
    float targetMaximumHp) noexcept;
void CompleteHudHealthTransitionBridge(
    std::size_t lane,
    std::uintptr_t fighterAddress,
    float verifiedCurrentHp,
    float verifiedMaximumHp) noexcept;
void CancelHudHealthTransitionBridge(
    std::size_t lane,
    std::uintptr_t fighterAddress) noexcept;
// Clears one native HUD lane when that lane's ownership changes (for
// example, target acquired/changed/cleared). The lane must earn a fresh
// presentation latch from paired native current/max writer submissions.
void ResetHudHealthPresentationLane(std::size_t lane) noexcept;

// Controls whether a lane is allowed to earn a fresh native-HUD presence
// latch. Changing eligibility clears all prior proof for that lane so writer
// submissions from an old ownership epoch cannot leak into the next one.
void SetHudHealthPresentationLaneEligible(
    std::size_t lane,
    bool eligible) noexcept;

// Clears all lanes for a new or quiesced battle lifecycle.
void ResetHudHealthPresentationTracker() noexcept;
void WriteHudHealthNormalizerReportSnapshot(const char* reason) noexcept;

} // namespace hs
