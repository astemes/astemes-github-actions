using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using TestStandCLI.Logging;

namespace TestStandCLI.Services;

public enum AnalysisMessageSeverity
{
    Error,
    Warning,
    Information,
    Unknown
}

public class AnalysisMessageInfo
{
    public string Text { get; set; }
    public AnalysisMessageSeverity Severity { get; set; }
    public string FilePath { get; set; }
    public string Location { get; set; }
}

public static class JUnitReportService
{
    public static void GenerateReport(string outputPath, List<AnalysisMessageInfo> messages)
    {
        try
        {
            var grouped = messages
                .GroupBy(m => string.IsNullOrEmpty(m.FilePath) ? "General" : m.FilePath)
                .ToList();

            var totalTests = messages.Count;
            var totalFailures = messages.Count(m => m.Severity == AnalysisMessageSeverity.Error);
            var totalWarnings = messages.Count(m => m.Severity == AnalysisMessageSeverity.Warning);

            // If no messages at all, create a single passing testcase
            if (totalTests == 0)
            {
                totalTests = 1;
            }

            var testSuites = new XElement("testsuites",
                new XAttribute("tests", totalTests),
                new XAttribute("failures", totalFailures),
                new XAttribute("skipped", totalWarnings),
                new XAttribute("errors", 0)
            );

            if (messages.Count == 0)
            {
                var cleanSuite = new XElement("testsuite",
                    new XAttribute("name", "TestStand Sequence Analysis"),
                    new XAttribute("tests", 1),
                    new XAttribute("failures", 0),
                    new XAttribute("errors", 0),
                    new XElement("testcase",
                        new XAttribute("name", "All sequences are clean"),
                        new XAttribute("classname", "SequenceAnalysis")
                    )
                );
                testSuites.Add(cleanSuite);
            }
            else
            {
                foreach (var group in grouped)
                {
                    var suiteName = group.Key == "General"
                        ? "General"
                        : Path.GetFileName(group.Key);

                    var suiteMessages = group.ToList();
                    var suiteFailures = suiteMessages.Count(m => m.Severity == AnalysisMessageSeverity.Error);
                    var suiteWarnings = suiteMessages.Count(m => m.Severity == AnalysisMessageSeverity.Warning);

                    var testSuite = new XElement("testsuite",
                        new XAttribute("name", suiteName),
                        new XAttribute("tests", suiteMessages.Count),
                        new XAttribute("failures", suiteFailures),
                        new XAttribute("skipped", suiteWarnings),
                        new XAttribute("errors", 0)
                    );

                    foreach (var message in suiteMessages)
                    {
                        var classname = string.IsNullOrEmpty(message.FilePath)
                            ? "SequenceAnalysis"
                            : Path.GetFileNameWithoutExtension(message.FilePath);

                        var testCase = new XElement("testcase",
                            new XAttribute("name", message.Text),
                            new XAttribute("classname", classname)
                        );

                        switch (message.Severity)
                        {
                            case AnalysisMessageSeverity.Error:
                            {
                                var detail = !string.IsNullOrEmpty(message.Location)
                                    ? $"Location: {message.Location}"
                                    : message.Text;
                                testCase.Add(new XElement("failure",
                                    new XAttribute("message", message.Text),
                                    new XAttribute("type", "Error"),
                                    detail
                                ));
                                break;
                            }
                            case AnalysisMessageSeverity.Warning:
                            {
                                // Reported as skipped so warnings are visible without failing the report.
                                var detail = !string.IsNullOrEmpty(message.Location)
                                    ? $"Warning: {message.Text} Location: {message.Location}"
                                    : $"Warning: {message.Text}";
                                testCase.Add(new XElement("skipped",
                                    new XAttribute("message", detail)
                                ));
                                break;
                            }
                        }

                        testSuite.Add(testCase);
                    }

                    testSuites.Add(testSuite);
                }
            }

            var doc = new XDocument(
                new XDeclaration("1.0", "UTF-8", null),
                testSuites
            );

            var outputDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
            {
                Directory.CreateDirectory(outputDir);
            }

            doc.Save(outputPath);
            ConsoleLogger.Information("JUnit XML report written to: {0}", outputPath);
        }
        catch (Exception ex)
        {
            ConsoleLogger.Error("Failed to generate JUnit XML report: {0}", ex.Message);
        }
    }
}
