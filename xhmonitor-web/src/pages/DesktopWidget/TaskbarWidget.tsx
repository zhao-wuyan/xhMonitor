import { useMemo } from 'react';
import { useMetricsHub } from '../../hooks/useMetricsHub';
import { useMetricConfig } from '../../hooks/useMetricConfig';
import { calculateSystemSummary } from '../../utils';
import { t } from '../../i18n';

export const TaskbarWidget = () => {
  const { metricsData, systemUsage, isConnected } = useMetricsHub();
  const { config } = useMetricConfig();

  // 计算系统总占用
  const summary = useMemo(() => {
    if (!metricsData) return null;
    return calculateSystemSummary(metricsData.processes, systemUsage);
  }, [metricsData, systemUsage]);

  // 格式化指标值（精简版）
  const formatCompact = (value: number, unit: string): string => {
    if (unit === '%') return `${value.toFixed(0)}%`;
    if (unit === 'MB' || unit === 'GB') {
      const gb = value / 1024;
      return gb >= 1 ? `${gb.toFixed(1)}GB` : `${value.toFixed(0)}MB`;
    }
    return `${value.toFixed(1)}${unit}`;
  };

  if (!config || !summary) {
    return (
      <div style={{
        width: '100%',
        height: '100%',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        background: 'rgba(30, 30, 30, 0.95)',
        color: '#999',
        fontSize: '11px',
        fontFamily: 'monospace'
      }}>
        {isConnected ? 'Loading...' : 'Disconnected'}
      </div>
    );
  }

  // 获取关键指标（CPU, MEM, GPU, NET）
  const keyMetrics = config.metadata.filter(m =>
    ['cpu', 'memory', 'gpu', 'network_upload'].includes(m.metricId)
  ).slice(0, 4);

  return (
    <div style={{
      width: '100%',
      height: '100%',
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'center',
      gap: '12px',
      background: 'rgba(30, 30, 30, 0.95)',
      color: 'white',
      fontSize: '11px',
      fontFamily: 'monospace',
      fontWeight: 600,
      padding: '0 12px'
    }}>
      {keyMetrics.map((metric) => {
        const value = summary[metric.metricId] || 0;
        const color = config.colorMap[metric.metricId] || '#6b7280';

        return (
          <span key={metric.metricId} style={{ color }}>
            {t(metric.displayName)}: {formatCompact(value, metric.unit)}
          </span>
        );
      })}

      {/* 连接状态指示器 */}
      <span style={{
        width: '6px',
        height: '6px',
        borderRadius: '50%',
        background: isConnected ? '#10b981' : '#ef4444',
        marginLeft: '4px'
      }} />
    </div>
  );
};
