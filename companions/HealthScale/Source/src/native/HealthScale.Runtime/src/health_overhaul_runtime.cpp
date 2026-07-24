#include "health_overhaul_runtime.h"
#include "logger.h"
#include "hud_health_normalizer.h"
#include "health_transition_model.h"

#include <array>
#include <atomic>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <limits>
#include <string>
#include <utility>
#include <cwctype>

namespace {
std::atomic<bool> gHealthRuntimeRunning{false};
std::atomic<bool> gHealthWritesEnabled{false};
// Verified against the user's XV2 Patcher 4.64 xinput1_3.dll.
constexpr std::uintptr_t kPatcherBattleCoreStorageRva = 0x2080C8;
constexpr DWORD kExpectedPatcherImageSize = 0x394000;

// Verified in the user's DBXV2 1.25.02.0 / XV2 Patcher 4.64 setup.
constexpr std::size_t kMobArrayOffset = 0x3A58;
constexpr std::size_t kCurrentHpOffset = 0x100;
constexpr std::size_t kMaximumHpOffset = 0x104;
constexpr std::size_t kMobSlotCount = 14;
constexpr std::size_t kInvalidHudSlot = kMobSlotCount;
constexpr std::size_t kPrimaryPlayerTargetPointerOffset = 0x2000;
constexpr std::size_t kSecondaryPlayerTargetPointerOffset = 0x2138;

constexpr float kMinimumPlausibleMaximumHp = 1.0f;
constexpr DWORD kPollIntervalMs = 50;
constexpr DWORD kCurrentHpLogIntervalMs = 750;
constexpr DWORD kHeartbeatIntervalMs = 10000;
constexpr int kRequiredStableSamples = 4;
constexpr DWORD kDefaultTargetStableSamples = 2;
constexpr DWORD kDefaultTargetReleaseSamples = 2;
constexpr DWORD kDefaultCoreStableSamples = 4;
constexpr DWORD kDefaultBattleReadySamples = 4;
constexpr DWORD kDefaultPlayerLossSamples = 1;
constexpr DWORD kDefaultTransitionCooldownMs = 1250;

struct Settings {
    bool writeHealth = true;
    bool correctMaximumIncreases = true;
    bool correctMaximumDecreases = true;
    bool preserveTransitionDelta = true;
    DWORD increaseStabilizationMs = 350;
    DWORD decreaseStabilizationMs = 650;
    DWORD maximumPendingMs = 3000;

    bool autoTarget = true;
    float minimumTargetMaximumHp = 1.0f;
    DWORD targetStableSamples = kDefaultTargetStableSamples;
    DWORD targetReleaseSamples = kDefaultTargetReleaseSamples;
    DWORD coreStableSamples = kDefaultCoreStableSamples;
    DWORD battleReadySamples = kDefaultBattleReadySamples;
    DWORD playerLossSamples = kDefaultPlayerLossSamples;
    DWORD transitionCooldownMs = kDefaultTransitionCooldownMs;
    std::size_t playerSlot = 0;
};

struct PendingCorrection {
    bool active = false;
    std::uintptr_t mob = 0;
    float oldCurrentHp = 0.0f;
    float oldMaximumHp = 0.0f;
    float targetMaximumHp = 0.0f;
    float transitionBaselineCurrentHp = 0.0f;
    float transitionBaselineScaleHp = 1.0f;
    double preservedRatio = 0.0;
    double transitionBaselineRatio = 0.0;
    double trackedRatio = 0.0;
    DWORD detectedTick = 0;
    DWORD lastMaximumChangeTick = 0;
    int stableSamples = 0;
    bool increase = false;
    bool sawTemporaryZero = false;
    std::size_t hudLane = hs::kHudHealthLaneCount;
    float canonicalMaximumHp = 0.0f;
    float lastObservedCurrentHp = 0.0f;
    bool domainMaximumJustChanged = false;
    hs::HealthScaleValueDomain sourceDomain =
        hs::HealthScaleValueDomain::Invalid;
};

struct HealthScaleDomainLease {
    bool active = false;
    std::uintptr_t mob = 0;
    float canonicalMaximumHp = 0.0f;
    float targetMaximumHp = 0.0f;
    float lastObservedCurrentHp = 0.0f;
    double trackedRatio = 0.0;
    bool canonicalSourceActive = false;
    float canonicalBaselineCurrentHp = 0.0f;
    double canonicalBaselineRatio = 0.0;
    std::uint64_t relapseCorrections = 0;
    DWORD lastCorrectionTick = 0;
};

struct SlotState {
    std::uintptr_t mob = 0;
    float currentHp = 0.0f;
    float maximumHp = 0.0f;
    DWORD lastCurrentLogTick = 0;
    bool valid = false;
    PendingCorrection pending{};
    HealthScaleDomainLease domainLease{};
};

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
    if (address == 0 || size == 0) return false;
    if (address > std::numeric_limits<std::uintptr_t>::max() - size) return false;

    MEMORY_BASIC_INFORMATION mbi{};
    if (VirtualQuery(reinterpret_cast<const void*>(address), &mbi, sizeof(mbi)) == 0) {
        return false;
    }
    if (mbi.State != MEM_COMMIT) return false;
    if (requireWritable ? !IsWritableProtection(mbi.Protect)
                        : !IsReadableProtection(mbi.Protect)) {
        return false;
    }

    const auto regionStart = reinterpret_cast<std::uintptr_t>(mbi.BaseAddress);
    const auto regionEnd = regionStart + mbi.RegionSize;
    return address >= regionStart && address + size <= regionEnd;
}

bool IsReadableRange(std::uintptr_t address, std::size_t size) noexcept {
    return IsRangeWithProtection(address, size, false);
}

bool IsWritableRange(std::uintptr_t address, std::size_t size) noexcept {
    return IsRangeWithProtection(address, size, true);
}

