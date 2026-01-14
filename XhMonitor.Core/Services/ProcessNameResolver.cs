using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using XhMonitor.Core.Interfaces;
using XhMonitor.Core.Models;

namespace XhMonitor.Core.Services;

/// <summary>
/// 进程名称解析服务
/// </summary>
public class ProcessNameResolver : IProcessNameResolver
{
    private readonly ILogger<ProcessNameResolver> _logger;
    private readonly List<ProcessNameRule> _rules;

    /// <summary>
    /// 按进程名称分组的规则字典，用于 O(1) 查找性能优化
    /// Key: 进程名称（不区分大小写）
    /// Value: 该进程的所有匹配规则列表
    /// </summary>
    private readonly Dictionary<string, List<ProcessNameRule>> _rulesByProcess;
    private readonly ConcurrentDictionary<string, Regex> _regexCache = new();

    public ProcessNameResolver(IConfiguration configuration, ILogger<ProcessNameResolver> logger)
    {
        _logger = logger;

        var rulesSection = configuration.GetSection("Monitor:ProcessNameRules");
        _rules = rulesSection.Get<List<ProcessNameRule>>() ?? new List<ProcessNameRule>();

        // 验证规则完整性
        foreach (var rule in _rules)
        {
            ValidateRule(rule);
        }

        // 按 ProcessName 分组以优化查找性能 (O(n) → O(1))
        _rulesByProcess = _rules
            .GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        _logger.LogInformation("ProcessNameResolver initialized with {Count} rules", _rules.Count);
    }

    /// <summary>
    /// 验证规则的完整性
    /// </summary>
    /// <param name="rule">待验证的规则</param>
    private void ValidateRule(ProcessNameRule rule)
    {
        if (rule.Type == "Regex" && string.IsNullOrWhiteSpace(rule.Pattern))
        {
            _logger.LogWarning("Invalid Regex rule: {ProcessName} missing Pattern", rule.ProcessName);
        }
        if (rule.Type == "Direct" && string.IsNullOrWhiteSpace(rule.DisplayName))
        {
            _logger.LogWarning("Invalid Direct rule: {ProcessName} missing DisplayName", rule.ProcessName);
        }
    }

    public string Resolve(string processName, string commandLine)
    {
        try
        {
            var matchedRule = FindMatchingRule(processName, commandLine);
            if (matchedRule == null)
            {
                _logger.LogDebug("No rule matched for process: {ProcessName}", processName);
                return processName;
            }

            return matchedRule.Type.ToLowerInvariant() switch
            {
                "regex" => ExtractWithRegex(matchedRule, commandLine, processName),
                "direct" => ExtractDirect(matchedRule),
                _ => processName
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to resolve display name for process: {ProcessName}", processName);
            return processName;
        }
    }

    private ProcessNameRule? FindMatchingRule(string processName, string commandLine)
    {
        // 使用字典查找，O(1) 复杂度
        if (!_rulesByProcess.TryGetValue(processName, out var rulesForProcess))
        {
            return null;
        }

        // 优先匹配带关键字的规则
        // 使用 IndexOf 替代 ToLowerInvariant + Contains 以提升性能
        var ruleWithKeywords = rulesForProcess
            .Where(r => r.Keywords.Length > 0)
            .FirstOrDefault(r => r.Keywords.Any(k =>
                commandLine.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0));

        if (ruleWithKeywords != null)
        {
            return ruleWithKeywords;
        }

        // 回退到无关键字的规则
        return rulesForProcess.FirstOrDefault(r => r.Keywords.Length == 0);
    }

    private string ExtractWithRegex(ProcessNameRule rule, string commandLine, string processName)
    {
        if (string.IsNullOrWhiteSpace(rule.Pattern))
        {
            _logger.LogDebug("Regex rule missing Pattern: {ProcessName}", rule.ProcessName);
            return processName;
        }

        var regex = _regexCache.GetOrAdd(rule.Pattern, pattern => new Regex(pattern, RegexOptions.Compiled));

        var match = regex.Match(commandLine);
        if (!match.Success)
        {
            _logger.LogDebug("Regex pattern did not match: {Pattern}", rule.Pattern);
            return processName;
        }

        var groupIndex = rule.Group ?? 1;
        if (groupIndex < 0 || groupIndex >= match.Groups.Count)
        {
            _logger.LogDebug("Invalid capture group index: {GroupIndex}", groupIndex);
            return processName;
        }

        var capturedValue = match.Groups[groupIndex].Value;

        if (!string.IsNullOrWhiteSpace(rule.Format))
        {
            return string.Format(rule.Format, capturedValue);
        }

        return capturedValue;
    }

    private string ExtractDirect(ProcessNameRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.DisplayName))
        {
            _logger.LogDebug("Direct rule missing DisplayName: {ProcessName}", rule.ProcessName);
            return rule.ProcessName;
        }

        return rule.DisplayName;
    }
}
