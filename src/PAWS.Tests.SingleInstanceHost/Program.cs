// Test-only helper executable spawned by PAWS.Tests's SingleInstanceGuardTests (a cross-process
// regression test). Not part of the shipping app. Two modes, matching what a real second PAWS launch
// does: "primary" becomes the single instance and waits to be activated; "secondary" tries once and
// reports which it became.
using System;
using System.Threading;
using PAWS;

if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: PAWS.Tests.SingleInstanceHost <primary|secondary>");
    return 2;
}

return args[0] switch
{
    "primary" => RunPrimary(),
    "secondary" => RunSecondary(),
    _ => Unknown(args[0]),
};

static int RunPrimary()
{
    if (!SingleInstanceGuard.TryBecomePrimary())
    {
        Console.WriteLine("SECONDARY");
        return 1;
    }

    Console.WriteLine("PRIMARY");

    var activated = new ManualResetEventSlim(false);
    SingleInstanceGuard.ListenForActivation(() => activated.Set());

    var wasActivated = activated.Wait(TimeSpan.FromSeconds(15));
    Console.WriteLine(wasActivated ? "ACTIVATED" : "TIMED_OUT");
    return wasActivated ? 0 : 1;
}

static int RunSecondary()
{
    Console.WriteLine(SingleInstanceGuard.TryBecomePrimary() ? "PRIMARY" : "SECONDARY");
    return 0;
}

static int Unknown(string mode)
{
    Console.Error.WriteLine($"Unknown mode '{mode}'.");
    return 2;
}
