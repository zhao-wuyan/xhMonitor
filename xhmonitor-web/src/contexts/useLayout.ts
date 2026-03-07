import { createContext, useContext } from 'react';
import type { LayoutState, LayoutStateUpdate } from '../hooks/useLayoutState';

export interface LayoutContextValue {
  layoutState: LayoutState;
  updateLayout: (update: LayoutStateUpdate) => void;
  resetLayout: () => void;
}

export const LayoutContext = createContext<LayoutContextValue | undefined>(undefined);

export const useLayout = () => {
  const context = useContext(LayoutContext);
  if (!context) {
    throw new Error('useLayout must be used within a LayoutProvider');
  }
  return context;
};

