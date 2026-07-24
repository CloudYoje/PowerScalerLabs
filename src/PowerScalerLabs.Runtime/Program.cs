using PowerScalerLabs.Runtime;

if (args.Any(argument => string.Equals(argument, "--architecture-self-test", StringComparison.OrdinalIgnoreCase)))
{
    return RuntimeArchitectureSelfTest.Run(Console.Out);
}

const string mutexName = "Local\\PowerScalerLabs.Runtime.CapabilityScannerGate";
using Mutex mutex = new(initiallyOwned: true, mutexName, out bool createdNew);
if (!createdNew)
{
    return 2;
}

RuntimeHost host = new();
return await host.RunAsync().ConfigureAwait(false);
