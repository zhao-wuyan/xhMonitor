import { useEffect, useMemo, useRef, useState } from 'react';
import { Activity, Settings } from 'lucide-react';
import { LayoutProvider, useLayout } from './contexts/LayoutContext';
import { TimeSeriesProvider } from './contexts/TimeSeriesContext';
import { AuthProvider, useAuth } from './contexts/AuthContext';
import { useMetricsHub } from './hooks/useMetricsHub';
import { useMetricConfig } from './hooks/useMetricConfig';
import { useAdaptiveScroll } from './hooks/useAdaptiveScroll';
import { t } from './i18n';
import { formatMegabytesParts, formatMegabytesLabel, formatNetworkRateLabel, formatNetworkRateParts } from './utils';
import type { SystemUsage } from './types';

// New components
import { StatCard } from './components/StatCard';
import { ChartCanvas } from './components/ChartCanvas';
import { SettingsDrawer } from './components/SettingsDrawer';
import { DiskWidget } from './components/DiskWidget';
import { DraggableGrid } from './components/DraggableGrid';
import { MobileNav } from './components/MobileNav';
import { ProcessList } from './components/ProcessList';
import { AccessKeyScreen } from './pages/AccessKeyScreen';

function AppShell() {
  const { metricsData, systemUsage, isConnected, error } = useMetricsHub();
  const { config, loading: configLoading } = useMetricConfig();
  const { layoutState } = useLayout();
  const shellRef = useRef<HTMLDivElement>(null);
  const processPanelRef = useRef<HTMLDivElement>(null);

  // Settings drawer state
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [isCompactWidth, setIsCompactWidth] = useState(() => {
    if (typeof window === 'undefined') return false;
    return window.matchMedia('(max-width: 1023px)').matches;
  });

  const adaptiveScroll = useAdaptiveScroll({
    enabled: !configLoading && Boolean(config) && layoutState.visibility.process && Boolean(metricsData),
    shellRef,
    processPanelRef,
    recomputeKey: [
      layoutState.gridColumns,
      layoutState.gaps.grid,
      layoutState.visibility.header,
      layoutState.visibility.disk,
      layoutState.visibility.cards,
      layoutState.visibility.process,
    ].join('|'),
  });

  const shellClassName = `min-h-screen w-full xh-app-shell${adaptiveScroll.mode === 'process' ? ' xh-app-shell--process-scroll' : ''}`;

  useEffect(() => {
    if (typeof window === 'undefined') return;

    const media = window.matchMedia('(max-width: 1023px)');
    const handler = (event: MediaQueryListEvent) => setIsCompactWidth(event.matches);

    setIsCompactWidth(media.matches);
    media.addEventListener('change', handler);
    return () => media.removeEventListener('change', handler);
  }, []);

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
    <div ref={shellRef} className={shellClassName}>
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
            {!isCompactWidth && layoutState.visibility.disk && (
              <div className="disk-header-slot">
                <DiskWidget disks={systemUsage?.disks} />
              </div>
            )}

            {/* Right: Status */}
            <div className="header-actions">
              <button
                type="button"
                className="header-icon-button"
                onClick={() => setSettingsOpen(true)}
                aria-label={t('Open settings')}
                title={t('Settings')}
              >
                <Settings size={18} aria-hidden="true" />
              </button>
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
            </div>
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
                    title={t('CPU')}
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
                const ramParts = formatMegabytesParts(systemUsage?.totalMemory ?? 0);
                const ramMaxMb = systemUsage?.maxMemory ?? 0;
                return (
                  <StatCard
                    key="ram"
                    cardId="ram"
                    title={t('RAM')}
                    value={ramParts.value}
                    unit={ramParts.unit}
                    subtitles={ramMaxMb > 0 ? [`/ ${formatMegabytesLabel(ramMaxMb)}`] : undefined}
                    accentColor={color}
                  >
                    <ChartCanvas
                      seriesKey="ram"
                      color={color}
                      maxValue={ramMaxMb > 0 ? ramMaxMb : undefined}
                      formatFn={(v) => formatMegabytesLabel(v)}
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
                    title={t('GPU')}
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
                const vramParts = formatMegabytesParts(systemUsage?.totalVram ?? 0);
                const vramMaxMb = systemUsage?.maxVram ?? 0;
                return (
                  <StatCard
                    key="vram"
                    cardId="vram"
                    title={t('VRAM')}
                    value={vramParts.value}
                    unit={vramParts.unit}
                    subtitles={vramMaxMb > 0 ? [`/ ${formatMegabytesLabel(vramMaxMb)}`] : undefined}
                    accentColor={color}
                  >
                    <ChartCanvas
                      seriesKey="vram"
                      color={color}
                      maxValue={vramMaxMb > 0 ? vramMaxMb : undefined}
                      formatFn={(v) => formatMegabytesLabel(v)}
                    />
                  </StatCard>
                );
              }

              if (cardId === 'net') {
                const upload = systemUsage?.uploadSpeed ?? 0;
                const download = systemUsage?.downloadSpeed ?? 0;
                const downloadParts = formatNetworkRateParts(download);
                const subline = `↑ ${formatNetworkRateParts(upload).compact}   ↓ ${downloadParts.compact}`;
                return (
                  <StatCard
                    key="net"
                    cardId="net"
                    title={t('NET')}
                    value={downloadParts.value}
                    unit={downloadParts.unit}
                    subtitles={[subline]}
                    accentColor={color}
                  >
                    <ChartCanvas
                      seriesKey="net"
                      color={color}
                      formatFn={(v) => formatNetworkRateLabel(v)}
                    />
                  </StatCard>
                );
              }

              if (cardId === 'pwr') {
                const totalPower = systemUsage?.totalPower ?? 0;
                const maxPower = systemUsage?.maxPower ?? 0;
                const powerAvailable = Boolean(systemUsage?.powerAvailable) || totalPower > 0 || maxPower > 0;
                return (
                  <StatCard
                    key="pwr"
                    cardId="pwr"
                    title={t('PWR')}
                    value={powerAvailable ? totalPower.toFixed(0) : '--'}
                    unit="W"
                    subtitles={powerAvailable && maxPower > 0 ? [`/ ${maxPower.toFixed(0)} W`] : undefined}
                    accentColor={color}
                  >
                    <ChartCanvas
                      seriesKey="pwr"
                      color={color}
                      maxValue={powerAvailable && maxPower > 0 ? maxPower : undefined}
                      formatFn={(v) => v.toFixed(0) + ' W'}
                    />
                  </StatCard>
                );
              }

              return null;
            })}
          </DraggableGrid>
        )}

        {/* Disk Info (Stacked on Compact Width) */}
        {isCompactWidth && layoutState.visibility.disk && (
          <div className="disk-stack-slot">
            <DiskWidget disks={systemUsage?.disks} />
          </div>
        )}

        {/* Process List */}
        {layoutState.visibility.process && metricsData && (
          <ProcessList
            ref={processPanelRef}
            processes={metricsData.processes}
            metricMetadata={config.metadata}
            colorMap={config.colorMap}
            maxMemoryMb={systemUsage?.maxMemory}
            maxVramMb={systemUsage?.maxVram}
            scrollMode={adaptiveScroll.mode}
            processTableMaxHeight={adaptiveScroll.processTableMaxHeight}
          />
        )}
      </div>

      {/* Settings Drawer */}
      <SettingsDrawer
        open={settingsOpen}
        onOpenChange={(open) => setSettingsOpen(open)}
        showTrigger={false}
      />
    </div>
  );
}

function AppContent() {
  const { requiresAccessKey, authEpoch } = useAuth();
  if (requiresAccessKey) {
    return <AccessKeyScreen />;
  }

  return <AppShell key={authEpoch} />;
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
        net: (usage: SystemUsage) => usage.downloadSpeed,
        pwr: (usage: SystemUsage) => usage.totalPower ?? 0,
      },
    }),
    []
  );

  return (
    <AuthProvider>
      <LayoutProvider>
        <TimeSeriesProvider options={timeSeriesOptions}>
          <AppContent />
        </TimeSeriesProvider>
      </LayoutProvider>
    </AuthProvider>
  );
}

export default App;
