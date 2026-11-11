using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using NationalInstruments.TestStand.Interop.AdapterAPI;
using NationalInstruments.TestStand.Interop.API;
using TestStandCLI.Logging;

namespace TestStandCLI.Services;

/// <summary>
/// Relinks the LabVIEW steps of the sequence files in a directory from the source VIs of a LabVIEW library
/// to the same VIs inside a packed project library (PPL) built from that library, and saves the files.
///
/// A step is relinked when its VI path resolves to a file below the library directory. The path of the VI
/// relative to the library directory is kept, and the library directory is replaced by the PPL, so
/// API\Drive\Connect.vi becomes API\139Z9725 API.lvlibp\Drive\Connect.vi. This matches how LabVIEW lays
/// out the VIs inside a PPL when all members of the library are located below the library directory.
/// The library directory and the PPL path are given relative to each sequence file, so a sequence file
/// must be staged at the same position relative to the PPL as it has to the library in the source tree.
///
/// After the path is changed, the prototype is reloaded with the existing arguments mapped, which also
/// updates the namespace of the step to the namespace of the VI in the PPL (Library.lvlibp:...).
/// </summary>
public static class RelinkToPPLService
{
    // AdapterKeyNames.FlexLVAdapterKeyName
    private const string LabVIEWAdapterKeyName = "G Flexible VI Adapter";

    private static readonly StepGroups[] StepGroupsToRelink =
    {
        StepGroups.StepGroup_Setup,
        StepGroups.StepGroup_Main,
        StepGroups.StepGroup_Cleanup
    };

    private static Engine _engine;

    public static void RelinkDirectory(string directory, string libraryDir, string pplPath, bool dryRun, string junitReportPath = null)
    {
        var messages = new List<AnalysisMessageInfo>();
        try
        {
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
                ConsoleLogger.Information("Relinking: {0} ({1}/{2})", sequenceFiles[i], i + 1, sequenceFiles.Length);
                RelinkSequenceFile(sequenceFiles[i], libraryDir, pplPath, dryRun, messages);
                ProcessUIMessages();
            }

            var relinked = messages.FindAll(m => m.Severity == AnalysisMessageSeverity.Information).Count;
            var warnings = messages.FindAll(m => m.Severity == AnalysisMessageSeverity.Warning).Count;
            var failed = messages.FindAll(m => m.Severity == AnalysisMessageSeverity.Error).Count;
            ConsoleLogger.Information("");
            ConsoleLogger.Information("---------- SUMMARY ----------");
            ConsoleLogger.Information("Sequence files: {0} | Steps relinked: {1} | Warnings: {2} | Errors: {3}", sequenceFiles.Length, relinked, warnings, failed);

            if (!dryRun && relinked == 0 && failed == 0)
            {
                var text = $"No LabVIEW step calls a VI below '{libraryDir}'.";
                ConsoleLogger.Error("ERROR   {0}", text);
                messages.Add(new AnalysisMessageInfo { Text = text, Severity = AnalysisMessageSeverity.Error });
                failed++;
            }

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
        // Without a UI, the engine messages must be polled and acknowledged
        _engine.UIMessagePollingEnabled = true;
        _engine.AcquireLicense(ApplicationLicenses.ApplicationLicense_SequenceEditor, AcquireLicenseOptions.AcquireLicenseOption_SuppressStartupDialog);

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

    private static string ResolveAgainst(string baseDir, string path)
    {
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(baseDir, path));
    }

