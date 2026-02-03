import { useState, useMemo } from 'react';
import { Settings, Activity } from 'lucide-react';
import { LayoutProvider, useLayout } from './contexts/LayoutContext';
import { TimeSeriesProvider } from './contexts/TimeSeriesContext';
import { useMetricsHub } from './hooks/useMetricsHub';
import { useMetricConfig } from './hooks/useMetricConfig';
import { t } from './i18n';

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
  const visibleCards = layoutState.cardOrder.filter(_cardId => layoutState.visibility.cards);

  return (
    <div className="min-h-screen bg-gradient-to-br from-gray-900 via-gray-800 to-gray-900">
      {/* Mobile Navigation */}
      <MobileNav />

      <div className="app-container">
        {/* Header */}
        <header className="flex items-center justify-between relative py-2.5 px-1.5">
          {/* Left: Brand */}
          <div className="flex items-center gap-3 z-20">
            <div className="w-9 h-9 bg-gradient-to-br from-cpu to-ram rounded-lg flex items-center justify-center text-white font-bold text-lg shadow-lg shadow-cpu/30">
              XM
            </div>
            <div className="flex items-center gap-2">
              <h1 className="text-xl font-bold text-white">
                {t('appTitle')}
              </h1>
              <span className="text-xs text-gray-400 font-normal px-2 py-0.5 bg-white/10 rounded">
                {t('appVersion')}
              </span>
            </div>
          </div>

          {/* Center: Disk Info */}
          {layoutState.visibility.disk && (
            <div className="absolute left-1/2 top-1/2 -translate-x-1/2 -translate-y-1/2 z-10 hidden md:flex">
              <DiskWidget />
            </div>
          )}

          {/* Right: Status */}
          <div className="flex items-center gap-3 z-20">
            {/* Settings Button - Hidden to match design */}
            <button
              onClick={() => setSettingsOpen(true)}
              className="p-2 rounded-lg hover:bg-white/10 transition-colors opacity-0 pointer-events-none"
              aria-label="Settings"
            >
              <Settings className="w-5 h-5" />
            </button>

            {/* Connection Status */}
            {isConnected ? (
              <div className="flex items-center gap-2 px-3 py-1.5 rounded-full bg-memory/15 border border-memory/30">
                <div className="w-1.5 h-1.5 bg-memory rounded-full shadow-[0_0_8px_currentColor] animate-pulse" />
                <span className="text-sm font-semibold text-memory">{t('online')}</span>
              </div>
            ) : (
              <div className="flex items-center gap-2 px-3 py-1.5 rounded-full bg-red-500/15 border border-red-500/30">
                <div className="w-1.5 h-1.5 bg-red-500 rounded-full" />
                <span className="text-sm font-semibold text-red-500">
                  {error ? t(error) : t('offline')}
                </span>
              </div>
            )}
          </div>
        </header>

        {/* Disk Widget */}
        {layoutState.visibility.disk && <DiskWidget />}

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
        cpu: (usage: any) => usage.totalCpu,
        ram: (usage: any) => usage.totalMemory,
        gpu: (usage: any) => usage.totalGpu,
        vram: (usage: any) => usage.totalVram,
        net: (usage: any) => usage.totalMemory * 0, // Placeholder
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
