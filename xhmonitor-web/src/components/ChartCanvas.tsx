import { memo, useEffect, useId, useMemo, useRef } from 'react';
import { useTimeSeries } from '../contexts/TimeSeriesContext';
import MiniChartModule from '../../components/charts/MiniChart.js';

export type ChartFormatFn = (value: number) => string;

interface ChartCanvasProps {
  seriesKey: string;
  color: string;
  maxValue?: number;
  formatFn?: ChartFormatFn;
  data?: number[];
  className?: string;
  refreshHz?: number;
  showPeakValleyMarkers?: boolean;
}

type MiniChartInstance = {
  draw: (data: number[], maxValue?: number) => void;
  resize: () => void;
  destroy: () => void;
  color?: string;
  formatFn?: ChartFormatFn;
  markersEnabled?: boolean;
};

type MiniChartConstructor = new (
  canvasId: string,
  containerId: string,
  color: string,
  formatFn?: ChartFormatFn
) => MiniChartInstance;

const MiniChart = MiniChartModule as unknown as MiniChartConstructor | undefined;

const clampRefreshHz = (hz: number) => Math.min(5, Math.max(2, hz));

const ChartCanvasBase = ({
  seriesKey,
  color,
  maxValue = 100,
  formatFn,
  data,
  className,
  refreshHz = 3,
  showPeakValleyMarkers = true,
}: ChartCanvasProps) => {
  const { getSeriesData } = useTimeSeries();
  const rawId = useId();
  const safeId = useMemo(() => rawId.replace(/[:]/g, ''), [rawId]);
  const canvasId = useMemo(() => `xh-chart-canvas-${safeId}`, [safeId]);
  const containerId = useMemo(() => `xh-chart-container-${safeId}`, [safeId]);
  const canvasRef = useRef<HTMLCanvasElement | null>(null);
  const containerRef = useRef<HTMLDivElement | null>(null);
  const chartRef = useRef<MiniChartInstance | null>(null);
  const rafRef = useRef<number | null>(null);
  const lastFrameRef = useRef(0);
  const pendingDataRef = useRef<number[] | null>(null);
  const maxValueRef = useRef(maxValue);
  const refreshHzRef = useRef(clampRefreshHz(refreshHz));

  const resolvedFormat = useMemo<ChartFormatFn>(() => {
    if (formatFn) return formatFn;
    return (value) => `${value.toFixed(0)}%`;
  }, [formatFn]);

  const seriesData = data ?? getSeriesData(seriesKey);

  useEffect(() => {
    maxValueRef.current = maxValue;
  }, [maxValue]);

  useEffect(() => {
    refreshHzRef.current = clampRefreshHz(refreshHz);
  }, [refreshHz]);

  useEffect(() => {
    if (!canvasRef.current || !containerRef.current) return;

    const ChartCtor =
      MiniChart ??
      (typeof window !== 'undefined'
        ? (window as unknown as { MiniChart?: MiniChartConstructor }).MiniChart
        : undefined);

    if (!ChartCtor) return;

    const chart = new ChartCtor(canvasId, containerId, color, resolvedFormat);
    chartRef.current = chart;

    return () => {
      if (rafRef.current) {
        cancelAnimationFrame(rafRef.current);
        rafRef.current = null;
      }
      chart.destroy();
      chartRef.current = null;
    };
  }, [canvasId, containerId]);

  useEffect(() => {
    if (!chartRef.current) return;
    chartRef.current.color = color;
    chartRef.current.formatFn = resolvedFormat;
    chartRef.current.markersEnabled = showPeakValleyMarkers;
  }, [color, resolvedFormat, showPeakValleyMarkers]);

  useEffect(() => {
    pendingDataRef.current = seriesData;

    const drawFrame = (timestamp: number) => {
      const minInterval = 1000 / refreshHzRef.current;
      if (timestamp - lastFrameRef.current < minInterval) {
        rafRef.current = requestAnimationFrame(drawFrame);
        return;
      }

      lastFrameRef.current = timestamp;
      rafRef.current = null;

      const chart = chartRef.current;
      const payload = pendingDataRef.current;
      if (chart && payload) {
        chart.draw(payload, maxValueRef.current);
      }
    };

    if (rafRef.current === null) {
      rafRef.current = requestAnimationFrame(drawFrame);
    }

    return () => {
      if (rafRef.current) {
        cancelAnimationFrame(rafRef.current);
        rafRef.current = null;
      }
    };
  }, [seriesData]);

  return (
    <div
      ref={containerRef}
      id={containerId}
      className={`xh-stat-card__chart ${className ?? ''}`.trim()}
    >
      <canvas ref={canvasRef} id={canvasId} className="xh-stat-card__canvas" />
    </div>
  );
};

const areEqual = (prev: ChartCanvasProps, next: ChartCanvasProps) => {
  return (
    prev.seriesKey === next.seriesKey &&
    prev.color === next.color &&
    prev.maxValue === next.maxValue &&
    prev.formatFn === next.formatFn &&
    prev.data === next.data &&
    prev.className === next.className &&
    prev.refreshHz === next.refreshHz &&
    prev.showPeakValleyMarkers === next.showPeakValleyMarkers
  );
};

export const ChartCanvas = memo(ChartCanvasBase, areEqual);
