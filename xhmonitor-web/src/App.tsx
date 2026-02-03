import { useState, useMemo } from 'react';
import { Activity, Wifi, WifiOff, Settings } from 'lucide-react';
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
        <header className="flex items-center justify-between">
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
            {/* Settings Button */}
            <button
              onClick={() => setSettingsOpen(true)}
              className="p-2 rounded-lg hover:bg-white/10 transition-colors"
              aria-label="Settings"
            >
              <Settings className="w-5 h-5" />
            </button>

            {/* Connection Status */}
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
        </header>

        {/* Disk Widget */}
        {layoutState.visibility.disk && <DiskWidget />}

        {/* Draggable Grid of Cards */}
        {layoutState.visibility.cards && (
          <DraggableGrid>
            {visibleCards.map((cardId) => {
              const color = layoutState.themeColors[cardId as keyof typeof layoutState.themeColors];

              if (cardId === 'cpu') {
                return (
                  <StatCard
                    key="cpu"
                    cardId="cpu"
                    title="CPU"
                    value={systemUsage?.totalCpu ?? 0}
                    unit="%"
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
                return (
                  <StatCard
                    key="gpu"
                    cardId="gpu"
                    title="GPU"
                    value={systemUsage?.totalGpu ?? 0}
                    unit="%"
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
                    value={systemUsage?.totalMemory ?? 0}
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
