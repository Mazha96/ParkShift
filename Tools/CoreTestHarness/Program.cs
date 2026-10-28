using System;
class Program
{
    /// Usage: run.ps1 [-Seeds N]   (default 25 seeds per solo/coop sweep)
    static int Main(string[] args)
    {
        int seeds = 25;
        for (int i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--seeds") int.TryParse(args[i + 1], out seeds);

        Console.WriteLine("ParkShift Core Test Harness (mono)");
        int fails = ParkShift.Core.Testing.CoreTestSuite.RunAll(Console.WriteLine);
        if (fails != 0) return fails;
        Playtest.Run();
        // Normal rates, then a stress pass that piles on overlapping incidents.
        return Playtest.RunShipped(seeds) + Playtest.RunShipped(seeds, chaos: 6f);
    }
}
