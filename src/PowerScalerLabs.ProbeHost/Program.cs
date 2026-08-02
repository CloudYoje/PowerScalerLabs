using PowerScalerLabs.ProbeHost;

if (args.Any(value => string.Equals(value, "--architecture-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return ProbeArchitectureSelfTest.Run(Console.Out);
}

const string mutexName = "Local\\PowerScalerLabs.ProbeHost.CausalResearchGate";
using Mutex mutex = new(initiallyOwned: true, mutexName, out bool createdNew);
if (!createdNew)
{
    return 2;
}

using ProbeHostService service = new();
return await service.RunAsync().ConfigureAwait(false);