template <typename T>
bool SafeRead(std::uintptr_t address, T& value) noexcept {
    if (!IsReadableRange(address, sizeof(T))) return false;
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

template <typename T>
bool SafeWrite(std::uintptr_t address, const T& value) noexcept {
    if (!IsWritableRange(address, sizeof(T))) return false;
#if defined(_MSC_VER)
    __try {
        std::memcpy(reinterpret_cast<void*>(address), &value, sizeof(T));
        return true;
    }
    __except (EXCEPTION_EXECUTE_HANDLER) {
        return false;
    }
#else
    std::memcpy(reinterpret_cast<void*>(address), &value, sizeof(T));
    return true;
#endif
}

DWORD ReadImageSize(HMODULE module) noexcept;

bool IsAddressInside(std::uintptr_t address, std::uintptr_t base,
                     DWORD size) noexcept {
    return base != 0 && size != 0 && address >= base &&
           address < base + static_cast<std::uintptr_t>(size);
}

bool IsGameImageAddress(std::uintptr_t address) noexcept {
    const HMODULE game = GetModuleHandleW(nullptr);
    return IsAddressInside(address,
        reinterpret_cast<std::uintptr_t>(game), ReadImageSize(game));
}

bool IsPrivateWritableObject(std::uintptr_t address) noexcept {
    if (address < 0x10000 || IsGameImageAddress(address)) return false;
    MEMORY_BASIC_INFORMATION information{};
    if (VirtualQuery(reinterpret_cast<const void*>(address), &information,
                     sizeof(information)) == 0) {
        return false;
    }
    return information.State == MEM_COMMIT && information.Type == MEM_PRIVATE &&
           IsWritableProtection(information.Protect);
}

bool IsGameVtable(std::uintptr_t vtable) noexcept {
    return IsGameImageAddress(vtable);
}

bool IsFiniteHealth(float value) noexcept {
    return std::isfinite(value);
}

bool ReadMobHealth(std::uintptr_t mob, float& currentHp, float& maximumHp) noexcept {
    if (mob == 0 || !IsPrivateWritableObject(mob)) return false;

    std::uintptr_t vtable = 0;
    if (!SafeRead(mob, vtable) || !IsGameVtable(vtable)) {
        return false;
    }

    if (!SafeRead(mob + kCurrentHpOffset, currentHp) ||
        !SafeRead(mob + kMaximumHpOffset, maximumHp)) {
        return false;
    }

    if (!IsFiniteHealth(currentHp) || !IsFiniteHealth(maximumHp)) return false;
    if (maximumHp < kMinimumPlausibleMaximumHp) return false;
    // Transformation magnitude is not a validity criterion. A mixed-domain
    // frame can have an arbitrarily large finite current/max ratio. Safety is
    // provided by Battle_Mob ownership, vtable/range validation, and finite
    // health fields; the transition state machine decides when a pair is
    // coherent enough to update the preserved percentage.
    return hs::IsPlausibleHealthPair(currentHp, maximumHp);
}

int ScoreCoreCandidate(std::uintptr_t core) noexcept {
    // A genuine BattleCore is a writable heap object. The qword at the first
    // pointer is normally its DBXV2.exe vtable and must never be promoted as a
    // second BattleCore candidate.
    if (!IsPrivateWritableObject(core) ||
        !IsReadableRange(core + kMobArrayOffset,
                         kMobSlotCount * sizeof(std::uintptr_t)) ||
        !IsReadableRange(core + 0x4A88, sizeof(std::uintptr_t))) {
        return -1000;
    }

    std::uintptr_t coreVtable = 0;
    if (!SafeRead(core, coreVtable) || !IsGameVtable(coreVtable)) {
        return -950;
    }

    int score = 0;
    for (std::size_t slot = 0; slot < kMobSlotCount; ++slot) {
        std::uintptr_t mob = 0;
        if (!SafeRead(core + kMobArrayOffset + slot * sizeof(std::uintptr_t), mob)) {
            return -1000;
        }
        if (mob == 0) continue;
        if (!IsPrivateWritableObject(mob)) return -900;

        float currentHp = 0.0f;
        float maximumHp = 0.0f;
        if (!ReadMobHealth(mob, currentHp, maximumHp)) return -800;
        score += 10;
    }
    return score;
}

DWORD ReadImageSize(HMODULE module) noexcept {
    if (!module) return 0;
    const auto base = reinterpret_cast<std::uintptr_t>(module);

    IMAGE_DOS_HEADER dos{};
    if (!SafeRead(base, dos) || dos.e_magic != IMAGE_DOS_SIGNATURE) return 0;

    IMAGE_NT_HEADERS64 nt{};
    if (!SafeRead(base + static_cast<std::uintptr_t>(dos.e_lfanew), nt) ||
        nt.Signature != IMAGE_NT_SIGNATURE) {
        return 0;
    }
    return nt.OptionalHeader.SizeOfImage;
}

struct CoreResolution {
    std::uintptr_t storageAddress = 0;
    std::uintptr_t firstPointer = 0;
    std::uintptr_t secondPointer = 0;
    int firstScore = -1000;
    int secondScore = -1000;
    int storageScore = -1000;
    std::uintptr_t selectedCore = 0;
    int selectedScore = -1000;
};

CoreResolution ResolveBattleCore(HMODULE patcher) noexcept {
    CoreResolution result{};
    if (!patcher) return result;

    const auto patcherBase = reinterpret_cast<std::uintptr_t>(patcher);
    if (!SafeRead(patcherBase + kPatcherBattleCoreStorageRva,
                  result.storageAddress) || result.storageAddress == 0) {
        return result;
    }

    SafeRead(result.storageAddress, result.firstPointer);
    if (result.firstPointer != 0) SafeRead(result.firstPointer, result.secondPointer);

    result.firstScore = ScoreCoreCandidate(result.firstPointer);
    result.secondScore = ScoreCoreCandidate(result.secondPointer);
    result.storageScore = ScoreCoreCandidate(result.storageAddress);

    // Prefer the singleton's first pointer whenever it passes the heap identity
    // gate. Only use a fallback candidate if it independently passes the same
    // test. A DBXV2.exe vtable will score -1000 because it is MEM_IMAGE, not a
    // writable MEM_PRIVATE object.
    if (result.firstScore >= 0) {
        result.selectedCore = result.firstPointer;
        result.selectedScore = result.firstScore;
    } else if (result.secondScore >= 0) {
        result.selectedCore = result.secondPointer;
        result.selectedScore = result.secondScore;
    } else if (result.storageScore >= 0) {
        result.selectedCore = result.storageAddress;
        result.selectedScore = result.storageScore;
    }

    return result;
}

bool Changed(float previous, float current) noexcept {
    const float tolerance = std::max(1.0f, std::fabs(previous) * 0.000001f);
    return std::fabs(current - previous) > tolerance;
}

bool NearlyEqualRatio(double a, double b, double tolerance = 0.002) noexcept {
    return std::fabs(a - b) <= tolerance;
}

void LogSlot(std::size_t slot, std::uintptr_t mob, float currentHp,
             float maximumHp, const wchar_t* reason) {
    const double percentage = maximumHp > 0.0f
        ? (static_cast<double>(currentHp) / static_cast<double>(maximumHp)) * 100.0
        : 0.0;
    hs::Log(L"%ls slot=%zu mob=0x%p current=%.3f max=%.3f health=%.6f%%",
            reason, slot, reinterpret_cast<void*>(mob), currentHp, maximumHp,
            percentage);
}

bool IniBool(const std::filesystem::path& path, const wchar_t* key,
             bool fallback) {
    wchar_t buffer[32]{};
    const wchar_t* fallbackText = fallback ? L"1" : L"0";
    const DWORD length = GetPrivateProfileStringW(
        L"HealthScale", key, fallbackText, buffer,
        static_cast<DWORD>(std::size(buffer)), path.c_str());

    std::wstring value(buffer, length);
    value.erase(value.begin(), std::find_if(value.begin(), value.end(),
        [](wchar_t ch) { return std::iswspace(ch) == 0; }));
    value.erase(std::find_if(value.rbegin(), value.rend(),
        [](wchar_t ch) { return std::iswspace(ch) == 0; }).base(), value.end());
    std::transform(value.begin(), value.end(), value.begin(),
        [](wchar_t ch) { return static_cast<wchar_t>(std::towlower(ch)); });

    if (value == L"1" || value == L"true" || value == L"yes" || value == L"on")
        return true;
    if (value == L"0" || value == L"false" || value == L"no" || value == L"off")
        return false;

    hs::Log(L"WARNING: Invalid boolean in HealthScale.ini: %ls=%ls; using %ls",
            key, value.c_str(), fallback ? L"true" : L"false");
    return fallback;
}

DWORD IniDword(const std::filesystem::path& path, const wchar_t* key,
               DWORD fallback, DWORD minimum, DWORD maximum) {
    const UINT value = GetPrivateProfileIntW(L"HealthScale", key, fallback,
                                              path.c_str());
    return std::clamp<DWORD>(value, minimum, maximum);
}

Settings LoadSettings() {
    const auto path = std::filesystem::path(hs::ModuleDirectory()) / L"HealthScale.ini";
    Settings settings{};
    settings.writeHealth = IniBool(path, L"WriteHealth", true);
    settings.correctMaximumIncreases = IniBool(path, L"CorrectMaximumIncreases", true);
    settings.correctMaximumDecreases = IniBool(path, L"CorrectMaximumDecreases", true);
    settings.preserveTransitionDelta = IniBool(path, L"PreserveTransitionDelta", true);
    settings.increaseStabilizationMs = IniDword(
        path, L"StabilizationMilliseconds", 350, 100, 2000);
    settings.decreaseStabilizationMs = IniDword(
        path, L"DecreaseStabilizationMilliseconds", 650, 200, 3000);
    settings.maximumPendingMs = IniDword(
        path, L"MaximumPendingMilliseconds", 3000, 750, 10000);

    settings.autoTarget = IniBool(path, L"HudAutoTarget", true);
    settings.minimumTargetMaximumHp = static_cast<float>(IniDword(
        path, L"HudMinimumTargetMaximumHp", 1, 1, 1000000));
    settings.targetStableSamples = IniDword(
        path, L"HudTargetStableSamples", kDefaultTargetStableSamples, 1, 20);
    settings.targetReleaseSamples = IniDword(
        path, L"HudTargetReleaseSamples", kDefaultTargetReleaseSamples, 1, 20);
    settings.coreStableSamples = IniDword(
        path, L"CoreStableSamples", kDefaultCoreStableSamples, 2, 20);
    settings.battleReadySamples = IniDword(
        path, L"BattleReadySamples", kDefaultBattleReadySamples, 2, 20);
    settings.playerLossSamples = IniDword(
        path, L"PlayerLossSamples", kDefaultPlayerLossSamples, 1, 10);
    settings.transitionCooldownMs = IniDword(
        path, L"QuestTransitionCooldownMilliseconds",
        kDefaultTransitionCooldownMs, 250, 5000);
    settings.playerSlot = static_cast<std::size_t>(IniDword(
        path, L"HudPlayerSlot", 0, 0, static_cast<DWORD>(kMobSlotCount - 1)));
    return settings;
}

std::string NarrowRuntimeReason(const wchar_t* value) {
    if (!value || !*value) return {};
    const int required = WideCharToMultiByte(
        CP_UTF8, 0, value, -1, nullptr, 0, nullptr, nullptr);
    if (required <= 1) return {};
    std::string result(static_cast<std::size_t>(required), '\0');
    WideCharToMultiByte(
        CP_UTF8, 0, value, -1, result.data(), required, nullptr, nullptr);
    if (!result.empty() && result.back() == '\0') result.pop_back();
    return result;
}

void PublishHealthTransition(
    const SlotState& state,
    std::size_t slot,
    const char* phase,
    float observedCurrentHp,
    float observedMaximumHp,
    const std::string& reason) {
    hs::Log(
        L"HEALTH TRANSITION slot=%zu mob=0x%p phase=%hs "
        L"observed-current=%.3f observed-max=%.3f reason=%hs",
        slot, reinterpret_cast<void*>(state.mob),
        phase ? phase : "unknown", observedCurrentHp, observedMaximumHp,
        reason.c_str());
}

void CancelPending(SlotState& state, std::size_t slot, const wchar_t* reason) {
    if (!state.pending.active) return;
    PublishHealthTransition(
        state, slot, "cancelled", state.currentHp, state.maximumHp,
        NarrowRuntimeReason(reason));
    if (state.pending.hudLane < hs::kHudHealthLaneCount) {
        hs::CancelHudHealthTransitionBridge(
            state.pending.hudLane, state.pending.mob);
    }
    hs::Log(L"CORRECTION CANCELLED slot=%zu mob=0x%p reason=%ls",
            slot, reinterpret_cast<void*>(state.pending.mob), reason);
    state.pending = {};
    state.domainLease = {};
}

void QueueCorrection(SlotState& state, std::size_t slot, float newCurrentHp,
                     float newMaximumHp, DWORD now, bool increase,
                     std::size_t hudLane) {
    const bool continuingLease = state.domainLease.active &&
        state.domainLease.mob == state.mob &&
        state.domainLease.canonicalMaximumHp > 0.0f;
    const float canonicalMaximumHp = continuingLease
        ? state.domainLease.canonicalMaximumHp
        : state.maximumHp;
    // Every distinct maximum-health change starts from the fighter's live
    // percentage immediately before that change. A domain lease exists only
    // to remap late canonical-scale writes while a maximum is already scaled;
    // its tracked ratio is not authoritative for a later transformation. In
    // particular, the lease can stop receiving absolute target-domain updates
    // once current HP numerically overlaps the canonical range. Reusing it here
    // would resurrect an older, higher percentage and heal the fighter.
    const double livePreChangeRatio = hs::ComputeLiveHealthRatio(
        state.currentHp, state.maximumHp);
    const double priorLeaseRatio = continuingLease
        ? std::clamp(state.domainLease.trackedRatio, 0.0, 1.0)
        : livePreChangeRatio;
    const double ratio = livePreChangeRatio;

    state.pending.active = true;
    state.pending.mob = state.mob;
    state.pending.oldCurrentHp = state.currentHp;
    state.pending.oldMaximumHp = state.maximumHp;
    state.pending.targetMaximumHp = newMaximumHp;
    // Use the first current-HP sample after the maximum changes as the
    // transition baseline. On decreases, Xenoverse may clamp current HP to the
    // new maximum; treating that clamp as damage would incorrectly drive the
    // correction toward zero.
    const bool initialTemporaryZero =
        hs::ClassifyHealthTransitionFrame(
            state.currentHp, false, newCurrentHp) ==
        hs::HealthTransitionFrameKind::TemporaryZero;
    state.pending.transitionBaselineCurrentHp = initialTemporaryZero
        ? state.currentHp
        : newCurrentHp;
    state.pending.transitionBaselineScaleHp = initialTemporaryZero
        ? state.maximumHp
        : hs::SelectTransitionCurrentScale(
            increase, state.maximumHp, newCurrentHp, newMaximumHp);
    state.pending.preservedRatio = ratio;
    state.pending.trackedRatio = ratio;
    // If current HP is still expressed on the previous maximum's scale, carry
    // only its delta into the held percentage. A zero frame is never a delta:
    // it freezes the last valid ratio until a nonzero recovery baseline arrives.
    if (!initialTemporaryZero && NearlyEqualRatio(
            state.pending.transitionBaselineScaleHp, state.maximumHp, 0.001) &&
        state.maximumHp > 0.0f) {
        state.pending.trackedRatio = hs::ComputeTransitionRatio(
            ratio, state.currentHp, state.maximumHp, newCurrentHp).ratio;
    }
    state.pending.transitionBaselineRatio = state.pending.trackedRatio;
    state.pending.sourceDomain = initialTemporaryZero
        ? hs::HealthScaleValueDomain::Invalid
        : (NearlyEqualRatio(state.pending.transitionBaselineScaleHp, newMaximumHp, 0.001)
            ? hs::HealthScaleValueDomain::TargetScale
            : hs::HealthScaleValueDomain::CanonicalScale);
    state.pending.detectedTick = now;
    state.pending.lastMaximumChangeTick = now;
    state.pending.stableSamples = 0;
    state.pending.increase = increase;
    state.pending.sawTemporaryZero = initialTemporaryZero;
    state.pending.hudLane = hudLane;
    state.pending.canonicalMaximumHp = canonicalMaximumHp;
    state.pending.lastObservedCurrentHp = state.currentHp;
    state.pending.domainMaximumJustChanged = true;

    if (hudLane < hs::kHudHealthLaneCount) {
        hs::BeginHudHealthTransitionBridge(
            hudLane,
            state.mob,
            static_cast<float>(ratio),
            state.pending.transitionBaselineCurrentHp,
            state.pending.transitionBaselineScaleHp,
            newMaximumHp);
    }

    PublishHealthTransition(
        state, slot, "started", newCurrentHp, newMaximumHp,
        increase ? "maximum-health increase detected"
                 : "maximum-health decrease detected");

    hs::Log(L"CORRECTION QUEUED slot=%zu mob=0x%p direction=%ls "
            L"old-current=%.3f old-max=%.3f observed-current=%.3f "
            L"new-max=%.3f transition-baseline=%.3f baseline-scale=%.3f "
            L"preserved-ratio=%.9f ratio-source=live-pre-change "
            L"prior-lease-ratio=%.9f canonical-max=%.3f hud-lane=%zu",
            slot, reinterpret_cast<void*>(state.mob),
            increase ? L"increase" : L"decrease", state.currentHp,
            state.maximumHp, newCurrentHp, newMaximumHp,
            state.pending.transitionBaselineCurrentHp,
            state.pending.transitionBaselineScaleHp, ratio, priorLeaseRatio,
            state.pending.canonicalMaximumHp, hudLane);
}

bool HasDistinctHealthScale(float canonicalMaximumHp,
                            float targetMaximumHp) noexcept {
    if (!std::isfinite(canonicalMaximumHp) || canonicalMaximumHp <= 0.0f ||
        !std::isfinite(targetMaximumHp) || targetMaximumHp <= 0.0f) {
        return false;
    }
    const double factor = static_cast<double>(targetMaximumHp) /
        static_cast<double>(canonicalMaximumHp);
    return factor >= 1.5 || factor <= (1.0 / 1.5);
}

void CommitDomainLease(
    SlotState& state,
    const PendingCorrection& pending,
    float settledCurrentHp,
    float settledMaximumHp,
    DWORD now) noexcept {
    if (!HasDistinctHealthScale(
            pending.canonicalMaximumHp, settledMaximumHp) ||
        settledCurrentHp <= 0.0f) {
        state.domainLease = {};
        return;
    }
    state.domainLease.active = true;
    state.domainLease.mob = state.mob;
    state.domainLease.canonicalMaximumHp = pending.canonicalMaximumHp;
    state.domainLease.targetMaximumHp = settledMaximumHp;
    state.domainLease.lastObservedCurrentHp = settledCurrentHp;
    state.domainLease.trackedRatio = std::clamp(
        pending.trackedRatio, 0.0, 1.0);
    state.domainLease.canonicalSourceActive = false;
    state.domainLease.canonicalBaselineCurrentHp = 0.0f;
    state.domainLease.canonicalBaselineRatio = state.domainLease.trackedRatio;
    state.domainLease.lastCorrectionTick = now;
}

bool ApplyPendingCorrection(SlotState& state, std::size_t slot,
                            float observedCurrentHp, float observedMaximumHp,
                            DWORD now, bool writesEnabled,
                            const Settings& settings,
                            std::uintptr_t battleCore) {
    auto& pending = state.pending;
    if (!pending.active) return false;

    if (state.mob != pending.mob) {
        CancelPending(state, slot, L"fighter pointer changed");
        return false;
    }

    if (now - pending.lastMaximumChangeTick > settings.maximumPendingMs) {
        CancelPending(state, slot, L"stabilization timeout after final maximum-health change");
        return false;
    }

    const auto frameKind = hs::ClassifyHealthTransitionFrame(
        pending.oldCurrentHp, pending.sawTemporaryZero, observedCurrentHp);

    // Zero-current-HP frames must be rejected before they can alter target,
    // baseline, domain, last-observed, or stabilization state. Keep only the
    // latest maximum as the pending target and freeze the last valid ratio.
    if (frameKind == hs::HealthTransitionFrameKind::TemporaryZero) {
        if (Changed(pending.targetMaximumHp, observedMaximumHp)) {
            const float previousTargetMaximumHp = pending.targetMaximumHp;
            pending.targetMaximumHp = observedMaximumHp;
            pending.increase = observedMaximumHp > previousTargetMaximumHp;
            pending.lastMaximumChangeTick = now;
            pending.stableSamples = 0;
        }
        if (!pending.sawTemporaryZero) {
            hs::Log(L"CORRECTION WAITING slot=%zu mob=0x%p reason=temporary-zero "
                    L"direction=%ls",
                    slot, reinterpret_cast<void*>(state.mob),
                    pending.increase ? L"increase" : L"decrease");
            PublishHealthTransition(
                state, slot, "temporary-zero", observedCurrentHp, observedMaximumHp,
                "temporary zero observed during HealthScale stabilization");
        }
        pending.sawTemporaryZero = true;
        if (pending.hudLane < hs::kHudHealthLaneCount) {
            hs::UpdateHudHealthTransitionBridge(
                pending.hudLane,
                pending.mob,
                static_cast<float>(pending.trackedRatio),
                observedMaximumHp);
        }
        return false;
    }

    // The first nonzero sample after one or more zero frames is a baseline,
    // never healing or damage. Resume with the exact ratio held before zero.
    if (frameKind == hs::HealthTransitionFrameKind::RecoveryBaseline) {
        if (Changed(pending.targetMaximumHp, observedMaximumHp)) {
            const float previousTargetMaximumHp = pending.targetMaximumHp;
            pending.targetMaximumHp = observedMaximumHp;
            pending.increase = observedMaximumHp > previousTargetMaximumHp;
            pending.lastMaximumChangeTick = now;
        }
        hs::Log(L"CORRECTION RESUMED slot=%zu mob=0x%p current=%.3f",
                slot, reinterpret_cast<void*>(state.mob), observedCurrentHp);
        pending.sawTemporaryZero = false;
        pending.transitionBaselineRatio = pending.trackedRatio;
        pending.transitionBaselineCurrentHp = observedCurrentHp;
        const bool netIncrease = observedMaximumHp > pending.oldMaximumHp;
        pending.transitionBaselineScaleHp = hs::SelectTransitionCurrentScale(
            netIncrease,
            pending.oldMaximumHp,
            observedCurrentHp,
            observedMaximumHp);
        pending.lastObservedCurrentHp = observedCurrentHp;
        pending.domainMaximumJustChanged = true;
        pending.sourceDomain = NearlyEqualRatio(
            pending.transitionBaselineScaleHp, observedMaximumHp, 0.001)
                ? hs::HealthScaleValueDomain::TargetScale
                : hs::HealthScaleValueDomain::CanonicalScale;
        pending.stableSamples = 0;
        if (pending.hudLane < hs::kHudHealthLaneCount) {
            hs::UpdateHudHealthTransitionBridge(
                pending.hudLane,
                pending.mob,
                static_cast<float>(pending.trackedRatio),
                observedMaximumHp);
        }
        PublishHealthTransition(
            state, slot, "resumed", observedCurrentHp, observedMaximumHp,
            "health recovered from temporary transition zero");
        return false;
    }

    if (Changed(pending.targetMaximumHp, observedMaximumHp)) {
        const float previousTargetMaximumHp = pending.targetMaximumHp;
        const bool latestIncrease = observedMaximumHp > previousTargetMaximumHp;

        // Maximum HP can move up, down, and back across the original value in
        // one transformation chain. Keep one held ratio across the entire
        // chain instead of cancelling and exposing a raw-scale frame.
        pending.targetMaximumHp = observedMaximumHp;
        pending.transitionBaselineRatio = pending.trackedRatio;
        pending.transitionBaselineCurrentHp = observedCurrentHp;
        pending.transitionBaselineScaleHp = hs::SelectTransitionCurrentScale(
            latestIncrease,
            previousTargetMaximumHp,
            observedCurrentHp,
            observedMaximumHp);
        pending.sourceDomain = NearlyEqualRatio(
            pending.transitionBaselineScaleHp, observedMaximumHp, 0.001)
                ? hs::HealthScaleValueDomain::TargetScale
                : hs::HealthScaleValueDomain::CanonicalScale;
        pending.increase = latestIncrease;
        pending.lastMaximumChangeTick = now;
        pending.stableSamples = 0;
        pending.sawTemporaryZero = false;
        pending.lastObservedCurrentHp = observedCurrentHp;
        pending.domainMaximumJustChanged = true;
        PublishHealthTransition(
            state, slot, "target-updated", observedCurrentHp, observedMaximumHp,
            "maximum-health transition chain changed target before stabilization");
        if (pending.hudLane < hs::kHudHealthLaneCount) {
            hs::UpdateHudHealthTransitionBridge(
                pending.hudLane,
                pending.mob,
                static_cast<float>(pending.trackedRatio),
                observedMaximumHp);
        }
        hs::Log(L"CORRECTION TARGET UPDATED slot=%zu mob=0x%p direction=%ls "
                L"previous-target-max=%.3f target-max=%.3f "
                L"transition-baseline=%.3f baseline-scale=%.3f tracked-ratio=%.9f",
                slot, reinterpret_cast<void*>(state.mob),
                latestIncrease ? L"increase" : L"decrease",
                previousTargetMaximumHp, observedMaximumHp, observedCurrentHp,
                pending.transitionBaselineScaleHp, pending.trackedRatio);
        return false;
    }

    // During a large decrease, the game can expose old-domain current HP with
    // the new maximum, then clamp/rebase current HP into the new domain. The
    // first target-domain sample is baseline-only: accepting 90,000/90,000 as a
    // new ratio after x20 Kaioken would create the observed full heal.
    const bool targetScaleRebase = !pending.increase &&
        hs::IsTargetScaleRebaseAfterDecrease(
            pending.transitionBaselineScaleHp,
            observedMaximumHp,
            observedCurrentHp);

    if (targetScaleRebase) {
        hs::Log(L"CORRECTION TARGET-DOMAIN REBASE slot=%zu mob=0x%p "
                L"held-ratio=%.9f observed-current=%.3f observed-max=%.3f "
                L"previous-scale=%.3f",
                slot, reinterpret_cast<void*>(state.mob), pending.trackedRatio,
                observedCurrentHp, observedMaximumHp,
                pending.transitionBaselineScaleHp);
        pending.sourceDomain = hs::HealthScaleValueDomain::TargetScale;
        pending.transitionBaselineRatio = pending.trackedRatio;
        pending.transitionBaselineCurrentHp = observedCurrentHp;
        pending.transitionBaselineScaleHp = observedMaximumHp;
        pending.domainMaximumJustChanged = false;
    } else {
        const auto domainObservation = hs::ClassifyHealthScaleValueDomain(
            pending.transitionBaselineScaleHp,
            observedMaximumHp,
            pending.lastObservedCurrentHp,
            pending.trackedRatio,
            observedCurrentHp,
            pending.domainMaximumJustChanged);
        pending.domainMaximumJustChanged = false;
        if (observedCurrentHp > 0.0f) {
            const bool sourceAndTargetShareScale = NearlyEqualRatio(
                pending.transitionBaselineScaleHp, observedMaximumHp, 0.001);
            const bool looksLikeSelectedSourceScale = sourceAndTargetShareScale ||
                (observedCurrentHp <= pending.transitionBaselineScaleHp * 1.25f &&
                 (domainObservation.domain == hs::HealthScaleValueDomain::CanonicalScale ||
                  pending.sourceDomain == hs::HealthScaleValueDomain::CanonicalScale));
            if (looksLikeSelectedSourceScale) {
                pending.trackedRatio = hs::ComputeTransitionRatio(
                    pending.transitionBaselineRatio,
                    pending.transitionBaselineCurrentHp,
                    pending.transitionBaselineScaleHp,
                    observedCurrentHp).ratio;
            } else if (domainObservation.domain == hs::HealthScaleValueDomain::TargetScale) {
                pending.sourceDomain = hs::HealthScaleValueDomain::TargetScale;
                pending.trackedRatio = std::clamp(
                    static_cast<double>(observedCurrentHp) /
                        static_cast<double>(observedMaximumHp),
                    0.0, 1.0);
                pending.transitionBaselineRatio = pending.trackedRatio;
                pending.transitionBaselineCurrentHp = observedCurrentHp;
                pending.transitionBaselineScaleHp = observedMaximumHp;
            }
        }
    }
    pending.lastObservedCurrentHp = observedCurrentHp;
    if (pending.hudLane < hs::kHudHealthLaneCount && observedCurrentHp > 0.0f) {
        hs::UpdateHudHealthTransitionBridge(
            pending.hudLane,
            pending.mob,
            static_cast<float>(pending.trackedRatio),
            observedMaximumHp);
    }

    ++pending.stableSamples;
    const DWORD requiredStabilizationMs = pending.increase
        ? settings.increaseStabilizationMs
        : settings.decreaseStabilizationMs;
    if (now - pending.lastMaximumChangeTick < requiredStabilizationMs ||
        pending.stableSamples < kRequiredStableSamples) {
        return false;
    }

    if (!writesEnabled) {
        CancelPending(state, slot, L"writes disabled");
        return false;
    }

    const double observedRatio = observedMaximumHp > 0.0f
        ? static_cast<double>(observedCurrentHp) /
          static_cast<double>(observedMaximumHp)
        : 0.0;
    if (pending.sourceDomain == hs::HealthScaleValueDomain::Invalid ||
        pending.sourceDomain == hs::HealthScaleValueDomain::Ambiguous) {
        // Ambiguous domain evidence is not enough to justify a health write.
        // Keep the HUD bridge on the held ratio and wait for a clearer sample.
        return false;
    }

    if (pending.sourceDomain == hs::HealthScaleValueDomain::TargetScale &&
        NearlyEqualRatio(observedRatio, pending.trackedRatio)) {
        hs::Log(L"CORRECTION NOT NEEDED slot=%zu mob=0x%p current=%.3f "
                L"max=%.3f ratio=%.9f tracked-ratio=%.9f",
                slot, reinterpret_cast<void*>(state.mob), observedCurrentHp,
                observedMaximumHp, observedRatio, pending.trackedRatio);
        PublishHealthTransition(
            state, slot, "not-needed", observedCurrentHp, observedMaximumHp,
            "game state already matched the tracked transition ratio");
        if (pending.hudLane < hs::kHudHealthLaneCount) {
            hs::CompleteHudHealthTransitionBridge(
                pending.hudLane, pending.mob, observedCurrentHp, observedMaximumHp);
        }
        CommitDomainLease(
            state, pending, observedCurrentHp, observedMaximumHp, now);
        pending = {};
        return false;
    }

    // For increases, write only while current HP still resembles the old-scale
    // value. For decreases, Xenoverse may either retain old current HP or clamp
    // it to the new maximum, so the increase-only ceiling is not applicable.
    if (pending.increase &&
        pending.sourceDomain != hs::HealthScaleValueDomain::TargetScale) {
        const double oldScaleCeiling =
            static_cast<double>(pending.transitionBaselineScaleHp) * 1.25 + 1.0;
        if (static_cast<double>(observedCurrentHp) > oldScaleCeiling) {
            hs::Log(L"CORRECTION SKIPPED slot=%zu mob=0x%p reason=current HP no "
                    L"longer looks old-scale observed=%.3f old-max=%.3f",
                    slot, reinterpret_cast<void*>(state.mob), observedCurrentHp,
                    pending.transitionBaselineScaleHp);
            PublishHealthTransition(
                state, slot, "skipped", observedCurrentHp, observedMaximumHp,
                "current health no longer resembled the pre-transition scale");
            if (pending.hudLane < hs::kHudHealthLaneCount) {
                hs::CancelHudHealthTransitionBridge(pending.hudLane, pending.mob);
            }
            pending = {};
            return false;
        }
    }

    const double correctedRatio = pending.trackedRatio;
    const double transitionDeltaRatio =
        correctedRatio - pending.transitionBaselineRatio;
    double corrected = correctedRatio *
                       static_cast<double>(observedMaximumHp);

    corrected = std::clamp(corrected, 0.0,
                           static_cast<double>(observedMaximumHp));
    if (pending.preservedRatio > 0.0 && corrected > 0.0 && corrected < 1.0) {
        corrected = 1.0;
    }

    const float writeValue = static_cast<float>(corrected);

    // Final ownership check: a readable address is not enough during quest
    // teardown because freed fighter memory can remain committed. Confirm that
    // the live battle-core slot still owns the exact fighter object immediately
    // before touching its HP field.
    std::uintptr_t liveSlotMob = 0;
    const std::uintptr_t liveSlotAddress = battleCore + kMobArrayOffset +
        slot * sizeof(std::uintptr_t);
    if (!SafeRead(liveSlotAddress, liveSlotMob) ||
        liveSlotMob != state.mob || liveSlotMob != pending.mob) {
        CancelPending(state, slot, L"fighter no longer owned by live battle-core slot");
        return false;
    }

    float verifyMaximum = 0.0f;
    float verifyCurrent = 0.0f;
    if (!ReadMobHealth(state.mob, verifyCurrent, verifyMaximum) ||
        Changed(verifyMaximum, observedMaximumHp)) {
        CancelPending(state, slot, L"health changed during final verification");
        return false;
    }

    if (!SafeWrite(state.mob + kCurrentHpOffset, writeValue)) {
        hs::Log(L"CORRECTION WRITE FAILED slot=%zu mob=0x%p address=0x%p",
                slot, reinterpret_cast<void*>(state.mob),
                reinterpret_cast<void*>(state.mob + kCurrentHpOffset));
        PublishHealthTransition(
            state, slot, "write-failed", observedCurrentHp, observedMaximumHp,
            "guarded HealthScale current-health correction write failed");
        if (pending.hudLane < hs::kHudHealthLaneCount) {
            hs::CancelHudHealthTransitionBridge(pending.hudLane, pending.mob);
        }
        pending = {};
        return false;
    }

    float readBack = 0.0f;
    if (!SafeRead(state.mob + kCurrentHpOffset, readBack)) {
        hs::Log(L"CORRECTION WRITE UNVERIFIED slot=%zu mob=0x%p wrote=%.3f",
                slot, reinterpret_cast<void*>(state.mob), writeValue);
        PublishHealthTransition(
            state, slot, "write-unverified", observedCurrentHp, observedMaximumHp,
            "HealthScale wrote current health but could not verify readback");
        if (pending.hudLane < hs::kHudHealthLaneCount) {
            hs::CancelHudHealthTransitionBridge(pending.hudLane, pending.mob);
        }
        pending = {};
        return false;
    }

    hs::Log(L"CORRECTION APPLIED slot=%zu mob=0x%p direction=%ls "
            L"old-current=%.3f old-max=%.3f new-max=%.3f ratio=%.9f "
            L"baseline=%.3f baseline-scale=%.3f delta-ratio=%.9f "
            L"tracked-ratio=%.9f wrote=%.3f readback=%.3f",
            slot, reinterpret_cast<void*>(state.mob),
            pending.increase ? L"increase" : L"decrease",
            pending.oldCurrentHp, pending.oldMaximumHp, observedMaximumHp,
            pending.preservedRatio, pending.transitionBaselineCurrentHp,
            pending.transitionBaselineScaleHp, transitionDeltaRatio,
            correctedRatio, writeValue, readBack);

    if (pending.hudLane < hs::kHudHealthLaneCount) {
        hs::CompleteHudHealthTransitionBridge(
            pending.hudLane, pending.mob, readBack, observedMaximumHp);
    }

    PublishHealthTransition(
        state, slot, "completed", readBack, observedMaximumHp,
        "guarded HealthScale correction was applied and read back");
    CommitDomainLease(state, pending, readBack, observedMaximumHp, now);
    pending = {};
    state.currentHp = readBack;
    return true;
}

bool ApplyDomainLeaseCorrection(
    SlotState& state,
    std::size_t slot,
    std::size_t hudLane,
    float& observedCurrentHp,
    float observedMaximumHp,
    DWORD now,
    bool writesEnabled,
    std::uintptr_t battleCore) {
    auto& lease = state.domainLease;
    if (!lease.active) return false;
    if (lease.mob != state.mob || observedCurrentHp <= 0.0f ||
        !std::isfinite(observedMaximumHp) || observedMaximumHp <= 0.0f) {
        lease = {};
        return false;
    }
    if (Changed(lease.targetMaximumHp, observedMaximumHp)) {
        // The normal maximum-change path will queue a new chained correction
        // while preserving this lease's canonical scale and tracked ratio.
        return false;
    }

    const auto domain = hs::ClassifyHealthScaleValueDomain(
        lease.canonicalMaximumHp,
        lease.targetMaximumHp,
        lease.lastObservedCurrentHp,
        lease.trackedRatio,
        observedCurrentHp,
        false);
    lease.lastObservedCurrentHp = observedCurrentHp;

    if (domain.domain == hs::HealthScaleValueDomain::TargetScale &&
        observedCurrentHp > lease.canonicalMaximumHp * 1.25f) {
        lease.trackedRatio = domain.ratio;
        lease.canonicalSourceActive = false;
        lease.canonicalBaselineCurrentHp = 0.0f;
        lease.canonicalBaselineRatio = lease.trackedRatio;
        return false;
    }
    if (domain.domain != hs::HealthScaleValueDomain::CanonicalScale ||
        !domain.abruptCanonicalRelapse || !writesEnabled) {
        return false;
    }

    // The first canonical-domain relapse is a source-scale switch, not proof
    // that the absolute canonical value is the new percentage. Anchor that
    // value to the percentage already held in the target domain. Subsequent
    // canonical writes contribute only their delta over the canonical maximum.
    if (!lease.canonicalSourceActive) {
        lease.canonicalSourceActive = true;
        lease.canonicalBaselineCurrentHp = observedCurrentHp;
        lease.canonicalBaselineRatio = lease.trackedRatio;
    }
    const auto bridged = hs::ComputeTransitionRatio(
        lease.canonicalBaselineRatio,
        lease.canonicalBaselineCurrentHp,
        lease.canonicalMaximumHp,
        observedCurrentHp);
    const double bridgedRatio = bridged.ratio;

    double corrected = std::clamp(
        bridgedRatio * static_cast<double>(observedMaximumHp),
        0.0,
        static_cast<double>(observedMaximumHp));
    if (bridgedRatio > 0.0 && corrected > 0.0 && corrected < 1.0) corrected = 1.0;
    const float writeValue = static_cast<float>(corrected);

    std::uintptr_t liveSlotMob = 0;
    const std::uintptr_t liveSlotAddress = battleCore + kMobArrayOffset +
        slot * sizeof(std::uintptr_t);
    if (!SafeRead(liveSlotAddress, liveSlotMob) ||
        liveSlotMob != state.mob || liveSlotMob != lease.mob) {
        lease = {};
        return false;
    }

    float verifyCurrent = 0.0f;
    float verifyMaximum = 0.0f;
    if (!ReadMobHealth(state.mob, verifyCurrent, verifyMaximum) ||
        Changed(verifyMaximum, observedMaximumHp) ||
        Changed(verifyCurrent, observedCurrentHp)) {
        return false;
    }

    if (hudLane < hs::kHudHealthLaneCount) {
        hs::BeginHudHealthTransitionBridge(
            hudLane,
            state.mob,
            static_cast<float>(bridgedRatio),
            observedCurrentHp,
            lease.canonicalMaximumHp,
            observedMaximumHp);
    }
    PublishHealthTransition(
        state, slot, "started", observedCurrentHp, observedMaximumHp,
        "late canonical-scale current-health relapse detected");

    if (!SafeWrite(state.mob + kCurrentHpOffset, writeValue)) {
        if (hudLane < hs::kHudHealthLaneCount)
            hs::CancelHudHealthTransitionBridge(hudLane, state.mob);
        PublishHealthTransition(
            state, slot, "cancelled", observedCurrentHp, observedMaximumHp,
            "late canonical-scale relapse correction write failed");
        return false;
    }
    float readBack = 0.0f;
    if (!SafeRead(state.mob + kCurrentHpOffset, readBack)) {
        if (hudLane < hs::kHudHealthLaneCount)
            hs::CancelHudHealthTransitionBridge(hudLane, state.mob);
        PublishHealthTransition(
            state, slot, "cancelled", observedCurrentHp, observedMaximumHp,
            "late canonical-scale relapse correction readback failed");
        return false;
    }

    ++lease.relapseCorrections;
    lease.trackedRatio = bridgedRatio;
    lease.lastObservedCurrentHp = readBack;
    lease.lastCorrectionTick = now;
    observedCurrentHp = readBack;
    state.currentHp = readBack;
    if (hudLane < hs::kHudHealthLaneCount) {
        hs::CompleteHudHealthTransitionBridge(
            hudLane, state.mob, readBack, observedMaximumHp);
    }
    PublishHealthTransition(
        state, slot, "completed", readBack, observedMaximumHp,
        "late canonical-scale current-health relapse remapped to target scale");
    hs::Log(L"HEALTH SCALE DOMAIN RELAPSE CORRECTED slot=%zu mob=0x%p "
            L"canonical-current=%.3f canonical-max=%.3f target-max=%.3f "
            L"ratio=%.9f wrote=%.3f readback=%.3f lease-corrections=%llu",
            slot, reinterpret_cast<void*>(state.mob), verifyCurrent,
            lease.canonicalMaximumHp, observedMaximumHp, bridgedRatio,
            writeValue, readBack,
            static_cast<unsigned long long>(lease.relapseCorrections));
    return true;
}

int FindSlotForMob(const std::array<SlotState, kMobSlotCount>& states,
                   std::uintptr_t mob) noexcept {
    for (std::size_t slot = 0; slot < kMobSlotCount; ++slot) {
        if (states[slot].valid && states[slot].mob == mob) {
            return static_cast<int>(slot);
        }
    }
    return -1;
}

} // namespace

