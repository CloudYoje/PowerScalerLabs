using System.Runtime.InteropServices;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.ProbeHost;

internal static class ProbeArchitectureSelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            Require(ProbeProtocol.ProtocolVersion == 1, "Probe protocol version must remain 1.");
            Require(ProbeProtocol.NativeAbiVersion == 1, "Native ABI version must remain 1.");
            Require(Marshal.SizeOf<ProbeInitializationArguments>() == 808, "Managed initialization ABI size changed.");
            Require(ProbeSharedMemory.HeaderSize == 256, "Shared header size changed.");
            Require(ProbeSharedMemory.EventSize == 256, "Native event size changed.");
            Require(ProbeSharedMemory.EventCapacity == 256, "Native event capacity changed.");
            Require(!Enum.GetNames<ProbeState>().Contains("Armed", StringComparer.Ordinal),
                "Foundation protocol must not expose an Armed state.");
            output.WriteLine("Native Causal Probe Foundation self-test passed.");
            output.WriteLine("- managed/native ABI dimensions are fixed");
            output.WriteLine("- probe protocol remains separate from Runtime protocol 8");
            output.WriteLine("- no armed instrumentation state exists");
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
