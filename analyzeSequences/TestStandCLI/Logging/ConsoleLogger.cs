using System;

namespace TestStandCLI.Logging;

/// <summary>
/// Custom logger that wraps Console.WriteLine and respects a verbose flag.
/// When verbose is false, only Information and Error are written; Debug is suppressed.
/// When verbose is true, Debug, Information, and Error are all written.
/// </summary>
public static class ConsoleLogger
{
    private static bool _verbose;

    /// <summary>
    /// Gets or sets whether debug messages are written. Set from command-line --verbose / -v.
    /// </summary>
    public static bool Verbose
    {
        get => _verbose;
        set => _verbose = value;
    }

    /// <summary>
    /// Writes a debug message only when Verbose is true.
    /// </summary>
    public static void Debug(string format, params object[] args)
    {
        if (!_verbose) return;
        Console.WriteLine(format, args);
    }

    /// <summary>
    /// Writes a debug message only when Verbose is true (no format args).
    /// </summary>
    public static void Debug(string message)
    {
        if (!_verbose) return;
        Console.WriteLine(message);
    }

    /// <summary>
    /// Writes an information message (always shown).
    /// </summary>
    public static void Information(string format, params object[] args)
    {
        Console.WriteLine(format, args);
    }

    /// <summary>
    /// Writes an information message (always shown, no format args).
    /// </summary>
    public static void Information(string message)
    {
        Console.WriteLine(message);
    }

    /// <summary>
    /// Writes an error message (always shown).
    /// </summary>
    public static void Error(string format, params object[] args)
    {
        Console.WriteLine(format, args);
    }

    /// <summary>
    /// Writes an error message (always shown, no format args).
    /// </summary>
    public static void Error(string message)
    {
        Console.WriteLine(message);
    }
}
