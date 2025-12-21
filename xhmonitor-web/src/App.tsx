import { useState, useEffect } from 'react';
import { Activity, Wifi, WifiOff } from 'lucide-react';
import { SystemSummary } from './components/SystemSummary';
import { ProcessList } from './components/ProcessList';
import { MetricChart } from './components/MetricChart';
import { useMetricsHub } from './hooks/useMetricsHub';
import { useMetricConfig } from './hooks/useMetricConfig';
import { calculateSystemSummary, formatTimestamp } from './utils';
import type { ChartDataPoint } from './types';

function App() {
  const { metricsData, isConnected, error } = useMetricsHub();
  const { config, loading: configLoading } = useMetricConfig();
  const [metricHistory, setMetricHistory] = useState<Record<string, ChartDataPoint[]>>({});

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
          <p className="text-gray-400">Loading configuration...</p>
        </div>
      </div>
    );
  }

  if (!config) {
    return (
      <div className="min-h-screen bg-gradient-to-br from-gray-900 via-gray-800 to-gray-900 flex items-center justify-center">
        <div className="text-center">
          <p className="text-red-500">Failed to load metric configuration</p>
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
      <div className="container mx-auto px-4 py-6">
        <header className="mb-8">
          <div className="flex items-center justify-between">
            <div className="flex items-center gap-3">
              <Activity className="w-10 h-10 text-cpu" />
              <div>
                <h1 className="text-3xl font-bold bg-gradient-to-r from-cpu to-gpu bg-clip-text text-transparent">
                  XhMonitor
                </h1>
                <p className="text-gray-400 text-sm">
                  Windows Resource Monitor
                </p>
              </div>
            </div>

            <div className="flex items-center gap-3">
              {isConnected ? (
                <div className="flex items-center gap-2 text-memory">
                  <Wifi className="w-5 h-5" />
                  <span className="text-sm font-medium">Connected</span>
                </div>
              ) : (
                <div className="flex items-center gap-2 text-red-500">
                  <WifiOff className="w-5 h-5" />
                  <span className="text-sm font-medium">
                    {error || 'Disconnected'}
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
              <p className="text-gray-400">Waiting for metrics data...</p>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}

export default App;
