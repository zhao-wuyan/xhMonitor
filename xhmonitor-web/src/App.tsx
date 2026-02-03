import { useState, useMemo } from 'react';
import { Activity } from 'lucide-react';
import { LayoutProvider, useLayout } from './contexts/LayoutContext';
import { TimeSeriesProvider } from './contexts/TimeSeriesContext';
import { useMetricsHub } from './hooks/useMetricsHub';
import { useMetricConfig } from './hooks/useMetricConfig';
import { t } from './i18n';
import type { SystemUsage } from './types';

// New components
import { StatCard } from './components/StatCard';
import { ChartCanvas } from './components/ChartCanvas';
import { SettingsDrawer } from './components/SettingsDrawer';
import { DiskWidget } from './components/DiskWidget';
import { DraggableGrid } from './components/DraggableGrid';
import { MobileNav } from './components/MobileNav';
import { ProcessList } from './components/ProcessList';

function AppContent() {
  const { metricsData, systemUsage, isConnected, error } = useMetricsHub();
  const { config, loading: configLoading } = useMetricConfig();
  const { layoutState } = useLayout();

  // Settings drawer state
  const [settingsOpen, setSettingsOpen] = useState(false);

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

  // Get visible cards based on layoutState.visibility
  const visibleCards = layoutState.visibility.cards ? layoutState.cardOrder : [];

  return (
    <div className="min-h-screen w-full bg-gradient-to-br from-gray-900 via-gray-800 to-gray-900 xh-app-shell">
      {/* Mobile Navigation */}
      <MobileNav />

      <div className="app-container">
        {/* Header */}
        {layoutState.visibility.header && (
          <header className="header">
            {/* Left: Brand */}
            <div className="brand">
              <div className="logo-box">XM</div>
              <div>
                {t('appTitle')}
                <span className="version-tag">{t('appVersion')}</span>
              </div>
            </div>

            {/* Center: Disk Info */}
            {layoutState.visibility.disk && (
              <div className="disk-info-container">
                <DiskWidget />
              </div>
            )}

            {/* Right: Status */}
            {isConnected ? (
              <div className="status-badge">
                <div className="status-dot" />
                <span>{t('online')}</span>
              </div>
            ) : (
              <div className="status-badge status-badge--offline">
                <div className="status-dot status-dot--offline" />
                <span>{error ? t(error) : t('offline')}</span>
              </div>
            )}
          </header>
        )}

        {/* Draggable Grid of Cards */}
        {layoutState.visibility.cards && (
          <DraggableGrid>
            {visibleCards.map((cardId) => {
              const color = layoutState.themeColors[cardId as keyof typeof layoutState.themeColors];

              if (cardId === 'cpu') {
                const cpuTemp = systemUsage?.totalCpu ? 40 + systemUsage.totalCpu * 0.5 : undefined;
                return (
                  <StatCard
                    key="cpu"
                    cardId="cpu"
                    title="CPU"
                    value={systemUsage?.totalCpu ?? 0}
                    unit="%"
                    temperature={cpuTemp}
                    accentColor={color}
                  >
                    <ChartCanvas
                      seriesKey="cpu"
                      color={color}
                      formatFn={(v) => v.toFixed(1) + '%'}
                    />
                  </StatCard>
                );
              }

              if (cardId === 'ram') {
                return (
                  <StatCard
                    key="ram"
                    cardId="ram"
                    title="RAM"
                    value={systemUsage?.totalMemory ?? 0}
                    unit="GB"
                    accentColor={color}
                  >
                    <ChartCanvas
                      seriesKey="ram"
                      color={color}
                      formatFn={(v) => (v / 100 * 32).toFixed(1) + ' GB'}
                    />
                  </StatCard>
                );
              }

              if (cardId === 'gpu') {
                const gpuTemp = systemUsage?.totalGpu ? 35 + systemUsage.totalGpu * 0.6 : undefined;
                return (
                  <StatCard
                    key="gpu"
                    cardId="gpu"
                    title="GPU"
                    value={systemUsage?.totalGpu ?? 0}
                    unit="%"
                    temperature={gpuTemp}
                    accentColor={color}
                  >
                    <ChartCanvas
                      seriesKey="gpu"
                      color={color}
                      formatFn={(v) => v.toFixed(1) + '%'}
                    />
                  </StatCard>
                );
              }

              if (cardId === 'vram') {
                return (
                  <StatCard
                    key="vram"
                    cardId="vram"
                    title="VRAM"
                    value={systemUsage?.totalVram ?? 0}
                    unit="GB"
                    accentColor={color}
                  >
                    <ChartCanvas
                      seriesKey="vram"
                      color={color}
                      formatFn={(v) => (v / 100 * 24).toFixed(1) + ' GB'}
                    />
                  </StatCard>
                );
              }

              if (cardId === 'net') {
                return (
                  <StatCard
                    key="net"
                    cardId="net"
                    title="NET"
                    value={0}
                    unit="MB/s"
                    accentColor={color}
                  >
                    <ChartCanvas
                      seriesKey="net"
                      color={color}
                      formatFn={(v) => (v > 1024 ? (v / 1024).toFixed(1) + ' GB' : v.toFixed(0) + ' MB')}
                    />
                  </StatCard>
                );
              }

              if (cardId === 'pwr') {
                return (
                  <StatCard
                    key="pwr"
                    cardId="pwr"
                    title="PWR"
                    value={0}
                    unit="W"
                    accentColor={color}
                  >
                    <ChartCanvas
                      seriesKey="pwr"
                      color={color}
                      formatFn={(v) => v.toFixed(0) + ' W'}
                    />
                  </StatCard>
                );
              }

              return null;
            })}
          </DraggableGrid>
        )}

        {/* Process List */}
        {layoutState.visibility.process && metricsData && (
          <ProcessList
            processes={metricsData.processes}
            metricMetadata={config.metadata}
            colorMap={config.colorMap}
          />
        )}
      </div>

      {/* Settings Drawer */}
      <SettingsDrawer
        open={settingsOpen}
        onOpenChange={(open) => setSettingsOpen(open)}
      />

      {/* Mobile Nav Bottom Spacing */}
      <div className="h-16 md:hidden" />
    </div>
  );
}

function App() {
  const timeSeriesOptions = useMemo(
    () => ({
      maxLength: 60,
      selectors: {
        cpu: (usage: SystemUsage) => usage.totalCpu,
        ram: (usage: SystemUsage) => usage.totalMemory,
        gpu: (usage: SystemUsage) => usage.totalGpu,
        vram: (usage: SystemUsage) => usage.totalVram,
        net: (_usage: SystemUsage) => 0, // Placeholder
        pwr: () => 0, // No power data available yet
      },
    }),
    []
  );

  return (
    <LayoutProvider>
      <TimeSeriesProvider options={timeSeriesOptions}>
        <AppContent />
      </TimeSeriesProvider>
    </LayoutProvider>
  );
}

export default App;
