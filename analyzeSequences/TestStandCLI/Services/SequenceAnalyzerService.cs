using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using NationalInstruments.TestStand.Interop.API;
using NationalInstruments.TestStand.Interop.SequenceAnalyzer;
using TestStandCLI.Logging;
using Thread = System.Threading.Thread;
using Timer = System.Windows.Forms.Timer;

namespace TestStandCLI.Services;

public static class SequenceAnalyzerService
{
    private static Engine _engine;
    private static IAnalyzer _analyzer;
    private static Project _project;
    private static bool _analysisComplete;
    private static Timer _messageUpdateTimer;
    private static int _numberOfFilesAnalyzed = 0;


    public static void AnalyzeDirectory(string analysisDirectory, string junitReportPath = null)
    {
        try
        {
            ConsoleLogger.Debug("TestStand Analyzer");

            // The TestStand engine resolves relative paths against its own search directories,
            // not the working directory, so pass it absolute paths only.
            analysisDirectory = Path.GetFullPath(analysisDirectory);
            if (!string.IsNullOrEmpty(junitReportPath))
                junitReportPath = Path.GetFullPath(junitReportPath);

            if (!Directory.Exists(analysisDirectory))
            {
                ConsoleLogger.Error("Error: Directory '{0}' does not exist.", analysisDirectory);
                Environment.ExitCode = 1;
                return;
            }

            // Initialize TestStand engine
            InitializeTestStandEngine();

            // Analyze sequences
            AnalyzeSequences(analysisDirectory);

            // Display analysis results and collect messages
            var messages = DisplayAnalysisResults();

            // Generate JUnit XML report if requested
            if (!string.IsNullOrEmpty(junitReportPath))
            {
                JUnitReportService.GenerateReport(junitReportPath, messages);
            }
            

            ConsoleLogger.Information("Analysis completed successfully.");
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Error: {0}", ex.Message);
            Environment.ExitCode = 1;
        }
        finally
        {
            Cleanup();
        }
    }

 
    private static void InitializeTestStandEngine()
    {
        ConsoleLogger.Debug("Initializing TestStand engine...");
        // Create and initialize analyzer
        _analyzer = new AnalyzerClass();
        _analyzer.Initialize();
        _analyzer.CacheSequenceFiles = false;
        
        ConsoleLogger.Debug("TestStand engine initialized ...");

        // Get engine from analyzer
        _engine = _analyzer.Engine;
        //
        //// Acquire license
        _engine.AcquireLicense(ApplicationLicenses.ApplicationLicense_SequenceEditor,AcquireLicenseOptions.AcquireLicenseOption_SuppressStartupDialog);
        ConsoleLogger.Debug("TestStand engine license acquired ...");

        //
        //// Load type palette files
        try
        {
            _engine.LoadTypePaletteFilesEx(TypeConflictHandlerTypes.ConflictHandler_UseGlobalType);
        }
        catch (COMException ex)
        {
            if (ex.ErrorCode != -17902) // Ignore specific error code
                throw;
        }

        // Create a new project
        _project = _analyzer.NewProject();

        // Initialize timer like the original code
        _messageUpdateTimer = new Timer();
        _messageUpdateTimer.Interval = 50; // 500ms like original
        _messageUpdateTimer.Tick += MessageUpdateTimer_Tick;

        ConsoleLogger.Debug("TestStand engine initialized successfully.");
    }


    private static void MessageUpdateTimer_Tick(object sender, EventArgs e)
    {
        if (!_project.AnalysisStatus.IsAnalyzing)
        {
            _messageUpdateTimer.Stop();
            _analysisComplete = true;
            ConsoleLogger.Information("Analysis completed.");
        }
        else
        {
            if (_numberOfFilesAnalyzed != _project.AnalysisStatus.NumberOfFilesAnalyzed)
            {
                _numberOfFilesAnalyzed = _project.AnalysisStatus.NumberOfFilesAnalyzed;
                ConsoleLogger.Debug("Analyzing... Files: {0}/{1}", _project.AnalysisStatus.NumberOfFilesAnalyzed, _project.AnalysisStatus.NumberOfFilesToAnalyze);
            }
        }
    }


