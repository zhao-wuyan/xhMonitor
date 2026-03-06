import { createContext, useContext } from 'react';
import { useMetricsHub } from '../hooks/useMetricsHub';
import type { ProcessMetricsSubscriptionMode } from '../hooks/useMetricsHub';

type MetricsHubContextValue = ReturnType<typeof useMetricsHub>;

const MetricsHubContext = createContext<MetricsHubContextValue | null>(null);

export const MetricsHubProvider = ({
  children,
  processMetricsMode,
}: {
  children: React.ReactNode;
  processMetricsMode?: ProcessMetricsSubscriptionMode;
}) => {
  const state = useMetricsHub({ processMetricsMode });
  return <MetricsHubContext.Provider value={state}>{children}</MetricsHubContext.Provider>;
};

export const useMetricsHubContext = () => {
  const ctx = useContext(MetricsHubContext);
  if (!ctx) {
    throw new Error('useMetricsHubContext must be used within MetricsHubProvider');
  }
  return ctx;
};

