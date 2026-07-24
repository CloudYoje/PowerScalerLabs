#include "hud_health_normalizer.h"

#include "logger.h"
#include "health_transition_model.h"

#include <windows.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <bit>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <limits>
#include <mutex>
#include <sstream>
#include <string>

namespace hs {
namespace {

constexpr std::uintptr_t kCurrentWriterRva = 0x001BD88E;
constexpr std::uintptr_t kMaximumWriterRva = 0x001BD8C4;
constexpr std::size_t kWriterInstructionLength = kHudHealthWriterInstructionLength;
constexpr std::uintptr_t kCurrentLaneOffset = 0x628;
constexpr std::uintptr_t kMaximumLaneOffset = 0x62C;
constexpr std::uintptr_t kLaneStride = 0x1A0;
constexpr std::size_t kLaneCount = kHudHealthLaneCount;
constexpr std::uintptr_t kTargetLaneDisplacement = 0x4E0;
constexpr std::uintptr_t kStackMaximumBitsOffsetAtCurrentWriter = 0xB8;
constexpr float kDefaultSingleBarUnits = 10000.0f;
constexpr float kMaximumPlausibleHealth = 1.0e13f;
constexpr DWORD kSignatureWaitTimeoutMs = 60000;
constexpr DWORD kSignaturePollIntervalMs = 50;
constexpr DWORD kDefaultHudTransitionHoldMs = 3500;
constexpr DWORD kHudSubmissionMaximumAgeMs = 500;
constexpr DWORD kHudSubmissionPairSkewMs = 125;
constexpr std::uint64_t kHudSubmissionRequiredHits = 4;
constexpr std::size_t kDiscoveredLaneCapacity = 32;
constexpr std::uintptr_t kUnclaimedDisplacement =
    std::numeric_limits<std::uintptr_t>::max();

constexpr std::array<std::uint8_t, kWriterInstructionLength> kCurrentSignature = {
    0xF3, 0x0F, 0x11, 0x84, 0x1F, 0x28, 0x06, 0x00, 0x00
};
constexpr std::array<std::uint8_t, kWriterInstructionLength> kMaximumSignature = {
    0xF3, 0x0F, 0x11, 0x8C, 0x1F, 0x2C, 0x06, 0x00, 0x00
};

struct Settings {
    bool enabled = true;
    bool normalizeAllHudLanes = true;
    bool normalizePlayer = true;
    bool normalizeTarget = true;
    // Quest HUDs can submit health through additional displacements that are
    // not part of the original five Training/Versus lanes. The exact writer
    // sites are still health-only, so plausible unknown lanes can be normalized
    // statelessly and recorded for diagnostics.
    bool normalizeDiscoveredHudLanes = true;
    float singleBarUnits = kDefaultSingleBarUnits;
    bool preserveRatioDuringMaximumTransitions = true;
    DWORD transitionHoldMs = kDefaultHudTransitionHoldMs;
};

struct HookSite {
    std::uintptr_t rva = 0;
    std::uintptr_t fieldOffset = 0;
    const char* label = nullptr;
    std::array<std::uint8_t, kWriterInstructionLength> signature{};
    std::atomic<bool> patched{false};
    bool signatureMatched = false;
    std::atomic<std::uint64_t> hits{0};
    std::atomic<std::uint64_t> normalizedHits{0};
    std::atomic<std::uint64_t> passthroughHits{0};
    std::atomic<std::uint64_t> writeFailures{0};
    std::array<std::uint8_t, kWriterInstructionLength> lastObservedBytes{};
    bool lastObservedReadable = false;
    DWORD lastObservedProtection = 0;
};

struct LaneSample {
    std::atomic<bool> currentSeen{false};
    std::atomic<bool> maximumSeen{false};
    std::atomic<std::uint32_t> originalCurrentBits{0};
    std::atomic<std::uint32_t> originalMaximumBits{0};
    std::atomic<std::uint32_t> normalizedCurrentBits{0};
    std::atomic<std::uint32_t> normalizedMaximumBits{0};
    std::atomic<std::uintptr_t> lastHudCockpit{0};
    std::atomic<DWORD> lastThreadId{0};

    // Presentation bridge used while a transformation changes maximum HP and
    // the guarded Battle_Mob percentage-preservation correction stabilizes.
    std::atomic<std::uint32_t> lastRealCurrentBits{0};
    std::atomic<std::uint32_t> lastRealMaximumBits{0};
    std::atomic<std::uint32_t> presentedRatioBits{0};
    std::atomic<std::uint32_t> transitionRatioBits{0};
    std::atomic<std::uint32_t> transitionBaselineCurrentBits{0};
    std::atomic<std::uint32_t> transitionBaselineScaleBits{0};
    std::atomic<std::uint32_t> transitionTargetMaximumBits{0};
    std::atomic<std::uint32_t> transitionCompletionRatioBits{0};
    std::atomic<std::uintptr_t> transitionFighterAddress{0};
    std::atomic<DWORD> transitionStartTick{0};
    std::atomic<bool> transitionActive{false};
    std::atomic<bool> transitionExternalOwner{false};
    std::atomic<bool> transitionCompletionPending{false};
    std::atomic<bool> temporaryZeroActive{false};
    std::atomic<std::uint64_t> transitionStarts{0};
    std::atomic<std::uint64_t> transitionBridgeHits{0};
    std::atomic<std::uint64_t> transitionCompletions{0};
    std::atomic<std::uint64_t> transitionTimeouts{0};
    std::atomic<std::uint64_t> transitionCancels{0};