namespace hs {
DWORD RunHealthOverhaulRuntime() {
    HMODULE patcher = nullptr;
    for (int attempt = 0; attempt < 300 && !patcher; ++attempt) {
        patcher = GetModuleHandleW(L"xinput1_3.dll");
        if (!patcher) Sleep(100);
    }

    if (!patcher) {
        Log(L"ERROR: XV2 Patcher xinput1_3.dll was not found. runtime stopped.");
        return 1;
    }

    const DWORD patcherImageSize = ReadImageSize(patcher);
    Log(L"XV2 Patcher base: 0x%p; image size: 0x%08lX",
        patcher, patcherImageSize);
    if (patcherImageSize != kExpectedPatcherImageSize) {
        Log(L"ERROR: HealthScale Overhaul expects XV2 Patcher 4.64 image size 0x%08lX. "
            L"Health writes are disabled.", kExpectedPatcherImageSize);
        return 2;
    }

    const Settings settings = LoadSettings();
    const bool writesEnabled = settings.writeHealth;
    gHealthRuntimeRunning.store(true, std::memory_order_release);
    gHealthWritesEnabled.store(writesEnabled, std::memory_order_release);

    Log(L"HealthScale Overhaul Final runtime started.");
    Log(L"Features: normalized native health bars and transformation-safe percentage preservation.");
    Log(L"Offsets: mob-array=0x%zX, current-HP=0x%zX, maximum-HP=0x%zX, slots=%zu",
        kMobArrayOffset, kCurrentHpOffset, kMaximumHpOffset, kMobSlotCount);
    Log(L"Correction: writes=%ls increases=%ls decreases=%ls preserve-delta=%ls increase-stabilization=%lums decrease-stabilization=%lums timeout=%lums",
        writesEnabled ? L"enabled" : L"disabled",
        settings.correctMaximumIncreases ? L"yes" : L"no",
        settings.correctMaximumDecreases ? L"yes" : L"no",
        settings.preserveTransitionDelta ? L"yes" : L"no",
        settings.increaseStabilizationMs, settings.decreaseStabilizationMs,
        settings.maximumPendingMs);

    std::array<SlotState, kMobSlotCount> states{};
    std::size_t selectedPlayerSlot = settings.playerSlot;
    std::size_t autoTargetSlot = kInvalidHudSlot;
    std::uintptr_t lastAutoTargetMob = 0;
    bool lastAutoTargetValid = false;
    std::uintptr_t pendingAutoTargetMob = 0;
    std::size_t pendingAutoTargetSlot = kInvalidHudSlot;
    DWORD pendingAutoTargetSamples = 0;
    DWORD invalidAutoTargetSamples = 0;
    std::uintptr_t lastPrimaryTargetRaw = 0;
    std::uintptr_t lastSecondaryTargetRaw = 0;
    bool lastTargetConsensus = false;
    bool targetUncertainPublished = false;
    DWORD lastTargetConsensusLogTick = 0;
    std::uintptr_t lastStorageAddress = 0;
    std::uintptr_t lastCore = 0;
    DWORD lastHeartbeatTick = GetTickCount();
    bool warnedUnresolved = false;
    bool battleActive = false;
    bool playerHudWasActive = false;
    bool targetHudWasActive = false;
    DWORD battleReadySamples = 0;
    DWORD playerLossSamples = 0;
    DWORD transitionResumeTick = 0;
    std::uintptr_t candidateCore = 0;
    DWORD candidateCoreSamples = 0;

    auto quiesceForTransition = [&](const wchar_t* reason, DWORD now,
                                    bool applyCooldown) {
        bool hadState = battleActive || lastCore != 0 || lastAutoTargetValid;
        for (std::size_t slot = 0; slot < kMobSlotCount; ++slot) {
            if (states[slot].pending.active) hadState = true;
            CancelPending(states[slot], slot, reason);
        }
        states = {};
        autoTargetSlot = kInvalidHudSlot;
        lastAutoTargetMob = 0;
        lastAutoTargetValid = false;
        pendingAutoTargetMob = 0;
        pendingAutoTargetSlot = kInvalidHudSlot;
        pendingAutoTargetSamples = 0;
        invalidAutoTargetSamples = 0;
        lastPrimaryTargetRaw = 0;
        lastSecondaryTargetRaw = 0;
        lastTargetConsensus = false;
        targetUncertainPublished = false;
        lastTargetConsensusLogTick = 0;
        battleActive = false;
        playerHudWasActive = false;
        targetHudWasActive = false;
        battleReadySamples = 0;
        playerLossSamples = 0;
        if (applyCooldown) {
            transitionResumeTick = now + settings.transitionCooldownMs;
        }
        if (hadState) {
            WriteHudHealthNormalizerReportSnapshot("battle transition quiesced");
            Log(L"QUEST TRANSITION QUIESCE reason=%ls cooldown-until=%lu",
                reason, transitionResumeTick);
        }
        ResetHudHealthPresentationTracker();
    };

    for (;;) {
        const DWORD now = GetTickCount();

        const auto resolution = ResolveBattleCore(patcher);

        if (resolution.storageAddress == 0 || resolution.selectedCore == 0 ||
            resolution.selectedScore < 0) {
            if (!warnedUnresolved) {
                Log(L"Waiting for a stable XV2 battle-core singleton; health processing is quiesced.");
                warnedUnresolved = true;
                quiesceForTransition(L"battle core unresolved or structurally invalid",
                                     now, true);
            } else {
            }
            lastCore = 0;
            candidateCore = 0;
            candidateCoreSamples = 0;
            Sleep(kPollIntervalMs);
            continue;
        }
        warnedUnresolved = false;

        if (resolution.selectedCore != candidateCore) {
            candidateCore = resolution.selectedCore;
            candidateCoreSamples = 1;
        } else if (candidateCoreSamples < settings.coreStableSamples) {
            ++candidateCoreSamples;
        }

        if (candidateCoreSamples < settings.coreStableSamples) {
            if (battleActive) {
                quiesceForTransition(L"battle core changed before stabilization",
                                     now, true);
            } else {
            }
            Sleep(kPollIntervalMs);
            continue;
        }

        if (resolution.storageAddress != lastStorageAddress) {
            Log(L"Battle-core singleton storage resolved: 0x%p",
                reinterpret_cast<void*>(resolution.storageAddress));
            Log(L"Pointer chain: first=0x%p score=%d second=0x%p score=%d storage-score=%d selected=0x%p score=%d",
                reinterpret_cast<void*>(resolution.firstPointer),
                resolution.firstScore,
                reinterpret_cast<void*>(resolution.secondPointer),
                resolution.secondScore,
                resolution.storageScore,
                reinterpret_cast<void*>(resolution.selectedCore),
                resolution.selectedScore);
            lastStorageAddress = resolution.storageAddress;
        }

        const auto core = resolution.selectedCore;
        if (core != lastCore) {
            Log(L"Battle-core object changed: 0x%p -> 0x%p",
                reinterpret_cast<void*>(lastCore), reinterpret_cast<void*>(core));
            quiesceForTransition(L"new battle core detected", now, true);
            lastCore = core;
            Sleep(kPollIntervalMs);
            continue;
        }

        // During an active battle, verify the selected player is still owned by
        // the live fighter array before processing any other slot or pending
        // write. This closes the teardown window where a stale fighter object
        // remains readable for a few frames after the quest has ended.
        if (battleActive && selectedPlayerSlot < kMobSlotCount) {
            std::uintptr_t livePlayerMob = 0;
            float livePlayerCurrent = 0.0f;
            float livePlayerMaximum = 0.0f;
            const std::uintptr_t livePlayerSlotAddress = core + kMobArrayOffset +
                selectedPlayerSlot * sizeof(std::uintptr_t);
            const bool livePlayerValid =
                SafeRead(livePlayerSlotAddress, livePlayerMob) &&
                livePlayerMob != 0 &&
                states[selectedPlayerSlot].valid &&
                livePlayerMob == states[selectedPlayerSlot].mob &&
                ReadMobHealth(livePlayerMob, livePlayerCurrent, livePlayerMaximum);
            if (!livePlayerValid) {
                ++playerLossSamples;
                if (playerLossSamples >= settings.playerLossSamples) {
                    quiesceForTransition(L"player fighter ownership lost",
                                         now, true);
                    lastCore = 0;
                    candidateCore = 0;
                    candidateCoreSamples = 0;
                }
                Sleep(kPollIntervalMs);
                continue;
            }
            playerLossSamples = 0;
        }

        std::size_t activeCount = 0;

        for (std::size_t slot = 0; slot < kMobSlotCount; ++slot) {
            std::uintptr_t mob = 0;
            const auto slotAddress = core + kMobArrayOffset +
                                     slot * sizeof(std::uintptr_t);
            auto& state = states[slot];
            if (!SafeRead(slotAddress, mob)) {
                if (state.valid || state.pending.active) {
                    CancelPending(state, slot, L"fighter slot became unreadable");
                    Log(L"FIGHTER INVALIDATED slot=%zu reason=slot-unreadable", slot);
                    state = {};
                }
                continue;
            }

            if (mob == 0) {
                if (state.valid) {
                    Log(L"FIGHTER REMOVED slot=%zu mob=0x%p", slot,
                        reinterpret_cast<void*>(state.mob));
                    state = {};
                }
                continue;
            }

            float currentHp = 0.0f;
            float maximumHp = 0.0f;
            if (!ReadMobHealth(mob, currentHp, maximumHp)) {
                if (state.valid || state.pending.active) {
                    Log(L"INVALID MOB CANDIDATE slot=%zu mob=0x%p; stale state cleared",
                        slot, reinterpret_cast<void*>(mob));
                }
                CancelPending(state, slot, L"fighter object became unreadable or invalid");
                state = {};
                continue;
            }
            ++activeCount;

            if (!state.valid || state.mob != mob) {
                state = {};
                state.mob = mob;
                state.currentHp = currentHp;
                state.maximumHp = maximumHp;
                state.lastCurrentLogTick = now;
                state.valid = true;
                LogSlot(slot, mob, currentHp, maximumHp, L"FIGHTER FOUND");
                continue;
            }

            const bool maxChanged = Changed(state.maximumHp, maximumHp);
            const bool currentChanged = Changed(state.currentHp, currentHp);

            if (maxChanged) {
                const bool increase = maximumHp > state.maximumHp;
                const double oldRatio = state.maximumHp > 0.0f
                    ? static_cast<double>(state.currentHp) /
                      static_cast<double>(state.maximumHp)
                    : 0.0;
                const double proposed = std::clamp(
                    oldRatio * static_cast<double>(maximumHp),
                    0.0, static_cast<double>(maximumHp));

                Log(L"MAX-HP CHANGE slot=%zu mob=0x%p old-current=%.3f "
                    L"old-max=%.3f new-current=%.3f new-max=%.3f "
                    L"old-ratio=%.9f proposed-current=%.3f",
                    slot, reinterpret_cast<void*>(mob), state.currentHp,
                    state.maximumHp, currentHp, maximumHp, oldRatio, proposed);

                const bool directionEnabled = increase
                    ? settings.correctMaximumIncreases
                    : settings.correctMaximumDecreases;
                if (state.pending.active) {
                    // Keep the original pre-transformation ratio. The pending
                    // handler below will update only the target maximum if a
                    // transformation reaches its final maximum in stages.
                    Log(L"MAX-HP CHANGE occurred while correction is pending; "
                        L"retaining one chained transition ratio for slot=%zu", slot);
                } else if (battleActive && directionEnabled && writesEnabled &&
                           state.currentHp > 0.0f) {
                    const std::size_t hudLane = slot == selectedPlayerSlot
                        ? kPlayerHudHealthLane
                        : (lastAutoTargetValid && mob == lastAutoTargetMob
                            ? kTargetHudHealthLane
                            : kHudHealthLaneCount);
                    QueueCorrection(
                        state, slot, currentHp, maximumHp, now, increase, hudLane);
                } else {
                    CancelPending(state, slot,
                                  directionEnabled ? L"writes disabled or old HP is zero"
                                                   : L"direction disabled in settings");
                }
            }

            const bool wrote = battleActive && ApplyPendingCorrection(
                state, slot, currentHp, maximumHp, now, writesEnabled, settings, core);
            if (wrote) {
                SafeRead(mob + kCurrentHpOffset, currentHp);
            }

            if (battleActive && !state.pending.active && state.domainLease.active) {
                const std::size_t leaseHudLane = slot == selectedPlayerSlot
                    ? kPlayerHudHealthLane
                    : (lastAutoTargetValid && mob == lastAutoTargetMob
                        ? kTargetHudHealthLane
                        : kHudHealthLaneCount);
                ApplyDomainLeaseCorrection(
                    state, slot, leaseHudLane, currentHp, maximumHp,
                    now, writesEnabled, core);
            }

            if (!maxChanged && currentChanged &&
                now - state.lastCurrentLogTick >= kCurrentHpLogIntervalMs) {
                LogSlot(slot, mob, currentHp, maximumHp, L"HP CHANGE");
                state.lastCurrentLogTick = now;
            }


            state.currentHp = currentHp;
            state.maximumHp = maximumHp;
        }

        const bool playerReady = selectedPlayerSlot < kMobSlotCount &&
                                 states[selectedPlayerSlot].valid;
        if (!playerReady || activeCount == 0) {
            battleReadySamples = 0;
            if (battleActive) {
                ++playerLossSamples;
                if (playerLossSamples >= settings.playerLossSamples) {
                    quiesceForTransition(L"player fighter disappeared after slot scan",
                                         now, true);
                    lastCore = 0;
                    candidateCore = 0;
                    candidateCoreSamples = 0;
                }
            } else {
            }
            Sleep(kPollIntervalMs);
            continue;
        }
        playerLossSamples = 0;

        if (!battleActive) {
            if (static_cast<LONG>(now - transitionResumeTick) < 0) {
                Sleep(kPollIntervalMs);
                continue;
            }
            ++battleReadySamples;
            if (battleReadySamples < settings.battleReadySamples) {
                Sleep(kPollIntervalMs);
                continue;
            }
            battleActive = true;
            ResetHudHealthPresentationTracker();
            SetHudHealthPresentationLaneEligible(kPlayerHudHealthLane, true);
            SetHudHealthPresentationLaneEligible(kTargetHudHealthLane, false);
            Log(L"HEALTH HUD TRACKER armed for fresh post-battle-active writer submissions.");
            Log(L"BATTLE LIFECYCLE ACTIVE core=0x%p player-slot=%zu player-mob=0x%p active-fighters=%zu",
                reinterpret_cast<void*>(core), selectedPlayerSlot,
                reinterpret_cast<void*>(states[selectedPlayerSlot].mob),
                activeCount);
        }

        bool autoTargetValid = false;
        std::uintptr_t rawAutoTargetMob = 0;
        std::uintptr_t rawPrimaryTargetMob = 0;
        std::uintptr_t rawSecondaryTargetMob = 0;
        std::size_t rawCandidateSlot = kInvalidHudSlot;
        autoTargetSlot = kInvalidHudSlot;

        bool primaryReadable = false;
        bool secondaryReadable = false;
        bool targetConsensus = false;
        bool rawCandidateEligible = false;
        const wchar_t* rejectionReason = L"no-consensus";

        if (settings.autoTarget &&
            selectedPlayerSlot < kMobSlotCount &&
            states[selectedPlayerSlot].valid) {
            const std::uintptr_t playerMob = states[selectedPlayerSlot].mob;
            primaryReadable = SafeRead(
                playerMob + kPrimaryPlayerTargetPointerOffset,
                rawPrimaryTargetMob);
            secondaryReadable = SafeRead(
                playerMob + kSecondaryPlayerTargetPointerOffset,
                rawSecondaryTargetMob);

            targetConsensus = primaryReadable && secondaryReadable &&
                              rawPrimaryTargetMob != 0 &&
                              rawPrimaryTargetMob == rawSecondaryTargetMob;

            if (targetConsensus) {
                rawAutoTargetMob = rawPrimaryTargetMob;
                const int resolvedSlot = FindSlotForMob(states, rawAutoTargetMob);
                if (resolvedSlot < 0) {
                    rejectionReason = L"not-in-fighter-array";
                } else if (static_cast<std::size_t>(resolvedSlot) == selectedPlayerSlot) {
                    rejectionReason = L"player-object";
                } else {
                    rawCandidateSlot = static_cast<std::size_t>(resolvedSlot);
                    const auto& candidate = states[rawCandidateSlot];
                    if (!candidate.valid) {
                        rejectionReason = L"inactive-slot";
                    } else if (!std::isfinite(candidate.currentHp) ||
                               !std::isfinite(candidate.maximumHp)) {
                        rejectionReason = L"non-finite-health";
                    } else if (candidate.maximumHp < settings.minimumTargetMaximumHp) {
                        rejectionReason = L"maximum-hp-below-filter";
                    } else if (candidate.currentHp <= 0.0f) {
                        rejectionReason = L"dead-or-zero-health";
                    } else {
                        rawCandidateEligible = true;
                        rejectionReason = L"eligible";
                    }
                }
            }
        }

        if (settings.autoTarget) {
            const bool rawStateChanged =
                rawPrimaryTargetMob != lastPrimaryTargetRaw ||
                rawSecondaryTargetMob != lastSecondaryTargetRaw ||
                targetConsensus != lastTargetConsensus;

            if (rawStateChanged &&
                now - lastTargetConsensusLogTick >= 250) {
                Log(L"AUTO TARGET CONSENSUS primary(+0x%zX)=0x%p "
                    L"secondary(+0x%zX)=0x%p agree=%ls resolved-slot=%zu eligible=%ls reason=%ls",
                    kPrimaryPlayerTargetPointerOffset,
                    reinterpret_cast<void*>(rawPrimaryTargetMob),
                    kSecondaryPlayerTargetPointerOffset,
                    reinterpret_cast<void*>(rawSecondaryTargetMob),
                    targetConsensus ? L"yes" : L"no",
                    rawCandidateSlot,
                    rawCandidateEligible ? L"yes" : L"no",
                    rejectionReason);
                lastTargetConsensusLogTick = now;
            }

            if (rawCandidateEligible) {
                invalidAutoTargetSamples = 0;
                targetUncertainPublished = false;

                if (lastAutoTargetValid && rawAutoTargetMob == lastAutoTargetMob) {
                    const int resolved = FindSlotForMob(states, lastAutoTargetMob);
                    if (resolved >= 0) {
                        autoTargetSlot = static_cast<std::size_t>(resolved);
                        autoTargetValid = true;
                    }
                    pendingAutoTargetMob = 0;
                    pendingAutoTargetSlot = kInvalidHudSlot;
                    pendingAutoTargetSamples = 0;
                } else {
                    if (pendingAutoTargetMob == rawAutoTargetMob) {
                        ++pendingAutoTargetSamples;
                    } else {
                        pendingAutoTargetMob = rawAutoTargetMob;
                        pendingAutoTargetSlot = rawCandidateSlot;
                        pendingAutoTargetSamples = 1;
                    }

                    if (pendingAutoTargetSamples >= settings.targetStableSamples) {
                        if (lastAutoTargetValid) {
                            const int oldResolvedSlot =
                                FindSlotForMob(states, lastAutoTargetMob);
                            Log(L"AUTO TARGET CHANGED source=dual-consensus-final "
                                L"old-slot=%zu old-mob=0x%p new-slot=%zu new-mob=0x%p samples=%lu",
                                oldResolvedSlot >= 0
                                    ? static_cast<std::size_t>(oldResolvedSlot)
                                    : kInvalidHudSlot,
                                reinterpret_cast<void*>(lastAutoTargetMob),
                                pendingAutoTargetSlot,
                                reinterpret_cast<void*>(pendingAutoTargetMob),
                                pendingAutoTargetSamples);
                        } else {
                            Log(L"AUTO TARGET ACQUIRED source=dual-consensus-final "
                                L"slot=%zu mob=0x%p samples=%lu",
                                pendingAutoTargetSlot,
                                reinterpret_cast<void*>(pendingAutoTargetMob),
                                pendingAutoTargetSamples);
                        }

                        // A target lane latch belongs to one target ownership
                        // epoch. A newly acquired or changed target must earn
                        // fresh paired native HUD submissions; it may not
                        // inherit readiness from the previous target.
                        SetHudHealthPresentationLaneEligible(
                            kTargetHudHealthLane, false);
                        SetHudHealthPresentationLaneEligible(
                            kTargetHudHealthLane, true);
                        targetHudWasActive = false;
                        lastAutoTargetMob = pendingAutoTargetMob;
                        lastAutoTargetValid = true;
                        autoTargetSlot = pendingAutoTargetSlot;
                        autoTargetValid = true;
                        pendingAutoTargetMob = 0;
                        pendingAutoTargetSlot = kInvalidHudSlot;
                        pendingAutoTargetSamples = 0;
                    } else if (lastAutoTargetValid) {
                        const int oldResolved = FindSlotForMob(states, lastAutoTargetMob);
                        if (oldResolved >= 0) {
                            const auto& oldState = states[static_cast<std::size_t>(oldResolved)];
                            if (oldState.valid && oldState.currentHp > 0.0f &&
                                oldState.maximumHp >= settings.minimumTargetMaximumHp) {
                                autoTargetSlot = static_cast<std::size_t>(oldResolved);
                                autoTargetValid = true;
                            }
                        }
                    }
                }
            } else {
                pendingAutoTargetMob = 0;
                pendingAutoTargetSlot = kInvalidHudSlot;
                pendingAutoTargetSamples = 0;
                ++invalidAutoTargetSamples;
                if (lastAutoTargetValid && !targetUncertainPublished) {
                    targetUncertainPublished = true;
                }

                if (lastAutoTargetValid &&
                    invalidAutoTargetSamples < settings.targetReleaseSamples) {
                    const int oldResolved = FindSlotForMob(states, lastAutoTargetMob);
                    if (oldResolved >= 0) {
                        const auto& oldState = states[static_cast<std::size_t>(oldResolved)];
                        if (oldState.valid && oldState.currentHp > 0.0f &&
                            oldState.maximumHp >= settings.minimumTargetMaximumHp) {
                            autoTargetSlot = static_cast<std::size_t>(oldResolved);
                            autoTargetValid = true;
                        }
                    }
                }

                if (!autoTargetValid && lastAutoTargetValid) {
                    Log(L"AUTO TARGET CLEARED source=dual-consensus-final "
                        L"previous-mob=0x%p primary=0x%p secondary=0x%p reason=%ls invalid-samples=%lu",
                        reinterpret_cast<void*>(lastAutoTargetMob),
                        reinterpret_cast<void*>(rawPrimaryTargetMob),
                        reinterpret_cast<void*>(rawSecondaryTargetMob),
                        rejectionReason,
                        invalidAutoTargetSamples);
                    // Clearing target ownership also clears only the target
                    // lane latch. Player HUD readiness remains bound to
                    // the still-active battle/player HUD lifecycle.
                    SetHudHealthPresentationLaneEligible(
                        kTargetHudHealthLane, false);
                    targetHudWasActive = false;
                    lastAutoTargetMob = 0;
                    lastAutoTargetValid = false;
                    targetUncertainPublished = false;
                }
            }

            lastPrimaryTargetRaw = rawPrimaryTargetMob;
            lastSecondaryTargetRaw = rawSecondaryTargetMob;
            lastTargetConsensus = targetConsensus;
        }

        const auto hudStatus = SnapshotHudHealthPresentationTrackerStatus();
        const bool playerHealthHudActive =
            hudStatus.lanes[kPlayerHudHealthLane].presentationLatched;
        const bool targetHealthHudActive =
            hudStatus.lanes[kTargetHudHealthLane].presentationLatched;
        const auto playerHealthHudReadyTick =
            hudStatus.lanes[kPlayerHudHealthLane].readyTick;
        const auto targetHealthHudReadyTick =
            hudStatus.lanes[kTargetHudHealthLane].readyTick;
        if (playerHealthHudActive != playerHudWasActive) {
            Log(L"HEALTH HUD TRACKER player-lane presentation %ls ready-tick=%llu recent-submissions=%ls current-hits=%llu max-hits=%llu",
                playerHealthHudActive ? L"latched" : L"not-latched",
                static_cast<unsigned long long>(playerHealthHudReadyTick),
                hudStatus.lanes[kPlayerHudHealthLane].submissionActive
                    ? L"active" : L"inactive",
                static_cast<unsigned long long>(
                    hudStatus.lanes[kPlayerHudHealthLane].currentHitsSinceReset),
                static_cast<unsigned long long>(
                    hudStatus.lanes[kPlayerHudHealthLane].maximumHitsSinceReset));
            playerHudWasActive = playerHealthHudActive;
        }
        if (targetHealthHudActive != targetHudWasActive) {
            Log(L"HEALTH HUD TRACKER target-lane presentation %ls ready-tick=%llu recent-submissions=%ls current-hits=%llu max-hits=%llu",
                targetHealthHudActive ? L"latched" : L"not-latched",
                static_cast<unsigned long long>(targetHealthHudReadyTick),
                hudStatus.lanes[kTargetHudHealthLane].submissionActive
                    ? L"active" : L"inactive",
                static_cast<unsigned long long>(
                    hudStatus.lanes[kTargetHudHealthLane].currentHitsSinceReset),
                static_cast<unsigned long long>(
                    hudStatus.lanes[kTargetHudHealthLane].maximumHitsSinceReset));
            targetHudWasActive = targetHealthHudActive;
        }


        if (now - lastHeartbeatTick >= kHeartbeatIntervalMs) {
            Log(L"HealthScale heartbeat: core=0x%p fighters=%zu target=%ls slot=%zu",
                reinterpret_cast<void*>(core), activeCount,
                autoTargetValid ? L"locked" : L"none",
                autoTargetValid ? autoTargetSlot : kInvalidHudSlot);
            lastHeartbeatTick = now;
        }

        Sleep(kPollIntervalMs);
    }
}
} // namespace hs

namespace hs {
HealthRuntimeStatus SnapshotHealthRuntimeStatus() noexcept {
    return HealthRuntimeStatus{
        gHealthRuntimeRunning.load(std::memory_order_acquire),
        gHealthWritesEnabled.load(std::memory_order_acquire)
    };
}
}

