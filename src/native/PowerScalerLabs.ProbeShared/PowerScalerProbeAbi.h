#pragma once

#include <cstdint>

namespace psl::probe
{
    inline constexpr std::uint32_t kSharedMagic = 0x50534C50;
    inline constexpr std::uint32_t kInitializationMagic = 0x49534C50;
    inline constexpr std::uint32_t kAbiVersion = 1;
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
        Shutdown = 1
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
        std::uint32_t reserved[31];
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
        std::uint64_t registers[16];
        std::uint64_t dr6;
        std::uint64_t dr7;
        std::uint64_t watched_address;
        std::uint64_t access_address;
        std::uint32_t thread_id;
        std::uint32_t event_type;
        std::uint32_t access_width;
        std::uint32_t access_type;
        std::uint8_t reserved[16];
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
}