    // Native HUD-presence tracker. The health presentation state is gated on a
    // recent, stable pair of current/max submissions after each battle reset.
    std::atomic<std::uint64_t> currentHitsSinceReset{0};
    std::atomic<std::uint64_t> maximumHitsSinceReset{0};
    std::atomic<DWORD> lastCurrentTick{0};
    std::atomic<DWORD> lastMaximumTick{0};
    std::atomic<DWORD> hudReadyTick{0};
    std::atomic<bool> presentationEligible{false};
};

struct DiscoveredLaneSample {
    std::atomic<std::uintptr_t> displacement{kUnclaimedDisplacement};
    std::atomic<std::uintptr_t> lastHudCockpit{0};
    std::atomic<DWORD> lastThreadId{0};
    std::atomic<std::uint64_t> currentHits{0};
    std::atomic<std::uint64_t> maximumHits{0};
    std::atomic<std::uint64_t> normalizedHits{0};
    std::atomic<std::uint32_t> lastCurrentBits{0};
    std::atomic<std::uint32_t> lastMaximumBits{0};
    std::atomic<std::uint32_t> lastNormalizedCurrentBits{0};
    LaneSample transitionState{};
};

std::array<HookSite, 2> gSites = {{
    {kCurrentWriterRva, kCurrentLaneOffset,
     "current: movss [rdi+rbx+0x628], xmm0", kCurrentSignature},
    {kMaximumWriterRva, kMaximumLaneOffset,
     "maximum: movss [rdi+rbx+0x62C], xmm1", kMaximumSignature}
}};
std::array<LaneSample, kLaneCount> gLaneSamples{};
std::array<DiscoveredLaneSample, kDiscoveredLaneCapacity> gDiscoveredLanes{};

std::atomic<bool> gInitialized{false};
std::atomic<bool> gActive{false};
std::atomic<bool> gShutdownRequested{false};
std::atomic<bool> gInitialReportWritten{false};
std::atomic<std::uint64_t> gReportSequence{0};
std::atomic<std::uint64_t> gUnsupportedLaneHits{0};
std::atomic<std::uint64_t> gDynamicallyNormalizedLaneHits{0};
std::atomic<std::uint64_t> gDiscoveredLaneCapacityMisses{0};
std::atomic<std::uint64_t> gInvalidValueHits{0};
std::atomic<std::uint64_t> gSignatureProbeAttempts{0};
std::atomic<DWORD> gSignatureWaitElapsedMs{0};
std::uintptr_t gGameBase = 0;
HMODULE gLoaderModule = nullptr;
PVOID gVehHandle = nullptr;
Settings gSettings{};
std::mutex gSiteStatusMutex;

bool ProbeSiteSignature(HookSite& site) noexcept;

float BitsToFloat(std::uint32_t bits) noexcept {
    return std::bit_cast<float>(bits);
}

std::uint32_t FloatToBits(float value) noexcept {
    return std::bit_cast<std::uint32_t>(value);
}

bool IsReadableProtection(DWORD protect) noexcept {
    if ((protect & PAGE_GUARD) != 0 || protect == PAGE_NOACCESS) return false;
    const DWORD basic = protect & 0xFF;
    return basic == PAGE_READONLY || basic == PAGE_READWRITE ||
           basic == PAGE_WRITECOPY || basic == PAGE_EXECUTE_READ ||
           basic == PAGE_EXECUTE_READWRITE || basic == PAGE_EXECUTE_WRITECOPY;
}

bool IsWritableProtection(DWORD protect) noexcept {
    if ((protect & PAGE_GUARD) != 0 || protect == PAGE_NOACCESS) return false;
    const DWORD basic = protect & 0xFF;
    return basic == PAGE_READWRITE || basic == PAGE_WRITECOPY ||
           basic == PAGE_EXECUTE_READWRITE || basic == PAGE_EXECUTE_WRITECOPY;
}

bool IsRangeWithProtection(std::uintptr_t address, std::size_t size,
                           bool requireWritable) noexcept {
    if (address == 0 || size == 0 ||
        address > std::numeric_limits<std::uintptr_t>::max() - size) {
        return false;
    }
    MEMORY_BASIC_INFORMATION information{};
    if (VirtualQuery(reinterpret_cast<const void*>(address), &information,
                     sizeof(information)) == 0 ||
        information.State != MEM_COMMIT) {
        return false;
    }
    if (requireWritable ? !IsWritableProtection(information.Protect)
                        : !IsReadableProtection(information.Protect)) {
        return false;
    }
    const auto start = reinterpret_cast<std::uintptr_t>(information.BaseAddress);
    const auto end = start + information.RegionSize;
    return address >= start && address + size <= end;
}

template <typename T>
bool SafeRead(std::uintptr_t address, T& value) noexcept {
    if (!IsRangeWithProtection(address, sizeof(T), false)) return false;
#if defined(_MSC_VER)
    __try {
        std::memcpy(&value, reinterpret_cast<const void*>(address), sizeof(T));
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
#else
    std::memcpy(&value, reinterpret_cast<const void*>(address), sizeof(T));
    return true;
#endif
}

bool SafeReadBytes(std::uintptr_t address, void* destination,
                   std::size_t size) noexcept {
    if (!destination || !IsRangeWithProtection(address, size, false)) return false;
#if defined(_MSC_VER)
    __try {
        std::memcpy(destination, reinterpret_cast<const void*>(address), size);
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
#else
    std::memcpy(destination, reinterpret_cast<const void*>(address), size);
    return true;
#endif
}

bool SafeWrite32(std::uintptr_t address, std::uint32_t value) noexcept {
    if (!IsRangeWithProtection(address, sizeof(value), true)) return false;
#if defined(_MSC_VER)
    __try {
        *reinterpret_cast<volatile std::uint32_t*>(address) = value;
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
#else
    *reinterpret_cast<volatile std::uint32_t*>(address) = value;
    return true;
#endif
}

bool WriteCodeByte(std::uintptr_t address, std::uint8_t value) noexcept {
    DWORD oldProtection = 0;
    if (!VirtualProtect(reinterpret_cast<void*>(address), 1,
                        PAGE_EXECUTE_READWRITE, &oldProtection)) {
        return false;
    }
#if defined(_MSC_VER)
    __try {
        *reinterpret_cast<volatile std::uint8_t*>(address) = value;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        DWORD ignored = 0;
        VirtualProtect(reinterpret_cast<void*>(address), 1,
                       oldProtection, &ignored);
        return false;
    }
#else
    *reinterpret_cast<volatile std::uint8_t*>(address) = value;
#endif
    FlushInstructionCache(GetCurrentProcess(),
                          reinterpret_cast<const void*>(address), 1);
    DWORD ignored = 0;
    VirtualProtect(reinterpret_cast<void*>(address), 1,
                   oldProtection, &ignored);
    return true;
}

std::filesystem::path IniPath() {
    wchar_t path[MAX_PATH]{};
    if (gLoaderModule && GetModuleFileNameW(gLoaderModule, path, MAX_PATH) != 0) {
        return std::filesystem::path(path).parent_path() / L"HealthScale.ini";
    }
    return L"HealthScale.ini";
}

float ReadIniFloat(const std::filesystem::path& path,
                   const wchar_t* section, const wchar_t* key,
                   float fallback) {
    wchar_t fallbackText[64]{};
    swprintf_s(fallbackText, L"%.6f", fallback);
    wchar_t value[128]{};
    const std::wstring widePath = path.wstring();
    GetPrivateProfileStringW(section, key, fallbackText, value,
                             static_cast<DWORD>(std::size(value)),
                             widePath.c_str());
    wchar_t* end = nullptr;
    const float parsed = std::wcstof(value, &end);
    return end != value && std::isfinite(parsed) ? parsed : fallback;
}

Settings LoadSettings() {
    const auto path = IniPath();
    const std::wstring widePath = path.wstring();
    Settings settings{};
    settings.enabled = GetPrivateProfileIntW(
        L"NativeHudNormalization", L"Enabled", 1, widePath.c_str()) != 0;
    settings.normalizeAllHudLanes = GetPrivateProfileIntW(
        L"NativeHudNormalization", L"NormalizeAllHudLanes", 1,
        widePath.c_str()) != 0;
    settings.normalizePlayer = GetPrivateProfileIntW(
        L"NativeHudNormalization", L"NormalizePlayer", 1,
        widePath.c_str()) != 0;
    settings.normalizeTarget = GetPrivateProfileIntW(
        L"NativeHudNormalization", L"NormalizeTarget", 1,
        widePath.c_str()) != 0;
    settings.normalizeDiscoveredHudLanes = GetPrivateProfileIntW(
        L"NativeHudNormalization", L"NormalizeDiscoveredHudLanes", 1,
        widePath.c_str()) != 0;
    settings.singleBarUnits = ReadIniFloat(
        path, L"NativeHudNormalization", L"SingleBarUnits",
        kDefaultSingleBarUnits);
    settings.preserveRatioDuringMaximumTransitions = GetPrivateProfileIntW(
        L"NativeHudNormalization", L"PreserveRatioDuringMaximumTransitions", 1,
        widePath.c_str()) != 0;
    settings.transitionHoldMs = static_cast<DWORD>(std::clamp<UINT>(
        GetPrivateProfileIntW(L"NativeHudNormalization", L"TransitionHoldMilliseconds",
                              kDefaultHudTransitionHoldMs, widePath.c_str()),
        250u, 10000u));
    if (!std::isfinite(settings.singleBarUnits) ||
        settings.singleBarUnits < 1.0f || settings.singleBarUnits > 1000000.0f) {
        settings.singleBarUnits = kDefaultSingleBarUnits;
    }
    return settings;
}

bool DecodeLane(std::uintptr_t displacement, std::size_t& lane) noexcept {
    if (displacement % kLaneStride != 0) return false;
    lane = static_cast<std::size_t>(displacement / kLaneStride);
    return lane < kLaneCount;
}

bool ShouldNormalizeLane(std::size_t lane) noexcept {
    if (gSettings.normalizeAllHudLanes) return lane < kLaneCount;
    if (lane == 0) return gSettings.normalizePlayer;
    if (lane == kTargetLaneDisplacement / kLaneStride) {
        return gSettings.normalizeTarget;
    }
    return false;
}

bool ShouldNormalizeDiscoveredLane() noexcept {
    return gSettings.enabled && gSettings.normalizeAllHudLanes &&
           gSettings.normalizeDiscoveredHudLanes;
}

DiscoveredLaneSample* FindOrClaimDiscoveredLane(
    std::uintptr_t displacement) noexcept {
    for (auto& sample : gDiscoveredLanes) {
        if (sample.displacement.load(std::memory_order_acquire) == displacement) {
            return &sample;
        }
    }
    for (auto& sample : gDiscoveredLanes) {
        std::uintptr_t expected = kUnclaimedDisplacement;
        if (sample.displacement.compare_exchange_strong(
                expected, displacement, std::memory_order_acq_rel)) {
            return &sample;
        }
        if (expected == displacement) return &sample;
    }
    gDiscoveredLaneCapacityMisses.fetch_add(1, std::memory_order_relaxed);
    return nullptr;
}

float NormalizeCurrentStateless(float current, float maximum) noexcept {
    const double ratio = std::clamp(
        static_cast<double>(current) / static_cast<double>(maximum), 0.0, 1.0);
    float result = static_cast<float>(ratio * gSettings.singleBarUnits);
    if (current > 0.0f && result <= 0.0f) {
        result = std::numeric_limits<float>::min();
    }
    return result;
}

void RecordDiscoveredCurrent(
    DiscoveredLaneSample* sample,
    std::uintptr_t cockpit,
    std::uint32_t currentBits,
    std::uint32_t maximumBits,
    std::uint32_t normalizedBits,
    bool normalized) noexcept {
    if (!sample) return;
    sample->lastHudCockpit.store(cockpit, std::memory_order_relaxed);
    sample->lastThreadId.store(GetCurrentThreadId(), std::memory_order_relaxed);
    sample->lastCurrentBits.store(currentBits, std::memory_order_relaxed);
    sample->lastMaximumBits.store(maximumBits, std::memory_order_relaxed);
    sample->lastNormalizedCurrentBits.store(normalizedBits, std::memory_order_relaxed);
    sample->currentHits.fetch_add(1, std::memory_order_relaxed);
    if (normalized) sample->normalizedHits.fetch_add(1, std::memory_order_relaxed);
}

void RecordDiscoveredMaximum(
    DiscoveredLaneSample* sample,
    std::uintptr_t cockpit,
    std::uint32_t maximumBits,
    bool normalized) noexcept {
    if (!sample) return;
    sample->lastHudCockpit.store(cockpit, std::memory_order_relaxed);
    sample->lastThreadId.store(GetCurrentThreadId(), std::memory_order_relaxed);
    sample->lastMaximumBits.store(maximumBits, std::memory_order_relaxed);
    sample->maximumHits.fetch_add(1, std::memory_order_relaxed);
    if (normalized) sample->normalizedHits.fetch_add(1, std::memory_order_relaxed);
}

bool IsPlausibleHealth(float current, float maximum) noexcept {
    return std::isfinite(current) && std::isfinite(maximum) &&
           current >= 0.0f && maximum > 0.0f &&
           current <= kMaximumPlausibleHealth &&
           maximum <= kMaximumPlausibleHealth;
}

bool HealthChanged(float previous, float current) noexcept {
    if (!std::isfinite(previous) || !std::isfinite(current)) return true;
    const double scale = std::max({1.0, std::fabs(static_cast<double>(previous)),
                                   std::fabs(static_cast<double>(current))});
    return std::fabs(static_cast<double>(previous) - static_cast<double>(current)) >
           scale * 1.0e-5;
}

bool RatioNearlyEqual(double left, double right) noexcept {
    return std::fabs(left - right) <= 0.0025;
}

void DeactivateTransition(LaneSample& state, bool completed, bool timedOut,
                          bool cancelled) noexcept {
    state.transitionActive.store(false, std::memory_order_release);
    state.transitionExternalOwner.store(false, std::memory_order_release);
    state.transitionCompletionPending.store(false, std::memory_order_release);
    state.temporaryZeroActive.store(false, std::memory_order_release);
    state.transitionFighterAddress.store(0, std::memory_order_relaxed);
    if (completed) {
        state.transitionCompletions.fetch_add(1, std::memory_order_relaxed);
    }
    if (timedOut) {
        state.transitionTimeouts.fetch_add(1, std::memory_order_relaxed);
    }
    if (cancelled) {
        state.transitionCancels.fetch_add(1, std::memory_order_relaxed);
    }
}

void RefreshHudReadyTick(LaneSample& state, DWORD now) noexcept {
    if (!state.presentationEligible.load(std::memory_order_acquire)) return;
    const bool active = IsHudLaneSubmissionActive(
        state.currentHitsSinceReset.load(std::memory_order_relaxed),
        state.maximumHitsSinceReset.load(std::memory_order_relaxed),
        state.lastCurrentTick.load(std::memory_order_relaxed),
        state.lastMaximumTick.load(std::memory_order_relaxed),
        now,
        kHudSubmissionMaximumAgeMs,
        kHudSubmissionPairSkewMs,
        kHudSubmissionRequiredHits);
    if (active && state.hudReadyTick.load(std::memory_order_relaxed) == 0) {
        state.hudReadyTick.store(now, std::memory_order_release);
    }
}

float NormalizeCurrentWithState(LaneSample& state, float current,
                                float maximum) noexcept {
    if (!IsPlausibleHealth(current, maximum)) return current;

    const DWORD now = GetTickCount();
    const float previousCurrent = BitsToFloat(
        state.lastRealCurrentBits.load(std::memory_order_relaxed));
    const float previousMaximum = BitsToFloat(
        state.lastRealMaximumBits.load(std::memory_order_relaxed));
    const double observedRatio = std::clamp(
        static_cast<double>(current) / static_cast<double>(maximum), 0.0, 1.0);
    double displayRatio = observedRatio;
    bool startedThisSample = false;

    const bool havePrevious = IsPlausibleHealth(previousCurrent, previousMaximum);
    const bool externallyOwned =
        state.transitionExternalOwner.load(std::memory_order_acquire);
    const bool transitionWasActive =
        state.transitionActive.load(std::memory_order_acquire);
    const bool maximumChanged = havePrevious &&
        HealthChanged(previousMaximum, maximum);
    const bool zeroTransitionContext =
        gSettings.preserveRatioDuringMaximumTransitions && havePrevious &&
        (transitionWasActive || maximumChanged);
    const auto frameKind = zeroTransitionContext
        ? ClassifyHealthTransitionFrame(
            previousCurrent,
            state.temporaryZeroActive.load(std::memory_order_acquire),
            current)
        : HealthTransitionFrameKind::Normal;

    if (frameKind == HealthTransitionFrameKind::TemporaryZero) {
        double heldRatio = std::clamp(
            static_cast<double>(previousCurrent) /
                static_cast<double>(previousMaximum),
            0.0, 1.0);
        if (transitionWasActive) {
            heldRatio = std::clamp(
                static_cast<double>(BitsToFloat(
                    state.transitionRatioBits.load(std::memory_order_relaxed))),
                0.0, 1.0);
        } else {
            state.transitionRatioBits.store(
                FloatToBits(static_cast<float>(heldRatio)),
                std::memory_order_relaxed);
            state.transitionBaselineCurrentBits.store(
                FloatToBits(previousCurrent), std::memory_order_relaxed);
            state.transitionBaselineScaleBits.store(
                FloatToBits(previousMaximum), std::memory_order_relaxed);
            state.transitionFighterAddress.store(0, std::memory_order_relaxed);
            state.transitionExternalOwner.store(false, std::memory_order_release);
            state.transitionCompletionPending.store(false,
                                                     std::memory_order_release);
            state.transitionStarts.fetch_add(1, std::memory_order_relaxed);
            state.transitionActive.store(true, std::memory_order_release);
        }
        state.transitionTargetMaximumBits.store(
            FloatToBits(maximum), std::memory_order_relaxed);
        state.transitionStartTick.store(now, std::memory_order_relaxed);
        state.temporaryZeroActive.store(true, std::memory_order_release);
        state.transitionBridgeHits.fetch_add(1, std::memory_order_relaxed);
        state.presentedRatioBits.store(
            FloatToBits(static_cast<float>(heldRatio)),
            std::memory_order_relaxed);
        return static_cast<float>(heldRatio * gSettings.singleBarUnits);
    }

    if (frameKind == HealthTransitionFrameKind::RecoveryBaseline) {
        state.temporaryZeroActive.store(false, std::memory_order_release);
        const double heldRatio = std::clamp(
            static_cast<double>(BitsToFloat(
                state.transitionRatioBits.load(std::memory_order_relaxed))),
            0.0, 1.0);
        const bool netIncrease = maximum > previousMaximum;
        state.transitionBaselineCurrentBits.store(
            FloatToBits(current), std::memory_order_relaxed);
        state.transitionBaselineScaleBits.store(
            FloatToBits(SelectTransitionCurrentScale(
                netIncrease, previousMaximum, current, maximum)),
            std::memory_order_relaxed);
        state.transitionTargetMaximumBits.store(
            FloatToBits(maximum), std::memory_order_relaxed);
        state.transitionStartTick.store(now, std::memory_order_relaxed);
        state.lastRealCurrentBits.store(
            FloatToBits(current), std::memory_order_relaxed);
        state.lastRealMaximumBits.store(
            FloatToBits(maximum), std::memory_order_relaxed);
        state.presentedRatioBits.store(
            FloatToBits(static_cast<float>(heldRatio)),
            std::memory_order_relaxed);
        state.transitionBridgeHits.fetch_add(1, std::memory_order_relaxed);
        return static_cast<float>(heldRatio * gSettings.singleBarUnits);
    }

    if (gSettings.preserveRatioDuringMaximumTransitions && havePrevious &&
        HealthChanged(previousMaximum, maximum) && current > 0.0f &&
        !externallyOwned) {
        const bool alreadyActive =
            state.transitionActive.load(std::memory_order_acquire);
        if (!alreadyActive) {
            const double preservedRatio = std::clamp(
                static_cast<double>(previousCurrent) /
                    static_cast<double>(previousMaximum),
                0.0, 1.0);
            state.transitionRatioBits.store(
                FloatToBits(static_cast<float>(preservedRatio)),
                std::memory_order_relaxed);
            state.transitionStarts.fetch_add(1, std::memory_order_relaxed);
            startedThisSample = true;
        }
        const bool increase = maximum > previousMaximum;
        state.transitionBaselineCurrentBits.store(FloatToBits(current),
                                                  std::memory_order_relaxed);
        state.transitionBaselineScaleBits.store(
            FloatToBits(SelectTransitionCurrentScale(
                increase, previousMaximum, current, maximum)),
            std::memory_order_relaxed);
        state.transitionTargetMaximumBits.store(FloatToBits(maximum),
                                                 std::memory_order_relaxed);
        state.transitionStartTick.store(now, std::memory_order_relaxed);
        state.transitionCompletionPending.store(false, std::memory_order_release);
        state.transitionActive.store(true, std::memory_order_release);
    }

    if (state.transitionActive.load(std::memory_order_acquire)) {
        const bool external =
            state.transitionExternalOwner.load(std::memory_order_acquire);
        const double heldRatio = std::clamp(
            static_cast<double>(BitsToFloat(
                state.transitionRatioBits.load(std::memory_order_relaxed))),
            0.0, 1.0);
        const DWORD start = state.transitionStartTick.load(std::memory_order_relaxed);
        const DWORD elapsed = now - start;

        if (elapsed > gSettings.transitionHoldMs) {
            DeactivateTransition(state, false, true, false);
        } else if (external) {
            const bool completionPending =
                state.transitionCompletionPending.load(std::memory_order_acquire);
            const double completionRatio = std::clamp(
                static_cast<double>(BitsToFloat(
                    state.transitionCompletionRatioBits.load(
                        std::memory_order_relaxed))),
                0.0, 1.0);
            if (completionPending && RatioNearlyEqual(observedRatio, completionRatio)) {
                displayRatio = completionRatio;
                DeactivateTransition(state, true, false, false);
            } else {
                // HealthScale owns this ratio and updates it from Battle_Mob
                // tracking. Do not independently complete from a transient HUD
                // sample before the guarded correction has been read back.
                displayRatio = heldRatio;
                state.transitionBridgeHits.fetch_add(1, std::memory_order_relaxed);
            }
        } else if (!startedThisSample && current > 0.0f &&
                   RatioNearlyEqual(observedRatio, heldRatio)) {
            displayRatio = heldRatio;
            DeactivateTransition(state, true, false, false);
        } else {
            const float baselineCurrent = BitsToFloat(
                state.transitionBaselineCurrentBits.load(std::memory_order_relaxed));
            const float baselineScale = BitsToFloat(
                state.transitionBaselineScaleBits.load(std::memory_order_relaxed));
            displayRatio = ComputeTransitionRatio(
                heldRatio, baselineCurrent, baselineScale, current).ratio;
            state.transitionBridgeHits.fetch_add(1, std::memory_order_relaxed);
        }
    }

    state.lastRealCurrentBits.store(FloatToBits(current), std::memory_order_relaxed);
    state.lastRealMaximumBits.store(FloatToBits(maximum), std::memory_order_relaxed);
    state.presentedRatioBits.store(FloatToBits(static_cast<float>(displayRatio)),
                                   std::memory_order_relaxed);

    float result = static_cast<float>(displayRatio * gSettings.singleBarUnits);
    if (current > 0.0f && result <= 0.0f) {
        result = std::numeric_limits<float>::min();
    }
    return result;
}

float NormalizeCurrentForLane(std::size_t lane, float current,
                              float maximum) noexcept {
    if (lane >= gLaneSamples.size()) return current;
    return NormalizeCurrentWithState(gLaneSamples[lane], current, maximum);
}

void RecordCurrentSample(std::size_t lane, std::uintptr_t cockpit,
                         std::uint32_t originalCurrent,
                         std::uint32_t originalMaximum,
                         std::uint32_t normalizedCurrent) noexcept {
    if (lane >= gLaneSamples.size()) return;
    auto& sample = gLaneSamples[lane];
    const DWORD now = GetTickCount();
    sample.originalCurrentBits.store(originalCurrent, std::memory_order_relaxed);
    sample.originalMaximumBits.store(originalMaximum, std::memory_order_relaxed);
    sample.normalizedCurrentBits.store(normalizedCurrent, std::memory_order_relaxed);
    sample.lastHudCockpit.store(cockpit, std::memory_order_relaxed);
    sample.lastThreadId.store(GetCurrentThreadId(), std::memory_order_relaxed);
    sample.lastCurrentTick.store(now, std::memory_order_relaxed);
    sample.currentHitsSinceReset.fetch_add(1, std::memory_order_relaxed);
    sample.currentSeen.store(true, std::memory_order_release);
    RefreshHudReadyTick(sample, now);
}

void RecordMaximumSample(std::size_t lane, std::uintptr_t cockpit,
                         std::uint32_t originalMaximum,
                         std::uint32_t normalizedMaximum) noexcept {
    if (lane >= gLaneSamples.size()) return;
    auto& sample = gLaneSamples[lane];
    const DWORD now = GetTickCount();
    sample.originalMaximumBits.store(originalMaximum, std::memory_order_relaxed);
    sample.normalizedMaximumBits.store(normalizedMaximum, std::memory_order_relaxed);
    sample.lastHudCockpit.store(cockpit, std::memory_order_relaxed);
    sample.lastThreadId.store(GetCurrentThreadId(), std::memory_order_relaxed);
    sample.lastMaximumTick.store(now, std::memory_order_relaxed);
    sample.maximumHitsSinceReset.fetch_add(1, std::memory_order_relaxed);
    sample.maximumSeen.store(true, std::memory_order_release);
    RefreshHudReadyTick(sample, now);
}

LONG CALLBACK BreakpointHandler(PEXCEPTION_POINTERS pointers) noexcept {
    if (!pointers || !pointers->ExceptionRecord || !pointers->ContextRecord ||
        pointers->ExceptionRecord->ExceptionCode != EXCEPTION_BREAKPOINT ||
        !gActive.load(std::memory_order_acquire)) {
        return EXCEPTION_CONTINUE_SEARCH;
    }

    const auto exceptionAddress = reinterpret_cast<std::uintptr_t>(
        pointers->ExceptionRecord->ExceptionAddress);
    std::size_t siteIndex = gSites.size();
    for (std::size_t index = 0; index < gSites.size(); ++index) {
        if (gSites[index].patched.load(std::memory_order_acquire) &&
            exceptionAddress == gGameBase + gSites[index].rva) {
            siteIndex = index;
            break;
        }
    }
    if (siteIndex >= gSites.size()) return EXCEPTION_CONTINUE_SEARCH;

    auto& site = gSites[siteIndex];
    site.hits.fetch_add(1, std::memory_order_relaxed);
    CONTEXT& context = *pointers->ContextRecord;
    const std::uintptr_t cockpit = static_cast<std::uintptr_t>(context.Rbx);
    const std::uintptr_t laneDisplacement = static_cast<std::uintptr_t>(context.Rdi);
    const std::uintptr_t destination = cockpit + laneDisplacement + site.fieldOffset;

    std::size_t lane = 0;
    const bool supportedLane = DecodeLane(laneDisplacement, lane);
    if (!supportedLane) {
        gUnsupportedLaneHits.fetch_add(1, std::memory_order_relaxed);
    }

    std::uint32_t originalBits = 0;
    std::uint32_t replacementBits = 0;
    bool normalized = false;

    if (siteIndex == 0) {
        originalBits = static_cast<std::uint32_t>(context.Xmm0.Low & 0xFFFFFFFFull);
        replacementBits = originalBits;
        std::uint32_t maximumBits = 0;
        const bool haveMaximum = SafeRead(
            static_cast<std::uintptr_t>(context.Rsp) +
                kStackMaximumBitsOffsetAtCurrentWriter,
            maximumBits);
        const float current = BitsToFloat(originalBits);
        const float maximum = BitsToFloat(maximumBits);
        const bool plausible = haveMaximum && IsPlausibleHealth(current, maximum);
        const bool normalizeKnown = gSettings.enabled && supportedLane &&
            ShouldNormalizeLane(lane) && plausible;
        const bool normalizeDiscovered = !supportedLane &&
            ShouldNormalizeDiscoveredLane() && plausible;
        if (normalizeKnown) {
            replacementBits = FloatToBits(NormalizeCurrentForLane(lane, current, maximum));
            normalized = true;
            RecordCurrentSample(lane, cockpit, originalBits, maximumBits,
                                replacementBits);
        } else if (normalizeDiscovered) {
            auto* discovered = FindOrClaimDiscoveredLane(laneDisplacement);
            replacementBits = FloatToBits(discovered
                ? NormalizeCurrentWithState(
                    discovered->transitionState, current, maximum)
                : NormalizeCurrentStateless(current, maximum));
            normalized = true;
            gDynamicallyNormalizedLaneHits.fetch_add(1, std::memory_order_relaxed);
            RecordDiscoveredCurrent(
                discovered, cockpit, originalBits, maximumBits,
                replacementBits, true);
        } else if (supportedLane && haveMaximum) {
            RecordCurrentSample(lane, cockpit, originalBits, maximumBits,
                                replacementBits);
            if (!plausible) {
                gInvalidValueHits.fetch_add(1, std::memory_order_relaxed);
            }
        } else if (!supportedLane && haveMaximum) {
            RecordDiscoveredCurrent(
                FindOrClaimDiscoveredLane(laneDisplacement),
                cockpit, originalBits, maximumBits,
                replacementBits, false);
            if (!plausible) {
                gInvalidValueHits.fetch_add(1, std::memory_order_relaxed);
            }
        }
    } else {
        originalBits = static_cast<std::uint32_t>(context.Xmm1.Low & 0xFFFFFFFFull);
        replacementBits = originalBits;
        const float maximum = BitsToFloat(originalBits);
        const bool plausibleMaximum = std::isfinite(maximum) && maximum > 0.0f &&
            maximum <= kMaximumPlausibleHealth;
        const bool normalizeKnown = gSettings.enabled && supportedLane &&
            ShouldNormalizeLane(lane) && plausibleMaximum;
        const bool normalizeDiscovered = !supportedLane &&
            ShouldNormalizeDiscoveredLane() && plausibleMaximum;
        if (normalizeKnown) {
            replacementBits = FloatToBits(gSettings.singleBarUnits);
            normalized = true;
            RecordMaximumSample(lane, cockpit, originalBits, replacementBits);
        } else if (normalizeDiscovered) {
            replacementBits = FloatToBits(gSettings.singleBarUnits);
            normalized = true;
            gDynamicallyNormalizedLaneHits.fetch_add(1, std::memory_order_relaxed);
            RecordDiscoveredMaximum(
                FindOrClaimDiscoveredLane(laneDisplacement),
                cockpit, originalBits, true);
        } else if (supportedLane) {
            RecordMaximumSample(lane, cockpit, originalBits, replacementBits);
            if (!plausibleMaximum) {
                gInvalidValueHits.fetch_add(1, std::memory_order_relaxed);
            }
        } else {
            RecordDiscoveredMaximum(
                FindOrClaimDiscoveredLane(laneDisplacement),
                cockpit, originalBits, false);
            if (!plausibleMaximum) {
                gInvalidValueHits.fetch_add(1, std::memory_order_relaxed);
            }
        }
    }

    const bool writeSucceeded = SafeWrite32(destination, replacementBits);
    if (!writeSucceeded) {
        site.writeFailures.fetch_add(1, std::memory_order_relaxed);
    }
    if (normalized) {
        site.normalizedHits.fetch_add(1, std::memory_order_relaxed);
    } else {
        site.passthroughHits.fetch_add(1, std::memory_order_relaxed);
    }

    // Emulate the displaced 9-byte MOVSS store and continue after it. The
    // original register state is intentionally left unchanged.
    context.Rip = exceptionAddress + kWriterInstructionLength;
    return EXCEPTION_CONTINUE_EXECUTION;
}

bool PatchSite(HookSite& site) noexcept {
    const std::uintptr_t address = gGameBase + site.rva;
    if (!ProbeSiteSignature(site)) {
        return false;
    }
    if (!WriteCodeByte(address, 0xCC)) return false;
    site.patched.store(true, std::memory_order_release);
    return true;
}

void RestoreSite(HookSite& site) noexcept {
    if (!site.patched.exchange(false, std::memory_order_acq_rel) ||
        gGameBase == 0) {
        return;
    }
    (void)WriteCodeByte(gGameBase + site.rva, site.signature[0]);
}


std::string HexBytes(const std::array<std::uint8_t, kWriterInstructionLength>& bytes) {
    std::ostringstream stream;
    stream << std::uppercase << std::hex << std::setfill('0');
    for (std::size_t index = 0; index < bytes.size(); ++index) {
        if (index != 0) stream << ' ';
        stream << std::setw(2) << static_cast<unsigned int>(bytes[index]);
    }
    return stream.str();
}

bool ProbeSiteSignature(HookSite& site) noexcept {
    const std::uintptr_t address = gGameBase + site.rva;
    MEMORY_BASIC_INFORMATION information{};
    DWORD observedProtection = 0;
    if (VirtualQuery(reinterpret_cast<const void*>(address), &information,
                     sizeof(information)) != 0) {
        observedProtection = information.Protect;
    }

    std::array<std::uint8_t, kWriterInstructionLength> bytes{};
    const bool readable = SafeReadBytes(address, bytes.data(), bytes.size());
    const bool matched = readable && bytes == site.signature;

    {
        std::scoped_lock lock(gSiteStatusMutex);
        site.signatureMatched = matched;
        site.lastObservedReadable = readable;
        site.lastObservedProtection = observedProtection;
        site.lastObservedBytes = readable
            ? bytes
            : std::array<std::uint8_t, kWriterInstructionLength>{};
    }
    return matched;
}

bool WaitForFinalWriterSignatures() noexcept {
    DWORD elapsed = 0;
    while (!gShutdownRequested.load(std::memory_order_acquire) &&
           elapsed <= kSignatureWaitTimeoutMs) {
        gSignatureProbeAttempts.fetch_add(1, std::memory_order_relaxed);
        const bool currentMatched = ProbeSiteSignature(gSites[0]);
        const bool maximumMatched = ProbeSiteSignature(gSites[1]);
        gSignatureWaitElapsedMs.store(elapsed, std::memory_order_relaxed);
        if (currentMatched && maximumMatched) {
            return true;
        }
        if (elapsed == kSignatureWaitTimeoutMs) break;
        Sleep(kSignaturePollIntervalMs);
        elapsed = std::min<DWORD>(kSignatureWaitTimeoutMs,
                                  elapsed + kSignaturePollIntervalMs);
    }
    return false;
}

std::filesystem::path ReportDirectory() {
    wchar_t executable[MAX_PATH]{};
    GetModuleFileNameW(nullptr, executable, MAX_PATH);
    return std::filesystem::path(executable).parent_path() /
           L"HealthScaleScanner_Reports";
}

std::wstring Timestamp() {
    SYSTEMTIME time{};
    GetLocalTime(&time);
    wchar_t result[64]{};
    swprintf_s(result, L"%04u%02u%02u_%02u%02u%02u",
               time.wYear, time.wMonth, time.wDay,
               time.wHour, time.wMinute, time.wSecond);
    return result;
}

std::string HexPointer(std::uintptr_t value) {
    std::ostringstream stream;
    stream << "0x" << std::uppercase << std::hex << std::setfill('0')
           << std::setw(sizeof(std::uintptr_t) * 2) << value;
    return stream.str();
}

void WriteReport(const char* reason, bool initialOnly) {
    if (initialOnly) {
        bool expected = false;
        if (!gInitialReportWritten.compare_exchange_strong(
                expected, true, std::memory_order_acq_rel)) {
            return;
        }
    }
    const auto sequence = gReportSequence.fetch_add(1, std::memory_order_relaxed) + 1;

    std::error_code error;
    const auto directory = ReportDirectory();
    std::filesystem::create_directories(directory, error);
    const auto path = directory /
        (L"HealthScaleNormalization_" + Timestamp() + L"_" +
         std::to_wstring(sequence) + L".txt");
    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    if (!output) {
        Log(L"NATIVE HUD NORMALIZATION ERROR: could not create report %ls",
            path.c_str());
        return;
    }

    output << "XV2 HEALTHSCALE OVERHAUL - NATIVE SINGLE-BAR NORMALIZATION\n"
           << "============================================================\n"
           << "Purpose: normalize HudCockpit health values while preserving percentage\n"
           << "through transformation-driven maximum-HP changes.\n\n"
           << "STATUS\n------------------------------------------------------------\n"
           << "active: " << (gActive.load() ? "YES" : "NO") << '\n'
           << "report reason: " << reason << '\n'
           << "game base: " << HexPointer(gGameBase) << '\n'
           << "single-bar units: " << std::fixed << std::setprecision(3)
           << gSettings.singleBarUnits << '\n'
           << "normalize fixed five HUD lanes: "
           << (gSettings.normalizeAllHudLanes ? "YES" : "NO") << '\n'
           << "normalize player: " << (gSettings.normalizePlayer ? "YES" : "NO") << '\n'
           << "normalize target: " << (gSettings.normalizeTarget ? "YES" : "NO") << '\n'
           << "normalize discovered quest HUD lanes: "
           << (gSettings.normalizeDiscoveredHudLanes ? "YES" : "NO") << '\n'
           << "signature probe attempts: " << gSignatureProbeAttempts.load() << '\n'
           << "signature wait elapsed ms: " << gSignatureWaitElapsedMs.load() << '\n'
           << "HUD transition bridge: "
           << (gSettings.preserveRatioDuringMaximumTransitions ? "ENABLED" : "DISABLED") << '\n'
           << "HUD transition hold ms: " << gSettings.transitionHoldMs << "\n\n";

    output << "HOOK SITES\n------------------------------------------------------------\n";
    for (const auto& site : gSites) {
        output << site.label << '\n'
               << "  RVA: 0x" << std::uppercase << std::hex << std::setfill('0')
               << std::setw(8) << site.rva << std::dec << '\n'
               << "  signature matched: " << (site.signatureMatched ? "YES" : "NO") << '\n'
               << "  last observed readable: " << (site.lastObservedReadable ? "YES" : "NO") << '\n'
               << "  last observed protection: 0x" << std::uppercase << std::hex
               << site.lastObservedProtection << std::dec << '\n'
               << "  expected bytes: " << HexBytes(site.signature) << '\n'
               << "  observed bytes: " << HexBytes(site.lastObservedBytes) << '\n'
               << "  patched: " << (site.patched.load() ? "YES" : "NO") << '\n'
               << "  hits: " << site.hits.load() << '\n'
               << "  normalized hits: " << site.normalizedHits.load() << '\n'
               << "  pass-through hits: " << site.passthroughHits.load() << '\n'
               << "  write failures: " << site.writeFailures.load() << '\n';
    }
    output << "unsupported lane hits: " << gUnsupportedLaneHits.load() << '\n'
           << "dynamically normalized unsupported-lane hits: "
           << gDynamicallyNormalizedLaneHits.load() << '\n'
           << "discovered-lane registry capacity misses: "
           << gDiscoveredLaneCapacityMisses.load() << '\n'
           << "invalid value hits: " << gInvalidValueHits.load() << "\n\n";

    output << "LANE SAMPLES\n------------------------------------------------------------\n";
    for (std::size_t lane = 0; lane < gLaneSamples.size(); ++lane) {
        const auto& sample = gLaneSamples[lane];
        output << "lane=" << lane
               << " displacement=0x" << std::uppercase << std::hex
               << lane * kLaneStride << std::dec
               << " current-seen=" << (sample.currentSeen.load() ? "YES" : "NO")
               << " maximum-seen=" << (sample.maximumSeen.load() ? "YES" : "NO")
               << " HudCockpit=" << HexPointer(sample.lastHudCockpit.load())
               << " thread=" << sample.lastThreadId.load() << '\n';
        if (sample.currentSeen.load()) {
            output << "  original current="
                   << BitsToFloat(sample.originalCurrentBits.load())
                   << " original maximum="
                   << BitsToFloat(sample.originalMaximumBits.load())
                   << " normalized current="
                   << BitsToFloat(sample.normalizedCurrentBits.load()) << '\n';
        }
        if (sample.maximumSeen.load()) {
            output << "  normalized maximum="
                   << BitsToFloat(sample.normalizedMaximumBits.load()) << '\n';
        }
        output << "  transition-active="
               << (sample.transitionActive.load() ? "YES" : "NO")
               << " starts=" << sample.transitionStarts.load()
               << " bridge-hits=" << sample.transitionBridgeHits.load()
               << " completions=" << sample.transitionCompletions.load()
               << " timeouts=" << sample.transitionTimeouts.load()
               << " cancels=" << sample.transitionCancels.load() << '\n'
               << "  hud-current-hits-since-reset="
               << sample.currentHitsSinceReset.load()
               << " hud-maximum-hits-since-reset="
               << sample.maximumHitsSinceReset.load()
               << " last-current-tick=" << sample.lastCurrentTick.load()
               << " last-maximum-tick=" << sample.lastMaximumTick.load()
               << " hud-ready-tick=" << sample.hudReadyTick.load()
               << " hud-presentation-eligible="
               << (sample.presentationEligible.load() ? "YES" : "NO")
               << " hud-presentation-latched="
               << (IsHudLanePresentationLatched(sample.hudReadyTick.load())
                       ? "YES" : "NO")
               << " hud-recent-submissions="
               << (IsHudLaneSubmissionActive(
                       sample.currentHitsSinceReset.load(),
                       sample.maximumHitsSinceReset.load(),
                       sample.lastCurrentTick.load(),
                       sample.lastMaximumTick.load(),
                       GetTickCount(),
                       kHudSubmissionMaximumAgeMs,
                       kHudSubmissionPairSkewMs,
                       kHudSubmissionRequiredHits)
                       ? "YES" : "NO")
               << '\n';
    }

    output << "\nDISCOVERED QUEST/ADDITIONAL HUD LANES\n"
           << "------------------------------------------------------------\n";
    bool wroteDiscovered = false;
    for (const auto& sample : gDiscoveredLanes) {
        const auto displacement = sample.displacement.load(std::memory_order_acquire);
        if (displacement == kUnclaimedDisplacement) continue;
        wroteDiscovered = true;
        output << "displacement=0x" << std::uppercase << std::hex
               << displacement << std::dec
               << " current-hits=" << sample.currentHits.load()
               << " maximum-hits=" << sample.maximumHits.load()
               << " normalized-hits=" << sample.normalizedHits.load()
               << " HudCockpit=" << HexPointer(sample.lastHudCockpit.load())
               << " thread=" << sample.lastThreadId.load() << '\n'
               << "  last current=" << BitsToFloat(sample.lastCurrentBits.load())
               << " last maximum=" << BitsToFloat(sample.lastMaximumBits.load())
               << " normalized current="
               << BitsToFloat(sample.lastNormalizedCurrentBits.load()) << '\n'
               << "  transition-active="
               << (sample.transitionState.transitionActive.load() ? "YES" : "NO")
               << " starts=" << sample.transitionState.transitionStarts.load()
               << " bridge-hits=" << sample.transitionState.transitionBridgeHits.load()
               << " completions=" << sample.transitionState.transitionCompletions.load()
               << " timeouts=" << sample.transitionState.transitionTimeouts.load()
               << " cancels=" << sample.transitionState.transitionCancels.load()
               << '\n';
    }
    if (!wroteDiscovered) output << "none observed\n";

    output << "\nSAFETY BOUNDARY\n------------------------------------------------------------\n"
           << "The native HUD hook writes only HudCockpit presentation fields. The separate\n"
           << "automatic transformation correction may write Battle_Mob+0x100 only after\n"
           << "a verified maximum-HP change stabilizes; it never writes maximum HP. A zero\n"
           << "real HP is never revived.\n\n"
           << "VISUAL TEST\n------------------------------------------------------------\n"
           << "At full HP, each enabled native bar should appear as one full layer.\n"
           << "After damage, the bar should shrink by current/maximum percentage\n"
           << "without exposing additional absolute-HP layers.\n\n"
           << "END OF HEALTHSCALE OVERHAUL NORMALIZATION REPORT\n";
    output.close();
    Log(L"HealthScale Overhaul native HUD normalization report written: %ls", path.c_str());
}

DWORD WINAPI ReportThread(void*) {
    DWORD elapsed = 0;
    bool sawUsefulSample = false;
    while (!gShutdownRequested.load(std::memory_order_acquire) && elapsed < 600000) {
        for (const auto& sample : gLaneSamples) {
            if (sample.currentSeen.load(std::memory_order_acquire) &&
                sample.maximumSeen.load(std::memory_order_acquire)) {
                sawUsefulSample = true;
                break;
            }
        }
        if (sawUsefulSample) break;
        Sleep(100);
        elapsed += 100;
    }
    if (sawUsefulSample && !gShutdownRequested.load(std::memory_order_acquire)) {
        Sleep(1500);
        WriteReport("first native HUD current/maximum samples captured", true);
    } else if (!gShutdownRequested.load(std::memory_order_acquire)) {
        WriteReport("10-minute hook observation timeout", true);
    }
    return 0;
}

} // namespace

bool InitializeNativeHudHealthNormalizer(HMODULE loaderModule) {
    bool expected = false;
    if (!gInitialized.compare_exchange_strong(expected, true,
                                               std::memory_order_acq_rel)) {
        return gActive.load(std::memory_order_acquire);
    }

    gLoaderModule = loaderModule;
    gGameBase = reinterpret_cast<std::uintptr_t>(GetModuleHandleW(nullptr));
    gSettings = LoadSettings();
    gShutdownRequested.store(false, std::memory_order_release);

    if (!gSettings.enabled) {
        Log(L"HealthScale Overhaul native HUD normalization is disabled in HealthScale.ini.");
        return false;
    }
    if (gGameBase == 0) {
        Log(L"HealthScale Overhaul ERROR: DBXV2.exe base is unavailable.");
        return false;
    }

    Log(L"HealthScale Overhaul waiting for the finalized DBXV2 writer signatures before installing either hook.");
    if (!WaitForFinalWriterSignatures()) {
        Log(L"HealthScale Overhaul ERROR: finalized writer signatures did not appear within %lu ms; no hook was installed.",
            static_cast<unsigned long>(kSignatureWaitTimeoutMs));
        WriteReport("timed out waiting for finalized writer signatures", false);
        return false;
    }

    Log(L"HealthScale Overhaul finalized writer signatures matched after %lu ms and %llu probes.",
        static_cast<unsigned long>(gSignatureWaitElapsedMs.load()),
        static_cast<unsigned long long>(gSignatureProbeAttempts.load()));

    gVehHandle = AddVectoredExceptionHandler(1, BreakpointHandler);
    if (!gVehHandle) {
        Log(L"HealthScale Overhaul ERROR: AddVectoredExceptionHandler failed.");
        WriteReport("vectored exception handler installation failed", false);
        return false;
    }

    // Enable the handler before publishing the first INT3 byte so a game
    // thread can never observe a patched site while the VEH is refusing it.
    gActive.store(true, std::memory_order_release);
    if (!PatchSite(gSites[0]) || !PatchSite(gSites[1])) {
        RestoreSite(gSites[0]);
        RestoreSite(gSites[1]);
        gActive.store(false, std::memory_order_release);
        RemoveVectoredExceptionHandler(gVehHandle);
        gVehHandle = nullptr;
        Log(L"HealthScale Overhaul ERROR: writer bytes changed during final installation; no hook remains installed.");
        WriteReport("writer bytes changed during final installation", false);
        return false;
    }

    Log(L"HealthScale Overhaul native single-bar normalization ACTIVE: current writer RVA=0x%08llX, maximum writer RVA=0x%08llX, units=%.3f, all-lanes=%ls.",
        static_cast<unsigned long long>(kCurrentWriterRva),
        static_cast<unsigned long long>(kMaximumWriterRva),
        gSettings.singleBarUnits,
        gSettings.normalizeAllHudLanes ? L"yes" : L"no");
    Log(L"Only HudCockpit submission values are changed. Battle_Mob HP remains untouched.");

    HANDLE reportThread = CreateThread(nullptr, 0, ReportThread, nullptr, 0, nullptr);
    if (reportThread) CloseHandle(reportThread);
    return true;
}

void ShutdownNativeHudHealthNormalizer() noexcept {
    gShutdownRequested.store(true, std::memory_order_release);
    // Keep the handler active until every INT3 byte has been restored.
    RestoreSite(gSites[0]);
    RestoreSite(gSites[1]);
    gActive.store(false, std::memory_order_release);
    if (gVehHandle) {
        RemoveVectoredExceptionHandler(gVehHandle);
        gVehHandle = nullptr;
    }
}

bool IsNativeHudHealthNormalizerActive() noexcept {
    return gActive.load(std::memory_order_acquire);
}

HudHealthPresentationTrackerStatus
SnapshotHudHealthPresentationTrackerStatus() noexcept {
    HudHealthPresentationTrackerStatus status;
    const DWORD now = GetTickCount();
    for (std::size_t lane = 0; lane < gLaneSamples.size(); ++lane) {
        const auto& source = gLaneSamples[lane];
        auto& destination = status.lanes[lane];
        destination.currentSeen = source.currentSeen.load(std::memory_order_acquire);
        destination.maximumSeen = source.maximumSeen.load(std::memory_order_acquire);
        destination.currentHitsSinceReset =
            source.currentHitsSinceReset.load(std::memory_order_relaxed);
        destination.maximumHitsSinceReset =
            source.maximumHitsSinceReset.load(std::memory_order_relaxed);
        destination.lastCurrentTick = source.lastCurrentTick.load(std::memory_order_relaxed);
        destination.lastMaximumTick = source.lastMaximumTick.load(std::memory_order_relaxed);
        destination.readyTick = source.hudReadyTick.load(std::memory_order_relaxed);
        destination.lastWriterThreadId =
            source.lastThreadId.load(std::memory_order_relaxed);
        destination.transitionActive =
            source.transitionActive.load(std::memory_order_acquire);
        destination.presentedRatio = BitsToFloat(
            source.presentedRatioBits.load(std::memory_order_relaxed));
        destination.submissionActive = IsHudLaneSubmissionActive(
            destination.currentHitsSinceReset,
            destination.maximumHitsSinceReset,
            destination.lastCurrentTick,
            destination.lastMaximumTick,
            now,
            kHudSubmissionMaximumAgeMs,
            kHudSubmissionPairSkewMs,
            kHudSubmissionRequiredHits);
        destination.presentationEligible =
            source.presentationEligible.load(std::memory_order_acquire);
        // Recent submissions prove the lane initially. Once readyTick is set,
        // native-HUD presence is latched for the battle lifecycle. This keeps
        // health presentation visibility tied to HUD tracking without flickering when the
        // game briefly pauses the writer instructions.
        destination.presentationLatched = destination.presentationEligible &&
            IsHudLanePresentationLatched(destination.readyTick);
    }
    return status;
}

void BeginHudHealthTransitionBridge(
    std::size_t lane,
    std::uintptr_t fighterAddress,
    float heldRatio,
    float baselineCurrentHp,
    float baselineScaleHp,
    float targetMaximumHp) noexcept {
    if (lane >= gLaneSamples.size() || fighterAddress == 0 ||
        !std::isfinite(heldRatio) || !std::isfinite(baselineCurrentHp) ||
        !std::isfinite(baselineScaleHp) || baselineScaleHp <= 0.0f ||
        !std::isfinite(targetMaximumHp) || targetMaximumHp <= 0.0f) {
        return;
    }
    auto& state = gLaneSamples[lane];
    state.transitionRatioBits.store(
        FloatToBits(std::clamp(heldRatio, 0.0f, 1.0f)),
        std::memory_order_relaxed);
    state.transitionBaselineCurrentBits.store(
        FloatToBits(baselineCurrentHp), std::memory_order_relaxed);
    state.transitionBaselineScaleBits.store(
        FloatToBits(baselineScaleHp), std::memory_order_relaxed);
    state.transitionTargetMaximumBits.store(
        FloatToBits(targetMaximumHp), std::memory_order_relaxed);
    state.transitionFighterAddress.store(fighterAddress, std::memory_order_relaxed);
    state.transitionCompletionPending.store(false, std::memory_order_release);
    state.transitionExternalOwner.store(true, std::memory_order_release);
    state.transitionStartTick.store(GetTickCount(), std::memory_order_relaxed);
    state.transitionStarts.fetch_add(1, std::memory_order_relaxed);
    state.transitionActive.store(true, std::memory_order_release);
}

void UpdateHudHealthTransitionBridge(
    std::size_t lane,
    std::uintptr_t fighterAddress,
    float heldRatio,
    float targetMaximumHp) noexcept {
    if (lane >= gLaneSamples.size()) return;
    auto& state = gLaneSamples[lane];
    if (!state.transitionActive.load(std::memory_order_acquire) ||
        !state.transitionExternalOwner.load(std::memory_order_acquire) ||
        state.transitionFighterAddress.load(std::memory_order_relaxed) !=
            fighterAddress) {
        return;
    }
    state.transitionRatioBits.store(
        FloatToBits(std::clamp(heldRatio, 0.0f, 1.0f)),
        std::memory_order_relaxed);
    if (std::isfinite(targetMaximumHp) && targetMaximumHp > 0.0f) {
        state.transitionTargetMaximumBits.store(
            FloatToBits(targetMaximumHp), std::memory_order_relaxed);
    }
    state.transitionStartTick.store(GetTickCount(), std::memory_order_relaxed);
}

void CompleteHudHealthTransitionBridge(
    std::size_t lane,
    std::uintptr_t fighterAddress,
    float verifiedCurrentHp,
    float verifiedMaximumHp) noexcept {
    if (lane >= gLaneSamples.size() || !std::isfinite(verifiedCurrentHp) ||
        !std::isfinite(verifiedMaximumHp) || verifiedMaximumHp <= 0.0f) {
        return;
    }
    auto& state = gLaneSamples[lane];
    if (!state.transitionActive.load(std::memory_order_acquire) ||
        !state.transitionExternalOwner.load(std::memory_order_acquire) ||
        state.transitionFighterAddress.load(std::memory_order_relaxed) !=
            fighterAddress) {
        return;
    }
    const float ratio = std::clamp(
        verifiedCurrentHp / verifiedMaximumHp, 0.0f, 1.0f);
    state.transitionRatioBits.store(FloatToBits(ratio), std::memory_order_relaxed);
    state.transitionCompletionRatioBits.store(
        FloatToBits(ratio), std::memory_order_relaxed);
    state.transitionTargetMaximumBits.store(
        FloatToBits(verifiedMaximumHp), std::memory_order_relaxed);
    state.transitionCompletionPending.store(true, std::memory_order_release);
    state.transitionStartTick.store(GetTickCount(), std::memory_order_relaxed);
}

void CancelHudHealthTransitionBridge(
    std::size_t lane,
    std::uintptr_t fighterAddress) noexcept {
    if (lane >= gLaneSamples.size()) return;
    auto& state = gLaneSamples[lane];
    const auto owner = state.transitionFighterAddress.load(std::memory_order_relaxed);
    if (fighterAddress != 0 && owner != 0 && owner != fighterAddress) return;
    if (state.transitionActive.load(std::memory_order_acquire)) {
        DeactivateTransition(state, false, false, true);
    }
}

void ResetHudHealthPresentationLane(std::size_t lane) noexcept {
    if (lane >= gLaneSamples.size()) return;
    auto& state = gLaneSamples[lane];
    state.currentHitsSinceReset.store(0, std::memory_order_relaxed);
    state.maximumHitsSinceReset.store(0, std::memory_order_relaxed);
    state.lastCurrentTick.store(0, std::memory_order_relaxed);
    state.lastMaximumTick.store(0, std::memory_order_relaxed);
    state.hudReadyTick.store(0, std::memory_order_relaxed);
    state.temporaryZeroActive.store(false, std::memory_order_release);
    state.currentSeen.store(false, std::memory_order_release);
    state.maximumSeen.store(false, std::memory_order_release);
    if (state.transitionActive.load(std::memory_order_acquire)) {
        DeactivateTransition(state, false, false, true);
    }
}

void SetHudHealthPresentationLaneEligible(
    std::size_t lane,
    bool eligible) noexcept {
    if (lane >= gLaneSamples.size()) return;
    auto& state = gLaneSamples[lane];
    const bool previous = state.presentationEligible.exchange(
        eligible, std::memory_order_acq_rel);
    if (previous == eligible) return;
    // Any eligibility edge is a new ownership epoch. Clear all writer proof
    // before accepting new submissions so stale traffic cannot relatch a lane.
    ResetHudHealthPresentationLane(lane);
}

void ResetHudHealthPresentationTracker() noexcept {
    for (std::size_t lane = 0; lane < gLaneSamples.size(); ++lane) {
        gLaneSamples[lane].presentationEligible.store(
            false, std::memory_order_release);
        ResetHudHealthPresentationLane(lane);
    }
}

void WriteHudHealthNormalizerReportSnapshot(const char* reason) noexcept {
    WriteReport(reason ? reason : "requested HUD tracker snapshot", false);
}

HudHealthNormalizerStatus SnapshotHudHealthNormalizerStatus() noexcept {
    HudHealthNormalizerStatus status;
    status.initialized = gInitialized.load(std::memory_order_acquire);
    status.active = gActive.load(std::memory_order_acquire);
    status.signatureProbeAttempts = gSignatureProbeAttempts.load(std::memory_order_relaxed);
    status.signatureWaitElapsedMs = gSignatureWaitElapsedMs.load(std::memory_order_relaxed);
    status.unsupportedLaneHits = gUnsupportedLaneHits.load(std::memory_order_relaxed);
    status.dynamicallyNormalizedLaneHits =
        gDynamicallyNormalizedLaneHits.load(std::memory_order_relaxed);
    status.discoveredLaneCapacityMisses =
        gDiscoveredLaneCapacityMisses.load(std::memory_order_relaxed);
    status.invalidValueHits = gInvalidValueHits.load(std::memory_order_relaxed);

    std::scoped_lock lock(gSiteStatusMutex);
    for (std::size_t index = 0; index < gSites.size(); ++index) {
        const auto& site = gSites[index];
        auto& output = status.sites[index];
        output.hookId = index == 0
            ? "health.hud.current-writer"
            : "health.hud.maximum-writer";
        output.rva = site.rva;
        output.expectedBytes = site.signature;
        output.observedBytes = site.lastObservedBytes;
        output.signatureMatched = site.signatureMatched;
        output.observedReadable = site.lastObservedReadable;
        output.observedProtection = site.lastObservedProtection;
        output.installed = site.patched.load(std::memory_order_acquire);
        output.hits = site.hits.load(std::memory_order_relaxed);
        output.normalizedHits = site.normalizedHits.load(std::memory_order_relaxed);
        output.passthroughHits = site.passthroughHits.load(std::memory_order_relaxed);
        output.writeFailures = site.writeFailures.load(std::memory_order_relaxed);
    }
    return status;
}

} // namespace hs
