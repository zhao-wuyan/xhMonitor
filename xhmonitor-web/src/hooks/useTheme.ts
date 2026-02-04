import { useCallback, useEffect } from 'react';
import { useLayout } from '../contexts/LayoutContext';
import type { LayoutState } from './useLayoutState';

const COLOR_KEYS = ['cpu', 'ram', 'gpu', 'vram', 'net', 'pwr'] as const;

const applyTheme = (state: LayoutState) => {
  if (typeof document === 'undefined') return;

  const root = document.documentElement;
  root.style.setProperty('--xh-grid-columns', String(state.gridColumns));
  root.style.setProperty('--xh-grid-columns-effective', String(state.gridColumns));
  root.style.setProperty('--xh-grid-gap', `${state.gaps.grid}px`);
  root.style.setProperty('--xh-bg-blur-opacity', String(state.background.blurOpacity));
  root.style.setProperty('--xh-glass-opacity', String(state.background.glassOpacity));
  root.style.setProperty('--xh-bg-image', state.background.imageDataUrl ? `url("${state.background.imageDataUrl}")` : 'none');
  root.style.setProperty('--xh-bg-image-blur', `${state.background.imageBlurPx}px`);

  COLOR_KEYS.forEach((key) => {
    root.style.setProperty(`--xh-color-${key}`, state.themeColors[key]);
  });

  document.body.classList.toggle('no-gradient', !state.background.gradient);
  document.body.classList.toggle('has-bg-image', Boolean(state.background.imageDataUrl));
};

export const useTheme = () => {
  const { layoutState } = useLayout();

  const updateTheme = useCallback((state: LayoutState) => {
    applyTheme(state);
  }, []);

  useEffect(() => {
    updateTheme(layoutState);
  }, [layoutState, updateTheme]);

  return { updateTheme };
};
