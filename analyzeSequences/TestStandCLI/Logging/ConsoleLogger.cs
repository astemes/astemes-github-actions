using System;

namespace TestStandCLI.Logging;

/// <summary>
/// Writes to the console. Debug messages are only written when Verbose is set.
/// </summary>
public static class ConsoleLogger
{
    public static bool Verbose { get; set; }

    public static void Debug(string format, params object[] args)
    {
        if (Verbose)
            Console.WriteLine(format, args);
    }

    public static void Information(string format, params object[] args) => Console.WriteLine(format, args);

    public static void Error(string format, params object[] args) => Console.WriteLine(format, args);
}
