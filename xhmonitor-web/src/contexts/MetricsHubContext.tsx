import { MetricsHubContext } from './useMetricsHubContext';
import { useMetricsHub } from '../hooks/useMetricsHub';
import type { ProcessMetricsSubscriptionMode } from '../hooks/useMetricsHub';

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
