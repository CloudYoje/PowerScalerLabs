using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using PowerScalerLabs.Protocol;

namespace PowerScalerLabs.Runtime;

internal static class RuntimeArchitectureSelfTest
{
    internal static int Run(TextWriter output)
    {
        try
        {
            Require(RuntimeProtocol.ProtocolVersion == 7, "Protocol version must be 7.");
            Require(TelemetryComparisonPolicy.Changed(10.0f, 9.99f),
                "Compressed-scale policy must detect a 0.01 health change.");
            Require(TelemetryComparisonPolicy.NumericEquivalent(10.0, 10.0000001),
                "Compressed-scale policy must suppress sub-tolerance floating-point noise.");

            IReadOnlyList<AddressProvenanceEntry> provenance = AddressProvenanceCatalog.Entries;
            Require(provenance.Count >= 8, "Address provenance registry is incomplete.");
            Require(provenance.Select(entry => entry.Key).Distinct(StringComparer.Ordinal).Count() == provenance.Count,
                "Address provenance keys must be unique.");
            foreach (uint offset in new[]
            {
                RuntimeProtocol.CurrentHealthOffset,
                RuntimeProtocol.MaximumHealthOffset,
                RuntimeProtocol.CurrentKiOffset,
                RuntimeProtocol.MaximumKiOffset,
                RuntimeProtocol.CurrentStaminaOffset,
                RuntimeProtocol.MaximumStaminaOffset
            })
            {
                string key = AddressProvenanceCatalog.KeyForOffset(offset);
                Require(provenance.Any(entry => entry.Key == key && entry.OffsetOrRva == offset),
                    $"Focused offset +0x{offset:X} lacks provenance.");
            }

            HashSet<string> allowedImports = new(StringComparer.Ordinal)
            {
                "OpenProcess",
                "ReadProcessMemory",
                "VirtualQueryEx",
                "CreateToolhelp32Snapshot",
                "Module32FirstW",
                "Module32NextW"
            };
            string[] importedMethods = typeof(NativeMethods)
                .GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(method => method.GetCustomAttribute<DllImportAttribute>() is not null)
                .Select(method => method.Name)
                .ToArray();
            Require(importedMethods.All(allowedImports.Contains),
                "A native import outside the approved query/read/module-enumeration set was found.");

            FighterIdentityMessage identity = new(
                "self-test:battle-1:slot-0:generation-1",
                "self-test",
                1,
                1,
                0,
                0x100000,
                0x140000000,
                DateTimeOffset.UnixEpoch,
                1);
            FighterSnapshot fighter = new(0, 0x100000, 9.99f, 10.0f, DateTimeOffset.UnixEpoch, 1, identity);
            string json = JsonSerializer.Serialize(fighter);
            FighterSnapshot? roundTrip = JsonSerializer.Deserialize<FighterSnapshot>(json);
            Require(roundTrip?.Identity.IdentityKey == identity.IdentityKey,
                "Fighter generation identity did not survive protocol serialization.");

            output.WriteLine("Runtime Access Architecture Gate 0 self-test passed.");
            output.WriteLine("- compressed 0.01 changes are observable");
            output.WriteLine("- address provenance registry is complete and unique");
            output.WriteLine("- native imports remain query/read/module-enumeration only");
            output.WriteLine("- fighter generation identity survives protocol serialization");
            return 0;
        }
        catch (Exception exception)
        {
            output.WriteLine($"Runtime Access Architecture Gate 0 self-test failed: {exception.Message}");
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
