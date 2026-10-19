using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Thread = System.Threading.Thread;
using NationalInstruments.TestStand.Interop.AdapterAPI;
using NationalInstruments.TestStand.Interop.API;
using TestStandCLI.Logging;

namespace TestStandCLI.Services;

/// <summary>
/// Reloads the prototype of every LabVIEW step in the sequence files of a directory and saves the files.
/// Existing argument expressions are kept. The prototype is reloaded from the VI path even when the
/// namespace stored in the step no longer matches the VI, for example after a build renamed the library
/// owning the VI. The step then takes the namespace of the VI on disk.
/// </summary>
public static class UpdatePrototypesService
{
    // AdapterKeyNames.FlexLVAdapterKeyName
    private const string LabVIEWAdapterKeyName = "G Flexible VI Adapter";

    private static readonly StepGroups[] StepGroupsToUpdate =
    {
        StepGroups.StepGroup_Setup,
        StepGroups.StepGroup_Main,
        StepGroups.StepGroup_Cleanup
    };

    private static readonly TimeSpan ShutDownTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(120);

    private static Engine _engine;

    public static void UpdateDirectory(string directory, string junitReportPath = null)
    {
        var messages = new List<AnalysisMessageInfo>();
        try
        {
            // The TestStand engine resolves relative paths against its own search directories,
            // not the working directory, so pass it absolute paths only.
            directory = Path.GetFullPath(directory);
            if (!string.IsNullOrEmpty(junitReportPath))
                junitReportPath = Path.GetFullPath(junitReportPath);

            if (!Directory.Exists(directory))
            {
                ConsoleLogger.Error("Error: Directory '{0}' does not exist.", directory);
                messages.Add(new AnalysisMessageInfo { Text = $"Directory '{directory}' does not exist.", Severity = AnalysisMessageSeverity.Error });
                Environment.ExitCode = 1;
                return;
            }

            var sequenceFiles = Directory.GetFiles(directory, "*.seq", SearchOption.AllDirectories);
            if (sequenceFiles.Length == 0)
            {
                ConsoleLogger.Information("No .seq files found.");
                return;
            }

            InitializeTestStandEngine();

            for (var i = 0; i < sequenceFiles.Length; i++)
            {
                ConsoleLogger.Information("Updating: {0} ({1}/{2})", sequenceFiles[i], i + 1, sequenceFiles.Length);
                UpdateSequenceFile(sequenceFiles[i], messages);
                ProcessUIMessages();
            }

            var updated = messages.FindAll(m => m.Severity == AnalysisMessageSeverity.Information).Count;
            var failed = messages.FindAll(m => m.Severity == AnalysisMessageSeverity.Error).Count;
            ConsoleLogger.Information("");
            ConsoleLogger.Information("---------- SUMMARY ----------");
            ConsoleLogger.Information("Sequence files: {0} | Steps updated: {1} | Errors: {2}", sequenceFiles.Length, updated, failed);

            Environment.ExitCode = failed > 0 ? 1 : 0;
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Error: {0}", ex.Message);
            messages.Add(new AnalysisMessageInfo { Text = ex.Message, Severity = AnalysisMessageSeverity.Error });
            Environment.ExitCode = 1;
        }
        finally
        {
            if (!string.IsNullOrEmpty(junitReportPath))
                JUnitReportService.GenerateReport(junitReportPath, messages);
            Cleanup();
        }
    }

    private static void InitializeTestStandEngine()
    {
        ConsoleLogger.Debug("Initializing TestStand engine...");
        _engine = new Engine();
        // Without a UI, the engine messages must be polled and acknowledged, or shutdown never completes
        _engine.UIMessagePollingEnabled = true;
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

        ConsoleLogger.Debug("TestStand engine initialized.");
    }

