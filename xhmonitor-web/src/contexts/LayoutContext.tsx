import { useMemo } from 'react';
import type { ReactNode } from 'react';
import { useLayoutState } from '../hooks/useLayoutState';
import { LayoutContext } from './useLayout';
import type { LayoutContextValue } from './useLayout';

interface LayoutProviderProps {
  children: ReactNode;
}

export const LayoutProvider = ({ children }: LayoutProviderProps) => {
  const { layoutState, updateLayout, resetLayout } = useLayoutState();

  const value = useMemo<LayoutContextValue>(
    () => ({
      layoutState,
      updateLayout,
      resetLayout,
    }),
    [layoutState, updateLayout, resetLayout]
  );

  return <LayoutContext.Provider value={value}>{children}</LayoutContext.Provider>;
};
