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

        var updateCommand = new Command("update-prototypes", "Reload the prototypes of all LabVIEW steps in sequence files in directory and save the files");
        updateCommand.Add(verboseOption);
        var updateDirectoryArg = new Argument<string>("directory") { Description = "Directory with sequence files to update" };
        var updateJunitReportOption = new Option<string>("--junit-report") { Description = "Output path for JUnit XML report" };
        updateCommand.Add(updateDirectoryArg);
        updateCommand.Add(updateJunitReportOption);
        updateCommand.SetAction(parseResult =>
        {
            ConsoleLogger.Verbose = parseResult.GetValue(verboseOption);
            var directory = parseResult.GetValue(updateDirectoryArg);
            var junitReportPath = parseResult.GetValue(updateJunitReportOption);
            ConsoleLogger.Information("Updating step prototypes in directory: {0}", directory);
            UpdatePrototypesService.UpdateDirectory(directory, junitReportPath);
            UpdatePrototypesService.Exit();
        });
        rootCommand.Add(updateCommand);

        return rootCommand.Parse(args).Invoke();
    }
}
