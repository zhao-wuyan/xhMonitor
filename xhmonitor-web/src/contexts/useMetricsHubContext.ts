import { createContext, useContext } from 'react';

type MetricsHubContextValue = ReturnType<typeof import('../hooks/useMetricsHub').useMetricsHub>;

export const MetricsHubContext = createContext<MetricsHubContextValue | null>(null);

export const useMetricsHubContext = () => {
  const ctx = useContext(MetricsHubContext);
  if (!ctx) {
    throw new Error('useMetricsHubContext must be used within MetricsHubProvider');
  }
  return ctx;
};

