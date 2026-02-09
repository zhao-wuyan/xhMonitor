using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using XhMonitor.Core.Interop;
using XhMonitor.Core.Models;
using XhMonitor.Core.Interfaces;
using XhMonitor.Service.Data;

namespace XhMonitor.Service.Core;

public class ProcessScanner
{
    private readonly ILogger<ProcessScanner> _logger;
    private readonly IConfiguration _config;
    private readonly IDbContextFactory<MonitorDbContext> _contextFactory;
    private readonly IProcessNameResolver _nameResolver;
    private readonly object _keywordsLock = new();
    private readonly ConcurrentDictionary<int, CommandLineCacheEntry> _commandLineCache = new();
    private static readonly TimeSpan CommandLineCacheTtl = TimeSpan.FromSeconds(30);

    // 配置文件中的基础关键字（不可变）
    private readonly List<string> _configIncludeKeywords;
    private readonly List<string> _configExcludeKeywords;

    // 运行时使用的关键字（配置文件 + 数据库融合）
    private List<string> _includeKeywords;
    private List<string> _excludeKeywords;

    public ProcessScanner(
        ILogger<ProcessScanner> logger,
        IConfiguration config,
        IProcessNameResolver nameResolver,
        IDbContextFactory<MonitorDbContext> contextFactory)
    {
        _logger = logger;
        _config = config;
        _nameResolver = nameResolver;
        _contextFactory = contextFactory;

        // 初始化时从 appsettings.json 加载默认关键字（保存为不可变基础）
        var keywords = config.GetSection("Monitor:Keywords").Get<string[]>() ?? Array.Empty<string>();
        _configIncludeKeywords = keywords.Where(k => !k.StartsWith("!")).Select(k => k.ToLowerInvariant()).ToList();
        _configExcludeKeywords = keywords.Where(k => k.StartsWith("!")).Select(k => k[1..].ToLowerInvariant()).ToList();

        // 初始运行时关键字 = 配置文件关键字
        _includeKeywords = new List<string>(_configIncludeKeywords);
        _excludeKeywords = new List<string>(_configExcludeKeywords);

        _logger.LogInformation("ProcessScanner initialized with {IncludeCount} include, {ExcludeCount} exclude keywords from config",
            _configIncludeKeywords.Count, _configExcludeKeywords.Count);

        // 异步加载数据库中的关键字并融合
        _ = Task.Run(async () => await TryLoadKeywordsFromDatabaseAsync());
    }

    /// <summary>
    /// 从数据库重新加载进程关键字（与配置文件融合）
    /// </summary>
    public async Task ReloadKeywordsAsync()
    {
        try
        {
            var loaded = await LoadAndMergeKeywordsFromDatabaseAsync();
            if (!loaded)
            {
                _logger.LogWarning("数据库中未找到 ProcessKeywords 配置,保持当前关键字不变");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "重新加载进程关键字失败");
        }
    }

