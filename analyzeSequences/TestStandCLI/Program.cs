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

        var relinkCommand = new Command("relink-to-ppl", "Relink the LabVIEW steps calling VIs of a library to the same VIs in a packed project library built from it, and save the files");
        relinkCommand.Add(verboseOption);
        var relinkDirectoryArg = new Argument<string>("directory") { Description = "Directory with sequence files to relink" };
        var libraryDirOption = new Option<string>("--library-dir") { Description = "Directory of the source library, relative to each sequence file or absolute", Required = true };
        var pplOption = new Option<string>("--ppl") { Description = "Path to the packed project library, relative to each sequence file or absolute", Required = true };
        var dryRunOption = new Option<bool>("--dry-run") { Description = "List the steps that would be relinked without changing the files" };
        var relinkJunitReportOption = new Option<string>("--junit-report") { Description = "Output path for JUnit XML report" };
        relinkCommand.Add(relinkDirectoryArg);
        relinkCommand.Add(libraryDirOption);
        relinkCommand.Add(pplOption);
        relinkCommand.Add(dryRunOption);
        relinkCommand.Add(relinkJunitReportOption);
        relinkCommand.SetAction(parseResult =>
        {
            ConsoleLogger.Verbose = parseResult.GetValue(verboseOption);
            var directory = parseResult.GetValue(relinkDirectoryArg);
            ConsoleLogger.Information("Relinking steps to PPL in directory: {0}", directory);
            RelinkToPPLService.RelinkDirectory(
                directory,
                parseResult.GetValue(libraryDirOption),
                parseResult.GetValue(pplOption),
                parseResult.GetValue(dryRunOption),
                parseResult.GetValue(relinkJunitReportOption));
            UpdatePrototypesService.Exit();
        });
        rootCommand.Add(relinkCommand);

        return rootCommand.Parse(args).Invoke();
    }
}
