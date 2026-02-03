import { createContext, useContext } from 'react';
import { useTimeSeries as useTimeSeriesHook } from '../hooks/useTimeSeries';
import type { TimeSeriesResult, TimeSeriesOptions } from '../hooks/useTimeSeries';

const TimeSeriesContext = createContext<TimeSeriesResult | null>(null);

interface TimeSeriesProviderProps {
  children: React.ReactNode;
  options?: TimeSeriesOptions;
}

export const TimeSeriesProvider = ({ children, options }: TimeSeriesProviderProps) => {
  const timeSeries = useTimeSeriesHook(options);
  return (
    <TimeSeriesContext.Provider value={timeSeries}>
      {children}
    </TimeSeriesContext.Provider>
  );
};

export const useTimeSeries = () => {
  const context = useContext(TimeSeriesContext);
  if (!context) {
    throw new Error('useTimeSeries must be used within TimeSeriesProvider');
  }
  return context;
};
