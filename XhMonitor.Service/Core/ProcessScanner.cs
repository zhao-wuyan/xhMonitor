using System.Collections.Concurrent;
using System.Diagnostics;
using XhMonitor.Core.Interop;
using XhMonitor.Core.Models;

namespace XhMonitor.Service.Core;

public class ProcessScanner
{
    private readonly ILogger<ProcessScanner> _logger;
    private readonly List<string> _keywords;

    public ProcessScanner(ILogger<ProcessScanner> logger, IConfiguration config)
    {
        _logger = logger;

        var keywords = config.GetSection("Monitor:Keywords").Get<string[]>() ?? Array.Empty<string>();
        _keywords = keywords.Select(k => k.ToLowerInvariant()).ToList();

        _logger.LogInformation("ProcessScanner initialized with {Count} keywords", _keywords.Count);
    }

    public List<ProcessInfo> ScanProcesses()
    {
        var results = new ConcurrentBag<ProcessInfo>();

        try
        {
            var processes = Process.GetProcesses();

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = 4
            };

            Parallel.ForEach(processes, options, process =>
            {
                try
                {
                    ProcessSingleProcess(process, results);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error scanning process {ProcessId} ({ProcessName})",
                        process.Id, process.ProcessName);
                }
                finally
                {
                    process.Dispose();
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during process enumeration");
        }

        return results.ToList();
    }

    private void ProcessSingleProcess(Process process, ConcurrentBag<ProcessInfo> results)
    {
        int pid = process.Id;
        string processName = process.ProcessName;
        string? commandLine = null;

        try
        {
            commandLine = ProcessCommandLineReader.GetCommandLine(pid);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get command line for process {ProcessId} ({ProcessName})",
                pid, processName);
            return;
        }

        if (commandLine == null)
        {
            return;
        }

        var matchedKeywords = GetMatchedKeywords(commandLine);

        if (_keywords.Count == 0 || matchedKeywords.Count > 0)
        {
            results.Add(new ProcessInfo
            {
                ProcessId = pid,
                ProcessName = processName,
                CommandLine = commandLine,
                MatchedKeywords = matchedKeywords
            });
        }
    }

    private List<string> GetMatchedKeywords(string commandLine)
    {
        if (_keywords.Count == 0)
        {
            return new List<string>();
        }

        var commandLineLower = commandLine.ToLowerInvariant();
        return _keywords.Where(k => commandLineLower.Contains(k)).ToList();
    }
}