    private static void AnalyzeSequences(string directoryPath)
    {
        ConsoleLogger.Information("Analyzing sequences in: {0}", directoryPath);

        //_engine.SearchDirectories.Insert(directoryPath,0,true);
        // Find all .seq files in the directory
        var sequenceFiles = Directory.GetFiles(directoryPath, "*.seq", SearchOption.AllDirectories);

        if (!sequenceFiles.Any())
        {
            ConsoleLogger.Information("No .seq files found.");
            return;
        }

        ConsoleLogger.Debug("Found {0} sequence files.", sequenceFiles.Length);

        // Add files to the project
        foreach (var filePath in sequenceFiles)
        {
            ConsoleLogger.Debug("  + Adding to project: {0}", Path.GetFileName(filePath));
            _project.FilesToAnalyze = AddFileToArray(_project.FilesToAnalyze, filePath);
        }

        // Start analysis
        ConsoleLogger.Information("Starting analysis...");
        _project.StartAnalysis(0);

        // Start the timer
        _messageUpdateTimer.Start();

        // Wait for analysis to complete using Application.DoEvents() to process timer events
        while (!_analysisComplete)
        {
            Application.DoEvents(); // Process Windows messages including timer events
            Thread.Sleep(50); // Small delay to prevent high CPU usage
        }
        
        //_engine.SearchDirectories.Remove(0);

    }

 
    private static List<AnalysisMessageInfo> DisplayAnalysisResults()
    {
        var collectedMessages = new List<AnalysisMessageInfo>();

        ConsoleLogger.Information("");
        ConsoleLogger.Information("---------- ANALYSIS RESULTS ----------");
        ConsoleLogger.Information("");

        var errorCount = 0;
        var warningCount = 0;
        var infoCount = 0;

        try
        {
            var messagesCount = _project.Messages.Count;

            if (messagesCount == 0)
            {
                ConsoleLogger.Information("No analysis messages found - all sequences are clean!");
                return collectedMessages;
            }

            ConsoleLogger.Information("Total messages: {0}", messagesCount);
            ConsoleLogger.Information("");

            // Collect all messages and sort by severity (Error > Warning > Information)
            var messages = new List<(AnalysisMessage message, int originalIndex)>();
            for (var i = 0; i < messagesCount; i++)
            {
                messages.Add((_project.Messages[i], i));
            }

            // Sort by severity: Error (0) > Warning (1) > Information (2)
            messages.Sort((a, b) => GetSeverityPriority(a.message.Severity).CompareTo(GetSeverityPriority(b.message.Severity)));

            // Display messages in sorted order
            foreach (var (message, originalIndex) in messages)
            {
                var severity = message.Severity;
                var messageText = message.Text;

                var prefix = "";
                var messageSeverity = AnalysisMessageSeverity.Unknown;

                switch (severity)
                {
                    case RuleSeverity.RuleSeverity_Error:
                        errorCount++;
                        prefix = "ERROR   ";
                        messageSeverity = AnalysisMessageSeverity.Error;
                        break;

                    case RuleSeverity.RuleSeverity_Warning:
                        warningCount++;
                        prefix = "WARNING ";
                        messageSeverity = AnalysisMessageSeverity.Warning;
                        break;

                    case RuleSeverity.RuleSeverity_Information:
                        infoCount++;
                        prefix = "INFO    ";
                        messageSeverity = AnalysisMessageSeverity.Information;
                        break;

                    default:
                        prefix = "UNKNOWN ";
                        break;
                }

                ConsoleLogger.Information("{0}{1}", prefix, messageText);

                string filePath = null;
                string locationPath = null;

                try
                {
                    if (message.Locations != null && message.Locations.Count > 0)
                    {
                        var location = message.Locations[0];
                        if (!string.IsNullOrEmpty(location.FilePath))
                        {
                            filePath = location.FilePath;
                            ConsoleLogger.Information("          File: {0}", Path.GetFileName(location.FilePath));
                        }
                        if (!string.IsNullOrEmpty(location.PropertyPathWithNames))
                        {
                            locationPath = location.PropertyPathWithNames;
                            ConsoleLogger.Information("          Location: {0}", location.PropertyPathWithNames);
                        }
                    }
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Error("          (Could not retrieve location info: {0})", ex.Message);
                }

                collectedMessages.Add(new AnalysisMessageInfo
                {
                    Text = messageText,
                    Severity = messageSeverity,
                    FilePath = filePath,
                    Location = locationPath
                });

                ConsoleLogger.Information("");
            }
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Error retrieving analysis results: {0}", ex.Message);
        }

        ConsoleLogger.Information("---------- SUMMARY ----------");
        ConsoleLogger.Information("Errors: {0} | Warnings: {1} | Info: {2} | Total: {3}", errorCount, warningCount, infoCount, errorCount + warningCount + infoCount);
        ConsoleLogger.Information("");

        if (errorCount > 0)
            Environment.ExitCode = 1; 
        else
            Environment.ExitCode = 0;

        return collectedMessages;
    }

    private static int GetSeverityPriority(RuleSeverity severity)
    {
        return severity switch
        {
            RuleSeverity.RuleSeverity_Error => 0,
            RuleSeverity.RuleSeverity_Warning => 1,
            RuleSeverity.RuleSeverity_Information => 2,
            _ => 3
        };
    }


    private static string[] AddFileToArray(string[] existingFiles, string newFile)
    {
        if (existingFiles == null)
            return new[] { newFile };

        var newArray = new string[existingFiles.Length + 1];
        Array.Copy(existingFiles, newArray, existingFiles.Length);
        newArray[existingFiles.Length] = newFile;
        return newArray;
    }


    private static void Cleanup()
    {
        try
        {
            if (_messageUpdateTimer != null)
            {
                _messageUpdateTimer.Stop();
                _messageUpdateTimer.Dispose();
                _messageUpdateTimer = null;
            }
            _engine.UnloadAllModules();
            _engine.ShutDown(true);
            _analyzer.UnloadAllModules();
            
            System.Runtime.InteropServices.Marshal.ReleaseComObject(_project);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(_engine);
            System.Runtime.InteropServices.Marshal.ReleaseComObject(_analyzer);

            if (_project != null) _project = null;

            if (_analyzer != null) _analyzer = null;

            if (_engine != null) _engine = null;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Error during cleanup: {0}", ex.Message);
        }
    }
}