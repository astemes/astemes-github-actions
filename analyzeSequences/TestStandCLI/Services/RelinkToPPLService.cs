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
/// A step is relinked when its VI path resolves to a file below the library directory. The library directory
/// and the PPL path are given relative to each sequence file, so a sequence file must be staged at the same
/// position relative to the PPL as it has to the library in the source tree. Inside the PPL, a VI keeps its
/// path relative to the directory LabVIEW used as the root of the PPL, which is detected by loading VIs from
/// the PPL (see DetectPPLRoot). API\Drive\Connect.vi becomes API\Lib.lvlibp\Drive\Connect.vi, or
/// API\Lib.lvlibp\API\Drive\Connect.vi when source dependencies outside the library directory are built in.
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

    /// <summary>
    /// Loads the VI of every LabVIEW step that is not skipped, as TestStand does before running it.
    /// Unlike loading the prototype, this fails when a VI is broken, for example because a
    /// dependency of the PPL is missing. Steps are reported as Information when they load.
    /// </summary>
    public static void VerifyLoadDirectory(string directory, string junitReportPath = null)
    {
        var messages = new List<AnalysisMessageInfo>();
        try
        {
            directory = Path.GetFullPath(directory);
            if (!string.IsNullOrEmpty(junitReportPath))
                junitReportPath = Path.GetFullPath(junitReportPath);

            if (!Directory.Exists(directory))
                throw new DirectoryNotFoundException($"Directory '{directory}' does not exist.");

            var sequenceFiles = Directory.GetFiles(directory, "*.seq", SearchOption.AllDirectories);
            if (sequenceFiles.Length == 0)
                throw new FileNotFoundException($"No .seq files found in '{directory}'.");

            InitializeTestStandEngine();

            foreach (var path in sequenceFiles)
            {
                ConsoleLogger.Information("Loading code modules: {0}", path);
                var sequenceFile = _engine.GetSequenceFileEx(path, GetSeqFileOptions.GetSeqFile_DoNotRunLoadCallback);
                try
                {
                    for (var s = 0; s < sequenceFile.NumSequences; s++)
                    {
                        var sequence = sequenceFile.GetSequence(s);
                        foreach (var stepGroup in StepGroupsToRelink)
                        {
                            for (var i = 0; i < sequence.GetNumSteps(stepGroup); i++)
                                VerifyLoadStep(path, sequence, sequence.GetStep(i, stepGroup), messages);
                        }
                    }
                    sequenceFile.UnloadModules();
                }
                finally
                {
                    _engine.ReleaseSequenceFileEx(sequenceFile, ReleaseSeqFileOptions.ReleaseSeqFile_UnloadFile);
                }
                ProcessUIMessages();
            }

            var loaded = messages.FindAll(m => m.Severity == AnalysisMessageSeverity.Information).Count;
            var failed = messages.FindAll(m => m.Severity == AnalysisMessageSeverity.Error).Count;
            ConsoleLogger.Information("");
            ConsoleLogger.Information("---------- SUMMARY ----------");
            ConsoleLogger.Information("Sequence files: {0} | Steps loaded: {1} | Errors: {2}", sequenceFiles.Length, loaded, failed);
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

    private static void VerifyLoadStep(string path, Sequence sequence, Step step, List<AnalysisMessageInfo> messages)
    {
        if (step.AdapterKeyName != LabVIEWAdapterKeyName || step.GetRunModeEx() == RunModes.RunMode_Skip)
            return;

        var location = $"{sequence.Name}.{step.Name}";
        var viPath = ((LabVIEWModule)step.Module).VIPath;
        try
        {
            // Throw instead of showing an error dialog, which would block an unattended run
            step.Module.Load(LoadModuleOptions.LoadModule_ThrowExceptionOnError | LoadModuleOptions.LoadModule_DoNotPromptToFindFile, null);
            var text = $"{location}: loaded '{viPath}'";
            ConsoleLogger.Debug("  {0}", text);
            messages.Add(new AnalysisMessageInfo { Text = text, Severity = AnalysisMessageSeverity.Information, FilePath = path, Location = location });
        }
        catch (Exception ex)
        {
            var text = $"{location}: failed to load '{viPath}': {ex.Message}";
            ConsoleLogger.Error("ERROR   {0}", text);
            messages.Add(new AnalysisMessageInfo { Text = text, Severity = AnalysisMessageSeverity.Error, FilePath = path, Location = location });
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

        var pplExists = File.Exists(pplFull);
        if (!dryRun && !pplExists)
        {
            var text = $"PPL '{pplFull}' does not exist.";
            ConsoleLogger.Error("ERROR   {0}", text);
            messages.Add(new AnalysisMessageInfo { Text = text, Severity = AnalysisMessageSeverity.Error, FilePath = path });
            return;
        }

        var sequenceFile = _engine.GetSequenceFileEx(path, GetSeqFileOptions.GetSeqFile_DoNotRunLoadCallback);
        try
        {
            var steps = GetStepsToRelink(sequenceFile, sequenceDir, libraryDirFull);
            if (steps.Count == 0)
                return;

            string pplRootFull;
            if (pplExists)
            {
                pplRootFull = DetectPPLRoot(steps, sequenceDir, libraryDirFull, pplFull);
                if (pplRootFull == null)
                {
                    var text = $"No VI called below '{libraryDirFull}' could be loaded from '{pplFull}', with the library directory or any of its parents as the root of the paths in the PPL. " +
                               "Check that the PPL is built from the library and that its dependencies are deployed next to it.";
                    ConsoleLogger.Error("ERROR   {0}", text);
                    messages.Add(new AnalysisMessageInfo { Text = text, Severity = AnalysisMessageSeverity.Error, FilePath = path });
                    return;
                }
                ConsoleLogger.Information("  Root of the paths in the PPL: {0}", pplRootFull);
            }
            else
            {
                pplRootFull = libraryDirFull;
                ConsoleLogger.Information("  The PPL does not exist, so the paths in it are assumed to be relative to the library directory");
            }

            foreach (var (sequence, step) in steps)
                RelinkStep(path, sequenceDir, pplRootFull, pplFull, dryRun, sequence, step, messages);

            if (!dryRun)
                sequenceFile.Save(path);
            sequenceFile.UnloadModules();
        }
        finally
        {
            _engine.ReleaseSequenceFileEx(sequenceFile, ReleaseSeqFileOptions.ReleaseSeqFile_UnloadFile);
        }
    }

    /// <summary>
    /// The LabVIEW steps of a sequence file calling a VI below the library directory.
    /// </summary>
    private static List<(Sequence Sequence, Step Step)> GetStepsToRelink(SequenceFile sequenceFile, string sequenceDir, string libraryDirFull)
    {
        var steps = new List<(Sequence, Step)>();
        for (var s = 0; s < sequenceFile.NumSequences; s++)
        {
            var sequence = sequenceFile.GetSequence(s);
            foreach (var stepGroup in StepGroupsToRelink)
            {
                for (var i = 0; i < sequence.GetNumSteps(stepGroup); i++)
                {
                    var step = sequence.GetStep(i, stepGroup);
                    if (step.AdapterKeyName != LabVIEWAdapterKeyName)
                        continue;
                    var viPath = ((LabVIEWModule)step.Module).VIPath;
                    if (string.IsNullOrEmpty(viPath))
                        continue;
                    if (ResolveAgainst(sequenceDir, viPath).StartsWith(libraryDirFull, StringComparison.OrdinalIgnoreCase))
                        steps.Add((sequence, step));
                    else
                        ConsoleLogger.Information("  {0}.{1}: '{2}' is not below the library directory, not relinked", sequence.Name, step.Name, viPath);
                }
            }
        }
        return steps;
    }

    /// <summary>
    /// Finds the directory in the source tree that LabVIEW used as the root of the paths inside the PPL.
    /// LabVIEW lays out the files in a PPL relative to the common directory of all files built into it.
    /// That is the library directory only when every source dependency built into the PPL is located below
    /// it. A dependency elsewhere moves the root up, for example to the project directory, which gives
    /// API\Lib.lvlibp\API\Drive\Connect.vi. The root is therefore the library directory or one of its parents,
    /// and is found by loading VIs from the PPL with each of these as the root, starting with the library
    /// directory, until one loads. A few VIs are tried for each directory, so one broken VI does not fail it.
    /// </summary>
    private static string DetectPPLRoot(List<(Sequence Sequence, Step Step)> steps, string sequenceDir, string libraryDirFull, string pplFull)
    {
        const int probeCount = 3;
        // Steps that run are tried first, a skipped step may call a VI that is broken
        var probes = new List<Step>();
        foreach (var (_, step) in steps)
            if (probes.Count < probeCount && step.GetRunModeEx() != RunModes.RunMode_Skip)
                probes.Add(step);
        foreach (var (_, step) in steps)
            if (probes.Count < probeCount && !probes.Contains(step))
                probes.Add(step);

        for (var root = new DirectoryInfo(libraryDirFull); root != null; root = root.Parent)
        {
            var rootFull = root.FullName.TrimEnd('\\') + "\\";
            foreach (var step in probes)
            {
                var module = (LabVIEWModule)step.Module;
                var oldPath = module.VIPath;
                var oldProject = module.ProjectPath;
                var oldNamespace = module.Namespace;
                var newPath = GetPathInPPL(sequenceDir, ResolveAgainst(sequenceDir, oldPath), rootFull, pplFull);
                try
                {
                    step.Module.Unload();
                    module.ProjectPath = "";
                    module.VIPath = newPath;
                    module.Namespace = GetNamespaceInPPL(oldNamespace, pplFull);
                    // Throw instead of showing an error dialog, which would block an unattended run
                    step.Module.Load(LoadModuleOptions.LoadModule_ThrowExceptionOnError | LoadModuleOptions.LoadModule_DoNotPromptToFindFile, null);
                    ConsoleLogger.Information("  Loaded '{0}' from the PPL", newPath);
                    return rootFull;
                }
                catch (Exception ex)
                {
                    ConsoleLogger.Information("  Could not load '{0}' from the PPL: {1}", newPath, ex.Message);
                }
                finally
                {
                    step.Module.Unload();
                    module.VIPath = oldPath;
                    module.ProjectPath = oldProject;
                    module.Namespace = oldNamespace;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Path of a source VI inside the PPL, relative to the sequence file, given the root of the paths in the PPL.
    /// </summary>
    private static string GetPathInPPL(string sequenceDir, string viFull, string pplRootFull, string pplFull)
    {
        return Path.GetRelativePath(sequenceDir, pplFull) + "\\" + Path.GetRelativePath(pplRootFull, viFull);
    }

    private static void RelinkStep(string path, string sequenceDir, string pplRootFull, string pplFull, bool dryRun,
        Sequence sequence, Step step, List<AnalysisMessageInfo> messages)
    {
        var location = $"{sequence.Name}.{step.Name}";
        var module = (LabVIEWModule)step.Module;
        var viPath = module.VIPath;
        var skipped = step.GetRunModeEx() == RunModes.RunMode_Skip;
        var newPath = GetPathInPPL(sequenceDir, ResolveAgainst(sequenceDir, viPath), pplRootFull, pplFull);

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
            // In the PPL the top level library is renamed from Library.lvlib to Library.lvlibp,
            // and TestStand refuses to load a VI whose namespace differs from the one stored in the step
            module.Namespace = GetNamespaceInPPL(oldNamespace, pplFull);
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

    /// <summary>
    /// Replaces the top level library in a namespace by the PPL built from it,
    /// so "Lib.lvlib:Sub.lvlib" becomes "Lib.lvlibp:Sub.lvlib" for the PPL "Lib.lvlibp".
    /// </summary>
    internal static string GetNamespaceInPPL(string sourceNamespace, string pplPath)
    {
        var pplName = Path.GetFileName(pplPath);
        var libraryName = Path.GetFileNameWithoutExtension(pplPath) + ".lvlib";
        if (string.IsNullOrEmpty(sourceNamespace))
            return pplName;
        if (sourceNamespace.Equals(libraryName, StringComparison.OrdinalIgnoreCase))
            return pplName;
        if (sourceNamespace.StartsWith(libraryName + ":", StringComparison.OrdinalIgnoreCase))
            return pplName + sourceNamespace.Substring(libraryName.Length);
        return sourceNamespace;
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
