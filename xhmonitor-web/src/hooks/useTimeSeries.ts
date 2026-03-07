import { useCallback, useEffect, useMemo, useState } from 'react';
import type { SystemUsage } from '../types';
import { useMetricsHubContext } from '../contexts/useMetricsHubContext';

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
  const { subscribeSystemUsage } = useMetricsHubContext();
  const maxLength = options.maxLength ?? 60;
  const selectors = useMemo(() => options.selectors ?? DEFAULT_SELECTORS, [options.selectors]);
  const [series, setSeries] = useState<Record<string, number[]>>(() =>
    createSeries(selectors, maxLength)
  );

  useEffect(() => {
    return subscribeSystemUsage((usage) => {
      setSeries((prev) => {
        const next = { ...prev };

        Object.keys(selectors).forEach((key) => {
          const value = selectors[key](usage);
          const prevSeries = prev[key] ?? new Array(maxLength).fill(0);
          next[key] = pushValue(prevSeries, value, maxLength);
        });

        return next;
      });
    });
  }, [maxLength, selectors, subscribeSystemUsage]);

  const seriesView = useMemo(() => {
    const keys = new Set<string>([...Object.keys(series), ...Object.keys(selectors)]);
    const normalized: Record<string, number[]> = {};

    keys.forEach((key) => {
      const values = series[key] ?? [];

      if (values.length === maxLength) {
        normalized[key] = values;
        return;
      }

      if (values.length > maxLength) {
        normalized[key] = values.slice(values.length - maxLength);
        return;
      }

      const padding = new Array(maxLength - values.length).fill(0);
      normalized[key] = [...padding, ...values];
    });

    return normalized;
  }, [maxLength, selectors, series]);

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
    return seriesView[key] ?? [];
  }, [seriesView]);

  return {
    series: seriesView,
    addDataPoint,
    getSeriesData,
  };
};
