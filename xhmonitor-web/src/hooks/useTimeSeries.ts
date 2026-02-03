import { useCallback, useEffect, useMemo, useState } from 'react';
import type { SystemUsage } from '../types';
import { useMetricsHub } from './useMetricsHub';

export interface TimeSeriesOptions {
  maxLength?: number;
  selectors?: Record<string, (usage: SystemUsage) => number>;
}

export interface TimeSeriesResult {
  series: Record<string, number[]>;
  addDataPoint: (key: string, value: number) => void;
  getSeriesData: (key: string) => number[];
}

const DEFAULT_SELECTORS: Record<string, (usage: SystemUsage) => number> = {
  cpu: (usage) => usage.totalCpu,
  ram: (usage) => usage.totalMemory,
  gpu: (usage) => usage.totalGpu,
  vram: (usage) => usage.totalVram,
};

const createSeries = (
  selectors: Record<string, (usage: SystemUsage) => number>,
  maxLength: number
): Record<string, number[]> => {
  const result: Record<string, number[]> = {};
  Object.keys(selectors).forEach((key) => {
    result[key] = new Array(maxLength).fill(0);
  });
  return result;
};

const pushValue = (series: number[], value: number, maxLength: number): number[] => {
  const trimmed =
    series.length >= maxLength
      ? series.slice(series.length - maxLength + 1)
      : series.slice();

  trimmed.push(value);

  if (trimmed.length < maxLength) {
    const padding = new Array(maxLength - trimmed.length).fill(0);
    return [...padding, ...trimmed];
  }

  return trimmed;
};

export const useTimeSeries = (options: TimeSeriesOptions = {}): TimeSeriesResult => {
  const { systemUsage } = useMetricsHub();
  const maxLength = options.maxLength ?? 60;
  const selectors = useMemo(() => options.selectors ?? DEFAULT_SELECTORS, [options.selectors]);
  const [series, setSeries] = useState<Record<string, number[]>>(() =>
    createSeries(selectors, maxLength)
  );

  useEffect(() => {
    setSeries((prev) => {
      const next: Record<string, number[]> = {};
      let changed = false;

      Object.keys(selectors).forEach((key) => {
        const prevSeries = prev[key] ?? [];
        let updated = prevSeries;

        if (prevSeries.length !== maxLength) {
          if (prevSeries.length > maxLength) {
            updated = prevSeries.slice(prevSeries.length - maxLength);
          } else {
            const padding = new Array(maxLength - prevSeries.length).fill(0);
            updated = [...padding, ...prevSeries];
          }
        }

        next[key] = updated;
        if (updated !== prevSeries) changed = true;
      });

      return changed ? next : prev;
    });
  }, [maxLength, selectors]);

  useEffect(() => {
    if (!systemUsage) return;

    setSeries((prev) => {
      const next: Record<string, number[]> = {};
      let changed = false;

      Object.keys(selectors).forEach((key) => {
        const value = selectors[key](systemUsage);
        const prevSeries = prev[key] ?? new Array(maxLength).fill(0);
        const updated = pushValue(prevSeries, value, maxLength);
        next[key] = updated;
        if (updated !== prevSeries) changed = true;
      });

      return changed ? next : prev;
    });
  }, [systemUsage, selectors, maxLength]);

  const addDataPoint = useCallback(
    (key: string, value: number) => {
      setSeries((prev) => {
        const prevSeries = prev[key] ?? new Array(maxLength).fill(0);
        const updated = pushValue(prevSeries, value, maxLength);
        return {
          ...prev,
          [key]: updated,
        };
      });
    },
    [maxLength]
  );

  const getSeriesData = useCallback((key: string) => {
    return series[key] ?? [];
  }, [series]);

  return {
    series,
    addDataPoint,
    getSeriesData,
  };
};
