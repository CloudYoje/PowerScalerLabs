#pragma once

#include "PowerScalerProbeAbi.h"

namespace psl::probe
{
    // Reserved for the next gate's allocation-free multi-producer event commits.
    bool TryCommitEvent(ProbeSharedRegion& region, const RawProbeEvent& event) noexcept;
}
