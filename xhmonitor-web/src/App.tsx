import { useState, useEffect } from 'react';
import { Activity, Wifi, WifiOff, Minus, Square, X } from 'lucide-react';
import { SystemSummary } from './components/SystemSummary';
import { ProcessList } from './components/ProcessList';
import { MetricChart } from './components/MetricChart';
import { useMetricsHub } from './hooks/useMetricsHub';
import { useMetricConfig } from './hooks/useMetricConfig';
import { calculateSystemSummary, formatTimestamp } from './utils';
import { t } from './i18n';
import type { ChartDataPoint } from './types';

function App() {
  const { metricsData, isConnected, error } = useMetricsHub();
  const { config, loading: configLoading } = useMetricConfig();
  const [metricHistory, setMetricHistory] = useState<Record<string, ChartDataPoint[]>>({});
  const isDesktop = typeof window !== 'undefined' && !!window.electronAPI;

  useEffect(() => {
    if (!metricsData || !config) return;

    const summary = calculateSystemSummary(metricsData.processes);
    const timestamp = formatTimestamp(metricsData.timestamp);

    setMetricHistory((prev) => {
      const newHistory = { ...prev };

      config.metadata.forEach((metric) => {
        const value = summary[metric.metricId] || 0;
        const history = prev[metric.metricId] || [];
        newHistory[metric.metricId] = [...history, { timestamp, value }].slice(-30);
      });

      return newHistory;
    });
  }, [metricsData, config]);

  if (configLoading) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-gray-900 via-gray-800 to-gray-900 flex items-center justify-center">
        <div className="text-center">
          <Activity className="w-16 h-16 mx-auto mb-4 text-cpu animate-pulse" />
          <p className="text-gray-400">{t('Loading configuration...')}</p>
        </div>
      </div>
    );
  }

  if (!config) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-gray-900 via-gray-800 to-gray-900 flex items-center justify-center">
        <div className="text-center">
          <p className="text-red-500">{t('Failed to load metric configuration')}</p>
        </div>
      </div>
    );
  }

  const summary = metricsData
    ? calculateSystemSummary(metricsData.processes)
    : { processCount: 0 };

  const primaryMetrics = config.metadata.slice(0, 2);

  return (
    <div className="min-h-screen bg-gradient-to-br from-gray-900 via-gray-800 to-gray-900">
      {/* Custom Title Bar */}
      {isDesktop && (
        <div className="h-8 w-full drag-region flex justify-end items-center px-2 space-x-2 fixed top-0 left-0 z-50">
          <button onClick={() => window.electronAPI?.minimize()} className="p-1 hover:bg-white/10 rounded no-drag text-gray-400">
            <Minus size={16} />
          </button>
          <button onClick={() => window.electronAPI?.maximize()} className="p-1 hover:bg-white/10 rounded no-drag text-gray-400">
            <Square size={14} />
          </button>
          <button onClick={() => window.electronAPI?.close()} className="p-1 hover:bg-red-500/50 rounded no-drag text-gray-400">
            <X size={16} />
          </button>
        </div>
      )}

      <div className="container mx-auto px-4 py-6 pt-12">
        <header className="mb-8 drag-region">
          <div className="flex items-center justify-between no-drag">
            <div className="flex items-center gap-3">
              <Activity className="w-10 h-10 text-cpu" />
              <div>
                <h1 className="text-3xl font-bold bg-gradient-to-r from-cpu to-gpu bg-clip-text text-transparent">
                  {t('appTitle')}
                </h1>
                <p className="text-gray-400 text-sm">
                  {t('appSubtitle')}
                </p>
              </div>
            </div>

            <div className="flex items-center gap-3">
              {isConnected ? (
                <div className="flex items-center gap-2 text-memory">
                  <Wifi className="w-5 h-5" />
                  <span className="text-sm font-medium">{t('connected')}</span>
                </div>
              ) : (
                <div className="flex items-center gap-2 text-red-500">
                  <WifiOff className="w-5 h-5" />
                  <span className="text-sm font-medium">
                    {error ? t(error) : t('disconnected')}
                  </span>
                </div>
              )}
            </div>
          </div>
        </header>

        <SystemSummary
          summary={summary}
          metricMetadata={config.metadata}
          colorMap={config.colorMap}
          iconMap={config.iconMap}
        />

        <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 mb-6">
          {primaryMetrics.map((metric) => (
            <div key={metric.metricId} className="glass rounded-xl p-6">
              <MetricChart
                data={metricHistory[metric.metricId] || []}
                metricId={metric.metricId}
                title={metric.displayName}
                unit={metric.unit}
                color={config.colorMap[metric.metricId]}
              />
            </div>
          ))}
        </div>

        {metricsData && (
          <ProcessList
            processes={metricsData.processes}
            metricMetadata={config.metadata}
            colorMap={config.colorMap}
          />
        )}

        {!metricsData && isConnected && (
          <div className="glass rounded-xl p-12 text-center">
            <div className="animate-pulse-slow">
              <Activity className="w-16 h-16 mx-auto mb-4 text-cpu" />
              <p className="text-gray-400">{t('Waiting for metrics data...')}</p>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

export default App;
