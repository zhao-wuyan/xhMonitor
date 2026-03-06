import { TimeSeriesContext } from './useTimeSeriesContext';
import { useTimeSeries as useTimeSeriesHook } from '../hooks/useTimeSeries';
import type { TimeSeriesOptions } from '../hooks/useTimeSeries';

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
