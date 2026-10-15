using System;
using System.Collections.Generic;
using System.IO;
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
    private static int _numberOfFilesAnalyzed;

    public static void AnalyzeDirectory(string analysisDirectory, string junitReportPath = null)
    {
        try
        {
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

            InitializeTestStandEngine();
            AnalyzeSequences(analysisDirectory);
            var messages = DisplayAnalysisResults();

            if (!string.IsNullOrEmpty(junitReportPath))
                JUnitReportService.GenerateReport(junitReportPath, messages);

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
        _analyzer = new AnalyzerClass();
        _analyzer.Initialize();
        _analyzer.CacheSequenceFiles = false;

        _engine = _analyzer.Engine;
        _engine.AcquireLicense(ApplicationLicenses.ApplicationLicense_SequenceEditor, AcquireLicenseOptions.AcquireLicenseOption_SuppressStartupDialog);
        ConsoleLogger.Debug("TestStand engine license acquired.");

        try
        {
            _engine.LoadTypePaletteFilesEx(TypeConflictHandlerTypes.ConflictHandler_UseGlobalType);
        }
        catch (COMException ex)
        {
            if (ex.ErrorCode != -17902)
                throw;
        }

        _project = _analyzer.NewProject();

        _messageUpdateTimer = new Timer();
        _messageUpdateTimer.Interval = 50;
        _messageUpdateTimer.Tick += MessageUpdateTimer_Tick;

        ConsoleLogger.Debug("TestStand engine initialized.");
    }

    private static void MessageUpdateTimer_Tick(object sender, EventArgs e)
    {
        if (!_project.AnalysisStatus.IsAnalyzing)
        {
            _messageUpdateTimer.Stop();
            _analysisComplete = true;
            ConsoleLogger.Information("Analysis completed.");
        }
        else if (_numberOfFilesAnalyzed != _project.AnalysisStatus.NumberOfFilesAnalyzed)
        {
            _numberOfFilesAnalyzed = _project.AnalysisStatus.NumberOfFilesAnalyzed;
            ConsoleLogger.Debug("Analyzing... Files: {0}/{1}", _project.AnalysisStatus.NumberOfFilesAnalyzed, _project.AnalysisStatus.NumberOfFilesToAnalyze);
        }
    }

    private static void AnalyzeSequences(string directoryPath)
    {
        ConsoleLogger.Information("Analyzing sequences in: {0}", directoryPath);

        var sequenceFiles = Directory.GetFiles(directoryPath, "*.seq", SearchOption.AllDirectories);
        if (sequenceFiles.Length == 0)
        {
            ConsoleLogger.Information("No .seq files found.");
            return;
        }

        foreach (var filePath in sequenceFiles)
            ConsoleLogger.Debug("  + Adding to project: {0}", Path.GetFileName(filePath));
        _project.FilesToAnalyze = sequenceFiles;

        ConsoleLogger.Information("Starting analysis...");
        _project.StartAnalysis(0);
        _messageUpdateTimer.Start();

        // Pump Windows messages so the timer can poll the analysis status
        while (!_analysisComplete)
        {
            Application.DoEvents();
            Thread.Sleep(50);
        }
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

            var messages = new List<AnalysisMessage>();
            for (var i = 0; i < messagesCount; i++)
                messages.Add(_project.Messages[i]);

            // Sort by severity: Error > Warning > Information
            messages.Sort((a, b) => GetSeverityPriority(a.Severity).CompareTo(GetSeverityPriority(b.Severity)));

            foreach (var message in messages)
            {
                var prefix = "UNKNOWN ";
                var messageSeverity = AnalysisMessageSeverity.Unknown;

                switch (message.Severity)
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
                }

                ConsoleLogger.Information("{0}{1}", prefix, message.Text);

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
                    Text = message.Text,
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

        Environment.ExitCode = errorCount > 0 ? 1 : 0;
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

    private static void Cleanup()
    {
        try
        {
            _messageUpdateTimer?.Stop();
            _messageUpdateTimer?.Dispose();
            _messageUpdateTimer = null;

            if (_engine != null)
            {
                _engine.UnloadAllModules();
                _engine.ShutDown(true);
            }
            _analyzer?.UnloadAllModules();

            if (_project != null) Marshal.ReleaseComObject(_project);
            if (_engine != null) Marshal.ReleaseComObject(_engine);
            if (_analyzer != null) Marshal.ReleaseComObject(_analyzer);

            _project = null;
            _engine = null;
            _analyzer = null;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Error during cleanup: {0}", ex.Message);
        }
    }
}
