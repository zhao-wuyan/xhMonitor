import { createContext, useContext } from 'react';
import type { TimeSeriesResult } from '../hooks/useTimeSeries';

export const TimeSeriesContext = createContext<TimeSeriesResult | null>(null);

export const useTimeSeries = () => {
  const context = useContext(TimeSeriesContext);
  if (!context) {
    throw new Error('useTimeSeries must be used within TimeSeriesProvider');
  }
  return context;
};