    private static void RelinkSequenceFile(string path, string libraryDir, string pplPath, bool dryRun, List<AnalysisMessageInfo> messages)
    {
        var sequenceDir = Path.GetDirectoryName(path);
        var libraryDirFull = ResolveAgainst(sequenceDir, libraryDir).TrimEnd('\\') + "\\";
        var pplFull = ResolveAgainst(sequenceDir, pplPath);
        ConsoleLogger.Information("  Library directory: {0}", libraryDirFull);
        ConsoleLogger.Information("  PPL: {0}", pplFull);

        if (!dryRun && !File.Exists(pplFull))
        {
            var text = $"PPL '{pplFull}' does not exist.";
            ConsoleLogger.Error("ERROR   {0}", text);
            messages.Add(new AnalysisMessageInfo { Text = text, Severity = AnalysisMessageSeverity.Error, FilePath = path });
            return;
        }

        var sequenceFile = _engine.GetSequenceFileEx(path, GetSeqFileOptions.GetSeqFile_DoNotRunLoadCallback);
        try
        {
            for (var s = 0; s < sequenceFile.NumSequences; s++)
            {
                var sequence = sequenceFile.GetSequence(s);
                foreach (var stepGroup in StepGroupsToRelink)
                {
                    for (var i = 0; i < sequence.GetNumSteps(stepGroup); i++)
                        RelinkStep(path, sequenceDir, libraryDirFull, pplFull, dryRun, sequence, sequence.GetStep(i, stepGroup), messages);
                }
            }

            if (!dryRun)
                sequenceFile.Save(path);
            sequenceFile.UnloadModules();
        }
        finally
        {
            _engine.ReleaseSequenceFileEx(sequenceFile, ReleaseSeqFileOptions.ReleaseSeqFile_UnloadFile);
        }
    }

    private static void RelinkStep(string path, string sequenceDir, string libraryDirFull, string pplFull, bool dryRun,
        Sequence sequence, Step step, List<AnalysisMessageInfo> messages)
    {
        if (step.AdapterKeyName != LabVIEWAdapterKeyName)
            return;

        var location = $"{sequence.Name}.{step.Name}";
        var module = (LabVIEWModule)step.Module;
        var viPath = module.VIPath;
        var skipped = step.GetRunModeEx() == RunModes.RunMode_Skip;

        if (string.IsNullOrEmpty(viPath))
            return;

        var viFull = ResolveAgainst(sequenceDir, viPath);
        if (!viFull.StartsWith(libraryDirFull, StringComparison.OrdinalIgnoreCase))
        {
            ConsoleLogger.Information("  {0}: '{1}' is not below the library directory, not relinked", location, viPath);
            return;
        }

        var relativeToLibrary = viFull.Substring(libraryDirFull.Length);
        var newPath = Path.GetRelativePath(sequenceDir, Path.Combine(pplFull, relativeToLibrary));

        if (dryRun)
        {
            ConsoleLogger.Information("  {0}: '{1}' -> '{2}' (project '{3}', namespace '{4}'{5})",
                location, viPath, newPath, module.ProjectPath, module.Namespace, skipped ? ", skipped" : "");
            return;
        }

        try
        {
            var oldNamespace = module.Namespace;
            step.Module.Unload();
            // The VI is called from the PPL, not in the context of the source project
            module.ProjectPath = "";
            module.VIPath = newPath;
            step.Module.LoadPrototype((int)LoadPrototypeOptions.LoadPrototypeOption_MapExistingParameters);

            var text = $"{location}: relinked '{viPath}' -> '{newPath}', namespace '{oldNamespace}' -> '{module.Namespace}'";
            ConsoleLogger.Information("  {0}", text);
            messages.Add(new AnalysisMessageInfo { Text = text, Severity = AnalysisMessageSeverity.Information, FilePath = path, Location = location });
        }
        catch (Exception ex)
        {
            // Skipped steps do not run, so a failure to load them does not fail the build
            var text = $"{location}: failed to relink '{viPath}' -> '{newPath}': {ex.Message}";
            ConsoleLogger.Error("{0}   {1}", skipped ? "WARNING" : "ERROR", text);
            messages.Add(new AnalysisMessageInfo
            {
                Text = text,
                Severity = skipped ? AnalysisMessageSeverity.Warning : AnalysisMessageSeverity.Error,
                FilePath = path,
                Location = location
            });
        }
    }

    private static void ProcessUIMessages()
    {
        while (!_engine.IsUIMessageQueueEmpty)
        {
            var message = _engine.GetUIMessage();
            ConsoleLogger.Debug("TestStand UI message: {0}", message.Event);
            message.Acknowledge();
            Marshal.ReleaseComObject(message);
        }
    }

    private static void Cleanup()
    {
        if (_engine == null)
            return;

        try
        {
            ConsoleLogger.Information("Unloading modules...");
            _engine.UnloadAllModules();
            ProcessUIMessages();
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Error during cleanup: {0}", ex.Message);
        }
    }
}
