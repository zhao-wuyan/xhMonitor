import { useState, useEffect, useMemo } from 'react';
import { Lock, Unlock, MousePointer2, WifiOff } from 'lucide-react';
import { useMetricsHub } from '../../hooks/useMetricsHub';
import { useMetricConfig } from '../../hooks/useMetricConfig';
import { useWidgetConfig } from '../../hooks/useWidgetConfig';
import { calculateSystemSummary, formatPercent, formatBytes } from '../../utils';
import { t } from '../../i18n';
import type { ProcessInfo } from '../../types';

type WidgetState = 'collapsed' | 'expanded' | 'locked' | 'clickthrough';

export const FloatingWidget = () => {
  const { metricsData, isConnected } = useMetricsHub();
  const { config } = useMetricConfig();
  const { isMetricClickEnabled, getMetricAction } = useWidgetConfig();
  const [state, setState] = useState<WidgetState>('collapsed');
  const [isHovering, setIsHovering] = useState(false);

  // 计算系统总占用
  const summary = useMemo(() => {
    if (!metricsData) return null;
    return calculateSystemSummary(metricsData.processes);
  }, [metricsData]);

  // 获取前5个进程（按 CPU 占用排序）
  const top5Processes = useMemo(() => {
    if (!metricsData?.processes) return [];

    return [...metricsData.processes]
      .sort((a, b) => {
        const aCpu = a.metrics['cpu']?.value || 0;
        const bCpu = b.metrics['cpu']?.value || 0;
        return bCpu - aCpu;
      })
      .slice(0, 5);
  }, [metricsData]);

  // 状态转换逻辑
  useEffect(() => {
    if (state === 'collapsed' && isHovering) {
      setState('expanded');
    } else if (state === 'expanded' && !isHovering) {
      setState('collapsed');
    }
  }, [isHovering, state]);

  // 处理背景区域点击（锁定展开状态）
  const handleBackgroundClick = (e: React.MouseEvent) => {
    // 只有点击背景区域才触发锁定
    if (e.target === e.currentTarget) {
      if (state === 'expanded') {
        setState('locked');
      } else if (state === 'locked') {
        setState('collapsed');
      }
    }
  };

  // 处理指标点击事件
  const handleMetricClick = (metricId: string, e: React.MouseEvent) => {
    e.stopPropagation();

    // 检查是否启用了该指标的点击功能
    if (!isMetricClickEnabled(metricId)) {
      return;
    }

    const action = getMetricAction(metricId);
    console.log(`Metric clicked: ${metricId}, action: ${action}`);

    // 通知 WPF 执行配置的动作
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage({
        type: 'metricAction',
        metricId: metricId,
        action: action
      });
    }
  };

  // 处理进程点击事件
  const handleProcessClick = (process: ProcessInfo, e: React.MouseEvent) => {
    e.stopPropagation();

    console.log(`Process clicked: ${process.processName}`);

    // 通知 WPF 打开进程详情
    if (window.chrome?.webview) {
      window.chrome.webview.postMessage({
        type: 'processAction',
        processId: process.processId,
        processName: process.processName,
        action: 'showDetails'
      });
    }
  };

  // 处理穿透模式切换
  const handleToggleClickthrough = (e: React.MouseEvent) => {
    e.stopPropagation();

    if (state === 'locked') {
      setState('clickthrough');
      if (window.chrome?.webview) {
        window.chrome.webview.postMessage({
          type: 'setClickthrough',
          enabled: true
        });
      }
    } else if (state === 'clickthrough') {
      setState('locked');
      if (window.chrome?.webview) {
        window.chrome.webview.postMessage({
          type: 'setClickthrough',
          enabled: false
        });
      }
    }
  };

  const isExpanded = state === 'expanded' || state === 'locked' || state === 'clickthrough';
  const showControls = state === 'locked' || state === 'clickthrough';

  // 获取指标值
  const getMetricValue = (process: ProcessInfo, metricId: string): number => {
    return process.metrics[metricId]?.value || 0;
  };

  // 格式化指标值
  const formatMetricValue = (value: number, unit: string): string => {
    if (unit === '%') return formatPercent(value);
    if (unit === 'MB' || unit === 'GB') return formatBytes(value * 1024 * 1024);
    return `${value.toFixed(1)} ${unit}`;
  };

  if (!config) return null;

  return (
    <div
      className={`widget-container ${state}`}
      onMouseEnter={() => setIsHovering(true)}
      onMouseLeave={() => setIsHovering(false)}
      onClick={handleBackgroundClick}
      style={{
        position: 'fixed',
        top: 0,
        left: 0,
        width: '100%',
        height: '100%',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        pointerEvents: state === 'clickthrough' ? 'none' : 'auto'
      }}
    >
      <div
        className={`widget-content ${isExpanded ? 'expanded' : 'collapsed'}`}
        style={{
          background: 'rgba(30, 30, 30, 0.95)',
          borderRadius: isExpanded ? '12px' : '8px',
          padding: isExpanded ? '16px' : '12px',
          boxShadow: '0 4px 12px rgba(0, 0, 0, 0.5)',
          transition: 'all 0.3s ease',
          width: isExpanded ? '600px' : '280px',
          minHeight: isExpanded ? '180px' : '70px',
          pointerEvents: 'auto'
        }}
      >
        {/* 系统总占用 */}
        <div className="summary-bar" style={{
          display: 'flex',
          gap: '16px',
          alignItems: 'center',
          paddingBottom: isExpanded ? '12px' : '0',
          borderBottom: isExpanded ? '1px solid rgba(255, 255, 255, 0.1)' : 'none',
          fontSize: '13px',
          color: 'white',
          fontWeight: 600
        }}>
          {config.metadata.slice(0, 5).map((metric) => {
            const value = summary ? summary[metric.metricId] || 0 : 0;
            const color = config.colorMap[metric.metricId] || '#6b7280';
            const clickEnabled = isMetricClickEnabled(metric.metricId);

            return (
              <span
                key={metric.metricId}
                onClick={(e) => handleMetricClick(metric.metricId, e)}
                style={{
                  cursor: clickEnabled ? 'pointer' : 'default',
                  padding: '4px 8px',
                  borderRadius: '4px',
                  transition: 'background 0.2s',
                  color: color,
                  opacity: clickEnabled ? 1 : 0.8
                }}
                onMouseEnter={(e) => {
                  if (clickEnabled) {
                    e.currentTarget.style.background = 'rgba(255, 255, 255, 0.1)';
                  }
                }}
                onMouseLeave={(e) => {
                  e.currentTarget.style.background = 'transparent';
                }}
                title={clickEnabled ? `点击执行 ${t(metric.displayName)} 操作` : t(metric.displayName)}
              >
                {t(metric.displayName)}: {formatMetricValue(value, metric.unit)}
              </span>
            );
          })}

          {/* 控制按钮 */}
          {showControls && (
            <div style={{ marginLeft: 'auto', display: 'flex', gap: '8px' }}>
              <button
                onClick={(e) => {
                  e.stopPropagation();
                  if (state === 'locked') {
                    setState('collapsed');
                  }
                }}
                style={{
                  background: 'rgba(255, 255, 255, 0.1)',
                  border: 'none',
                  borderRadius: '4px',
                  padding: '4px 8px',
                  cursor: 'pointer',
                  fontSize: '16px',
                  color: 'white'
                }}
                title="解锁"
              >
                {state === 'locked' ? <Lock size={16} /> : <Unlock size={16} />}
              </button>
              <button
                onClick={handleToggleClickthrough}
                style={{
                  background: state === 'clickthrough' ? 'rgba(59, 130, 246, 0.3)' : 'rgba(255, 255, 255, 0.1)',
                  border: 'none',
                  borderRadius: '4px',
                  padding: '4px 8px',
                  cursor: 'pointer',
                  fontSize: '16px',
                  color: 'white'
                }}
                title={state === 'clickthrough' ? '禁用穿透' : '启用穿透'}
              >
                <MousePointer2 size={16} />
              </button>
            </div>
          )}
        </div>

        {/* 进程列表（展开时显示） */}
        {isExpanded && (
          <div className="process-list" style={{
            marginTop: '12px',
            display: 'flex',
            flexDirection: 'column',
            gap: '8px'
          }}>
            {top5Processes.map((process, index) => (
              <div
                key={`${process.processId}-${index}`}
                className="process-row"
                onClick={(e) => handleProcessClick(process, e)}
                style={{
                  display: 'flex',
                  alignItems: 'center',
                  gap: '12px',
                  padding: '6px 8px',
                  background: 'rgba(255, 255, 255, 0.05)',
                  borderRadius: '4px',
                  fontSize: '12px',
                  color: 'white',
                  transition: 'background 0.2s',
                  cursor: 'pointer'
                }}
                onMouseEnter={(e) => {
                  e.currentTarget.style.background = 'rgba(255, 255, 255, 0.1)';
                }}
                onMouseLeave={(e) => {
                  e.currentTarget.style.background = 'rgba(255, 255, 255, 0.05)';
                }}
                title={`点击查看 ${process.processName} 详情`}
              >
                <span style={{ flex: 1, fontWeight: 500 }}>
                  {process.processName}
                </span>
                {config.metadata.slice(0, 4).map((metric) => {
                  const value = getMetricValue(process, metric.metricId);
                  return (
                    <span
                      key={metric.metricId}
                      style={{
                        width: '80px',
                        textAlign: 'right',
                        color: 'rgba(255, 255, 255, 0.8)'
                      }}
                    >
                      {t(metric.displayName)}: {formatMetricValue(value, metric.unit)}
                    </span>
                  );
                })}
              </div>
            ))}
          </div>
        )}

        {/* 连接状态 */}
        {!isConnected && (
          <div style={{
            marginTop: '8px',
            display: 'flex',
            alignItems: 'center',
            gap: '8px',
            fontSize: '12px',
            color: '#ef4444'
          }}>
            <WifiOff size={14} />
            <span>{t('Disconnected')}</span>
          </div>
        )}
      </div>
    </div>
  );
};