    private static void UpdateSequenceFile(string path, List<AnalysisMessageInfo> messages)
    {
        var sequenceFile = _engine.GetSequenceFileEx(path, GetSeqFileOptions.GetSeqFile_DoNotRunLoadCallback);
        try
        {
            for (var s = 0; s < sequenceFile.NumSequences; s++)
            {
                var sequence = sequenceFile.GetSequence(s);
                foreach (var stepGroup in StepGroupsToUpdate)
                {
                    for (var i = 0; i < sequence.GetNumSteps(stepGroup); i++)
                        UpdateStep(path, sequence, sequence.GetStep(i, stepGroup), messages);
                }
            }

            sequenceFile.Save(path);
            sequenceFile.UnloadModules();
        }
        finally
        {
            _engine.ReleaseSequenceFileEx(sequenceFile, ReleaseSeqFileOptions.ReleaseSeqFile_UnloadFile);
        }
    }

    private static void UpdateStep(string path, Sequence sequence, Step step, List<AnalysisMessageInfo> messages)
    {
        if (step.AdapterKeyName != LabVIEWAdapterKeyName)
            return;

        // Skipped steps are not updated, as in the LabVIEW implementation
        if (step.GetRunModeEx() == RunModes.RunMode_Skip)
            return;

        var location = $"{sequence.Name}.{step.Name}";
        try
        {
            var module = (LabVIEWModule)step.Module;
            var oldNamespace = module.Namespace;

            step.Module.Unload();
            step.Module.LoadPrototype((int)LoadPrototypeOptions.LoadPrototypeOption_MapExistingParameters);

            var text = $"{location}: prototype updated";
            if (oldNamespace != module.Namespace)
                text += $", namespace changed from '{oldNamespace}' to '{module.Namespace}'";
            ConsoleLogger.Debug("  {0}", text);
            messages.Add(new AnalysisMessageInfo { Text = text, Severity = AnalysisMessageSeverity.Information, FilePath = path, Location = location });
        }
        catch (Exception ex)
        {
            var text = $"{location}: failed to update prototype: {ex.Message}";
            ConsoleLogger.Error("ERROR   {0}", text);
            messages.Add(new AnalysisMessageInfo { Text = text, Severity = AnalysisMessageSeverity.Error, FilePath = path, Location = location });
        }
    }

    /// <summary>
    /// Acknowledges all queued engine messages. Returns true if the shutdown completed.
    /// </summary>
    private static bool ProcessUIMessages()
    {
        var shutDownComplete = false;
        while (!_engine.IsUIMessageQueueEmpty)
        {
            var message = _engine.GetUIMessage();
            ConsoleLogger.Debug("TestStand UI message: {0}", message.Event);
            if (message.Event == UIMessageCodes.UIMsg_ShutDownComplete)
                shutDownComplete = true;
            message.Acknowledge();
            Marshal.ReleaseComObject(message);
        }
        return shutDownComplete;
    }

    private static void Cleanup()
    {
        if (_engine == null)
            return;

        // The sequence files are saved at this point. If shutting down the engine hangs,
        // exit anyway instead of blocking the workflow.
        var watchdog = new Thread(() =>
        {
            Thread.Sleep(CleanupTimeout);
            ConsoleLogger.Error("TestStand engine did not shut down within {0} s, exiting.", CleanupTimeout.TotalSeconds);
            Console.Out.Flush();
            Environment.Exit(Environment.ExitCode);
        }) { IsBackground = true };
        watchdog.Start();

        try
        {
            ConsoleLogger.Information("Unloading modules...");
            _engine.UnloadAllModules();
            ProcessUIMessages();

            ConsoleLogger.Information("Shutting down TestStand engine...");
            _engine.ShutDown(true);
            var timer = Stopwatch.StartNew();
            while (!ProcessUIMessages())
            {
                if (timer.Elapsed > ShutDownTimeout)
                {
                    ConsoleLogger.Error("No shutdown complete message from TestStand within {0} s.", ShutDownTimeout.TotalSeconds);
                    break;
                }
                Thread.Sleep(50);
            }

            Marshal.ReleaseComObject(_engine);
            _engine = null;
            ConsoleLogger.Information("TestStand engine shut down.");
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Error during cleanup: {0}", ex.Message);
        }
    }
}
