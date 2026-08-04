#pragma once

#include <cstddef>
#include <cstdint>

namespace psl::probe
{
    inline constexpr std::uint32_t kSharedMagic = 0x50534C50;
    inline constexpr std::uint32_t kInitializationMagic = 0x49534C50;
    inline constexpr std::uint32_t kAbiVersion = 3;
    inline constexpr std::uint32_t kHeaderSize = 256;
    inline constexpr std::uint32_t kEventSize = 256;
    inline constexpr std::uint32_t kEventCapacity = 256;

    enum class NativeState : std::uint32_t
    {
        Created = 1,
        Initializing = 2,
        Ready = 3,
        Inert = 4,
        ShuttingDown = 5,
        SafeToUnload = 6,
        Faulted = 7
    };

    enum class NativeCommand : std::uint32_t
    {
        None = 0,
        Shutdown = 1,
        EmitSyntheticEvent = 2,
        ArmWriteWatch = 3,
        DisarmWatch = 4
    };

    enum class NativeEventType : std::uint32_t
    {
        Synthetic = 1,
        HardwareWriteTrap = 2,
        InstrumentationFault = 3
    };

    enum class NativeAccessType : std::uint32_t
    {
        None = 0,
        Write = 1
    };

#pragma pack(push, 8)
    struct ProbeSharedHeader
    {
        std::uint32_t magic;
        std::uint32_t abi_version;
        std::uint32_t header_size;
        std::uint32_t event_size;
        std::uint32_t capacity;
        std::uint32_t state;
        std::uint32_t host_process_id;
        std::uint32_t game_process_id;
        std::uint64_t nonce_low;
        std::uint64_t nonce_high;
        std::uint64_t qpc_frequency;
        std::uint64_t host_heartbeat_qpc;
        std::uint64_t probe_heartbeat_qpc;
        std::uint64_t probe_heartbeat_sequence;
        std::uint64_t dropped_event_count;
        std::uint32_t active_watchpoint_count;
        std::uint32_t command;
        std::uint64_t command_sequence;
        std::uint64_t command_ack_sequence;
        std::uint64_t event_write_sequence;
        std::uint64_t event_read_sequence;
        std::uint32_t initialization_status;
        std::uint32_t command_result_code;
        std::uint64_t command_trace_session_id;
        std::uint64_t command_watch_id;
        std::uint64_t command_target_address;
        std::uint32_t command_width;
        std::uint32_t command_access_type;
        std::uint32_t command_event_count;
        std::uint32_t command_interval_milliseconds;
        std::uint32_t command_generated_event_count;
        std::uint32_t command_reserved;
        std::uint32_t eligible_thread_count;
        std::uint32_t instrumented_thread_count;
        std::uint32_t exited_thread_count;
        std::uint32_t newly_armed_thread_count;
        std::uint32_t conflict_thread_count;
        std::uint32_t conflict_component;
        std::uint64_t conflict_expected_value;
        std::uint64_t conflict_observed_value;
        std::uint32_t non_owned_change_flags;
        std::uint32_t non_owned_change_thread_id;
        std::uint32_t command_simd_register_0;
        std::uint32_t command_simd_register_1;
        std::uint32_t reserved[4];
    };

    struct RawProbeEvent
    {
        std::uint64_t commit_sequence;
        std::uint64_t sequence;
        std::uint64_t qpc;
        std::uint64_t trace_session_id;
        std::uint64_t watch_id;
        std::uint64_t rip;
        std::uint64_t rsp;
        std::uint64_t rflags;
        // RAX, RBX, RCX, RDX, RSI, RDI, RBP, R8-R15, reserved.
        std::uint64_t registers[16];
        std::uint64_t dr6;
        std::uint64_t dr7;
        std::uint64_t watched_address;
        std::uint64_t access_address;
        std::uint32_t thread_id;
        std::uint32_t event_type;
        std::uint32_t access_width;
        std::uint32_t access_type;
        std::uint32_t simd_register_0;
        std::uint32_t simd_register_1;
        std::uint32_t simd_scalar_bits_0;
        std::uint32_t simd_scalar_bits_1;
    };

    struct ProbeSharedRegion
    {
        ProbeSharedHeader header;
        RawProbeEvent events[kEventCapacity];
    };

    struct ProbeInitializationArguments
    {
        std::uint32_t structure_magic;
        std::uint32_t abi_version;
        std::uint32_t structure_size;
        std::uint32_t host_process_id;
        std::uint32_t game_process_id;
        std::uint32_t reserved;
        std::uint64_t nonce_low;
        std::uint64_t nonce_high;
        wchar_t mapping_name[128];
        wchar_t command_event_name[128];
        wchar_t event_ready_name[128];
    };
#pragma pack(pop)

    static_assert(sizeof(ProbeSharedHeader) == kHeaderSize);
    static_assert(sizeof(RawProbeEvent) == kEventSize);
    static_assert(sizeof(ProbeInitializationArguments) == 808);
    static_assert(sizeof(ProbeSharedRegion) == kHeaderSize + kEventSize * kEventCapacity);
    static_assert(offsetof(ProbeSharedHeader, command_result_code) == 132);
    static_assert(offsetof(ProbeSharedHeader, command_trace_session_id) == 136);
    static_assert(offsetof(ProbeSharedHeader, command_watch_id) == 144);
    static_assert(offsetof(ProbeSharedHeader, command_target_address) == 152);
    static_assert(offsetof(ProbeSharedHeader, command_width) == 160);
    static_assert(offsetof(ProbeSharedHeader, command_access_type) == 164);
    static_assert(offsetof(ProbeSharedHeader, command_event_count) == 168);
    static_assert(offsetof(ProbeSharedHeader, command_interval_milliseconds) == 172);
    static_assert(offsetof(ProbeSharedHeader, command_generated_event_count) == 176);
    static_assert(offsetof(ProbeSharedHeader, eligible_thread_count) == 184);
    static_assert(offsetof(ProbeSharedHeader, conflict_expected_value) == 208);
    static_assert(offsetof(ProbeSharedHeader, non_owned_change_flags) == 224);
    static_assert(offsetof(ProbeSharedHeader, command_simd_register_0) == 232);
    static_assert(offsetof(RawProbeEvent, commit_sequence) == 0);
    static_assert(offsetof(RawProbeEvent, sequence) == 8);
    static_assert(offsetof(RawProbeEvent, qpc) == 16);
    static_assert(offsetof(RawProbeEvent, registers) == 64);
    static_assert(offsetof(RawProbeEvent, thread_id) == 224);
    static_assert(offsetof(RawProbeEvent, simd_register_0) == 240);
}
