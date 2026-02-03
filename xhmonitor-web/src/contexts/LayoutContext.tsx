import { createContext, useContext, useMemo } from 'react';
import type { ReactNode } from 'react';
import { useLayoutState } from '../hooks/useLayoutState';
import type { LayoutState, LayoutStateUpdate } from '../hooks/useLayoutState';

interface LayoutContextValue {
  layoutState: LayoutState;
  updateLayout: (update: LayoutStateUpdate) => void;
}

const LayoutContext = createContext<LayoutContextValue | undefined>(undefined);

interface LayoutProviderProps {
  children: ReactNode;
}

export const LayoutProvider = ({ children }: LayoutProviderProps) => {
  const { layoutState, updateLayout } = useLayoutState();

  const value = useMemo(
    () => ({
      layoutState,
      updateLayout,
    }),
    [layoutState, updateLayout]
  );

  return <LayoutContext.Provider value={value}>{children}</LayoutContext.Provider>;
};

export const useLayout = () => {
  const context = useContext(LayoutContext);
  if (!context) {
    throw new Error('useLayout must be used within a LayoutProvider');
  }
  return context;
};
