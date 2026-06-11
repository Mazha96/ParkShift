using System;
class Program
{
    static int Main()
    {
        Console.WriteLine("ParkShift Core Test Harness (mono)");
        int fails = ParkShift.Core.Testing.CoreTestSuite.RunAll(Console.WriteLine);
        if (fails == 0) Playtest.Run();
        return fails;
    }
}
