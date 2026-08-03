#pragma once

#include <Windows.h>

#include "PowerScalerProbeAbi.h"

namespace psl::probe
{
    bool TryCommitEvent(ProbeSharedRegion& region, HANDLE event_ready, const RawProbeEvent& event) noexcept;
}
