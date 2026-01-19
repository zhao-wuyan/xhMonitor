using System.Collections.Concurrent;
using System.Diagnostics;
using XhMonitor.Core.Interop;
using XhMonitor.Core.Models;
using XhMonitor.Core.Interfaces;

namespace XhMonitor.Service.Core;

public class ProcessScanner
{
    private readonly ILogger<ProcessScanner> _logger;
    private readonly List<string> _includeKeywords;
    private readonly List<string> _excludeKeywords;
    private readonly IProcessNameResolver _nameResolver;

    public ProcessScanner(ILogger<ProcessScanner> logger, IConfiguration config, IProcessNameResolver nameResolver)
    {
        _logger = logger;
        _nameResolver = nameResolver;

        var keywords = config.GetSection("Monitor:Keywords").Get<string[]>() ?? Array.Empty<string>();
        _includeKeywords = keywords.Where(k => !k.StartsWith("!")).Select(k => k.ToLowerInvariant()).ToList();
        _excludeKeywords = keywords.Where(k => k.StartsWith("!")).Select(k => k[1..].ToLowerInvariant()).ToList();

        _logger.LogInformation("ProcessScanner initialized with {IncludeCount} include, {ExcludeCount} exclude keywords",
            _includeKeywords.Count, _excludeKeywords.Count);
    }

    public List<ProcessInfo> ScanProcesses()
    {
        var sw = Stopwatch.StartNew();
        var results = new ConcurrentBag<ProcessInfo>();

        try
        {
            var processes = Process.GetProcesses();
            var totalProcesses = processes.Length;
            _logger.LogDebug("    → 开始扫描系统进程: 总计 {TotalCount} 个进程", totalProcesses);

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
                    _logger.LogWarning(ex, "    → 扫描进程 {ProcessId} ({ProcessName}) 时出错",
                        process.Id, process.ProcessName);
                }
                finally
                {
                    process.Dispose();
                }
            });

            _logger.LogDebug("    → 进程扫描完成: 从 {TotalCount} 个进程中匹配到 {MatchedCount} 个, 耗时: {ElapsedMs}ms",
                totalProcesses, results.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "    → 进程枚举失败");
        }

        return results.ToList();
    }

    private void ProcessSingleProcess(Process process, ConcurrentBag<ProcessInfo> results)
    {
        var pid = process.Id;
        var processName = process.ProcessName;
        string? commandLine = null;
        _logger.LogTrace("进入ProcessSingleProcess");
        
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
            _logger.LogWarning(ex, "获取进程命令行失败 {ProcessId} ({ProcessName})",
                pid, processName);
            return;
        }

        if (commandLine == null)
        {
            return;
        }

        var matchedKeywords = GetMatchedKeywords(commandLine);

        if ((_includeKeywords.Count != 0 || _excludeKeywords.Count != 0) && matchedKeywords.Count <= 0) return;
        
        var resolvedName = _nameResolver.Resolve(processName, commandLine);
        var displayName = !string.IsNullOrEmpty(resolvedName) ? resolvedName : processName;

        _logger.LogTrace("获取进程命令友好名称【{displayName}】 {commandLine} ({processName})",
            displayName, commandLine, processName);

        results.Add(new ProcessInfo
        {
            ProcessId = pid,
            ProcessName = processName,
            CommandLine = commandLine,
            DisplayName = displayName,
            MatchedKeywords = matchedKeywords
        });
    }

    private List<string> GetMatchedKeywords(string commandLine)
    {
        var commandLineLower = commandLine.ToLowerInvariant();

        // 排除检查：命中任意排除关键字则返回空
        if (_excludeKeywords.Any(k => commandLineLower.Contains(k)))
            return new List<string>();

        if (_includeKeywords.Count == 0)
            return new List<string>();

        return _includeKeywords.Where(k => commandLineLower.Contains(k)).ToList();
    }
}