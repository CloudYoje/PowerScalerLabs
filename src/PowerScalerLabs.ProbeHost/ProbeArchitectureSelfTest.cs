using System.Runtime.InteropServices;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.ProbeHost;

internal static class ProbeArchitectureSelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            Require(ProbeProtocol.ProtocolVersion == 2, "Probe protocol version must be 2.");
            Require(ProbeProtocol.NativeAbiVersion == 2, "Native ABI version must be 2.");
            Require(Marshal.SizeOf<ProbeInitializationArguments>() == 808, "Managed initialization ABI size changed.");
            Require(ProbeSharedMemory.HeaderSize == 256, "Shared header size changed.");
            Require(ProbeSharedMemory.EventSize == 256, "Native event size changed.");
            Require(ProbeSharedMemory.EventCapacity == 256, "Native event capacity changed.");
            Require(ProbeSharedMemory.Offset.CommandTraceSessionId == 136, "Trace-session mailbox offset changed.");
            Require(ProbeSharedMemory.Offset.CommandWatchId == 144, "Watch-ID mailbox offset changed.");
            Require(ProbeSharedMemory.Offset.CommandTargetAddress == 152, "Address mailbox offset changed.");
            Require(ProbeSharedMemory.Offset.CommandGeneratedEventCount == 176, "Native result mailbox offset changed.");
            Require(!Enum.GetNames<ProbeState>().Contains("Armed", StringComparer.Ordinal),
                "Foundation protocol must not expose an Armed state.");
            output.WriteLine("Native Causal Trace Transport self-test passed.");
            output.WriteLine("- managed/native ABI dimensions are fixed");
            output.WriteLine("- probe protocol remains separate from Runtime protocol 8");
            output.WriteLine("- ABI 2 mailbox offsets and 256-slot event ring are fixed");
            output.WriteLine("- instrumentation state remains separate from the Probe lifecycle enum");
            return 0;
        }
        catch (Exception exception)
        {
            output.WriteLine($"Native Causal Probe Foundation self-test failed: {exception.Message}");
            return 1;
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
