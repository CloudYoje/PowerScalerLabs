#include "ProbeEvents.h"

namespace psl::probe
{
    bool TryCommitEvent(ProbeSharedRegion&, const RawProbeEvent&) noexcept
    {
        return false;
    }
}
