using System;
using System.CommandLine;
using TestStandCLI.Logging;
using TestStandCLI.Services;

namespace TestStandCLI;

internal class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var verboseOption = new Option<bool>("--verbose", "-v"){ Description = "Enable verbose (debug) logging"};

        var rootCommand = new RootCommand("TestStandCLI");

        var analyzeCommand = new Command("analyze", "Analyze sequence files in directory");
        analyzeCommand.Add(verboseOption);
        var analyzeDirectoryArg = new Argument<string>("directory") { Description = "Directory where analysis will execute" };
        var junitReportOption = new Option<string>("--junit-report") { Description = "Output path for JUnit XML report" };
        analyzeCommand.Add(analyzeDirectoryArg);
        analyzeCommand.Add(junitReportOption);
        analyzeCommand.SetAction(parseResult =>
        {
            ConsoleLogger.Verbose = parseResult.GetValue(verboseOption);
            var directory = parseResult.GetValue(analyzeDirectoryArg);
            var junitReportPath = parseResult.GetValue(junitReportOption);
            ConsoleLogger.Information("Analyzing sequences in directory: {0}", directory);
            SequenceAnalyzerService.AnalyzeDirectory(directory, junitReportPath);
            Environment.Exit(Environment.ExitCode);
        });
        rootCommand.Add(analyzeCommand);

        return rootCommand.Parse(args).Invoke();
    }
}