    /// <summary>
    /// 尝试从数据库加载关键字并与配置文件融合（初始化时调用）
    /// </summary>
    private async Task TryLoadKeywordsFromDatabaseAsync()
    {
        try
        {
            await LoadAndMergeKeywordsFromDatabaseAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "从数据库加载进程关键字失败,使用 appsettings.json 中的默认值");
        }
    }

    /// <summary>
    /// 从数据库加载关键字并与配置文件融合的核心逻辑
    /// </summary>
    /// <returns>是否成功加载并融合了数据库中的关键字</returns>
    private async Task<bool> LoadAndMergeKeywordsFromDatabaseAsync()
    {
        await using var context = await _contextFactory.CreateDbContextAsync();

        var setting = await context.ApplicationSettings
            .FirstOrDefaultAsync(s => s.Category == "DataCollection" && s.Key == "ProcessKeywords");

        if (setting == null)
        {
            return false;
        }

        var dbKeywords = JsonSerializer.Deserialize<string[]>(setting.Value) ?? Array.Empty<string>();
        var dbInclude = dbKeywords.Where(k => !k.StartsWith("!")).Select(k => k.ToLowerInvariant()).ToList();
        var dbExclude = dbKeywords.Where(k => k.StartsWith("!")).Select(k => k[1..].ToLowerInvariant()).ToList();

        lock (_keywordsLock)
        {
            // 融合逻辑（数据库优先）：
            // 1. 最终包含 = (配置文件包含 + 数据库包含) - 数据库排除
            // 2. 最终排除 = (配置文件排除 + 数据库排除) - 数据库包含
            // 例如：配置文件有 python，用户配置 !python → 最终排除 python
            var mergedInclude = _configIncludeKeywords.Union(dbInclude).Distinct().ToList();
            var mergedExclude = _configExcludeKeywords.Union(dbExclude).Distinct().ToList();

            _includeKeywords = mergedInclude.Except(dbExclude).ToList();
            _excludeKeywords = mergedExclude.Except(dbInclude).ToList();
        }

        _logger.LogInformation("进程关键字已加载并融合: 配置文件 {ConfigInclude}+{ConfigExclude}, 数据库 {DbInclude}+{DbExclude}, 融合后 {TotalInclude}+{TotalExclude}",
            _configIncludeKeywords.Count, _configExcludeKeywords.Count,
            dbInclude.Count, dbExclude.Count,
            _includeKeywords.Count, _excludeKeywords.Count);

        return true;
    }

    public List<ProcessInfo> ScanProcesses()
    {
        var sw = Stopwatch.StartNew();
        var results = new ConcurrentBag<ProcessInfo>();

        try
        {
            var processes = Process.GetProcesses();
            var totalProcesses = processes.Length;
            var scanTimestamp = DateTime.UtcNow;
            var liveProcessIds = new HashSet<int>(totalProcesses);
            foreach (var process in processes)
            {
                liveProcessIds.Add(process.Id);
            }
            _logger.LogDebug("    → 开始扫描系统进程: 总计 {TotalCount} 个进程", totalProcesses);

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = 4
            };

            Parallel.ForEach(processes, options, process =>
            {
                try
                {
                    ProcessSingleProcess(process, results, scanTimestamp);
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

            CleanupCommandLineCache(liveProcessIds, scanTimestamp);

            _logger.LogDebug("    → 进程扫描完成: 从 {TotalCount} 个进程中匹配到 {MatchedCount} 个, 耗时: {ElapsedMs}ms",
                totalProcesses, results.Count, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "    → 进程枚举失败");
        }

        return results.ToList();
    }

    private void ProcessSingleProcess(Process process, ConcurrentBag<ProcessInfo> results, DateTime scanTimestamp)
    {
        var pid = process.Id;
        var processName = process.ProcessName;
        string? commandLine = null;
        _logger.LogTrace("进入ProcessSingleProcess");

        if (_commandLineCache.TryGetValue(pid, out var cacheEntry) && cacheEntry.ExpiresAtUtc > scanTimestamp)
        {
            commandLine = cacheEntry.CommandLine;
        }
        else
        {
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

            _commandLineCache[pid] = new CommandLineCacheEntry(commandLine, scanTimestamp + CommandLineCacheTtl);
        }

        if (commandLine == null)
        {
            return;
        }

        var matchedKeywords = GetMatchedKeywords(commandLine);

        // 使用锁保护关键字列表的读取
        bool shouldFilter;
        lock (_keywordsLock)
        {
            shouldFilter = (_includeKeywords.Count != 0 || _excludeKeywords.Count != 0) && matchedKeywords.Count <= 0;
        }

        if (shouldFilter) return;

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

        // 使用锁保护关键字列表的读取
        lock (_keywordsLock)
        {
            // 排除检查：命中任意排除关键字则返回空
            if (_excludeKeywords.Any(k => commandLineLower.Contains(k)))
                return new List<string>();

            if (_includeKeywords.Count == 0)
                return new List<string>();

            return _includeKeywords.Where(k => commandLineLower.Contains(k)).ToList();
        }
    }

    private void CleanupCommandLineCache(IReadOnlySet<int> liveProcessIds, DateTime scanTimestamp)
    {
        foreach (var cacheItem in _commandLineCache)
        {
            if (cacheItem.Value.ExpiresAtUtc <= scanTimestamp || !liveProcessIds.Contains(cacheItem.Key))
            {
                _commandLineCache.TryRemove(cacheItem.Key, out _);
            }
        }
    }

    private readonly record struct CommandLineCacheEntry(string CommandLine, DateTime ExpiresAtUtc);
}
