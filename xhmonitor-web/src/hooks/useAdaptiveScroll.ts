import { useCallback, useEffect, useRef, useState } from 'react';

export type AdaptiveScrollMode = 'page' | 'process';

export interface AdaptiveScrollState {
  mode: AdaptiveScrollMode;
  processTableMaxHeight: number;
}

export interface UseAdaptiveScrollOptions {
  enabled: boolean;
  shellRef: React.RefObject<HTMLElement | null>;
  processPanelRef: React.RefObject<HTMLElement | null>;
  processVisibleRatioThreshold?: number;
  processVisibleRatioRelease?: number;
  mobileBreakpointPx?: number;
  mobileBottomPaddingPx?: number;
  minProcessTableHeightPx?: number;
}

const clamp = (value: number, min: number, max: number) => Math.min(max, Math.max(min, value));

export const useAdaptiveScroll = ({
  enabled,
  shellRef,
  processPanelRef,
  processVisibleRatioThreshold = 0.5,
  processVisibleRatioRelease = 0.45,
  mobileBreakpointPx = 768,
  mobileBottomPaddingPx = 76,
  minProcessTableHeightPx = 160,
}: UseAdaptiveScrollOptions): AdaptiveScrollState => {
  const [state, setState] = useState<AdaptiveScrollState>({
    mode: 'page',
    processTableMaxHeight: 0,
  });

  const frameRef = useRef<number | null>(null);

  const recompute = useCallback(() => {
    if (typeof window === 'undefined') return;

    const shell = shellRef.current;
    const panel = processPanelRef.current;
    if (!shell || !panel) return;

    const viewportHeight = window.innerHeight;
    if (viewportHeight <= 0) return;

    const panelRect = panel.getBoundingClientRect();
    const visibleTop = clamp(panelRect.top, 0, viewportHeight);
    const visibleBottom = clamp(panelRect.bottom, 0, viewportHeight);
    const visibleHeight = Math.max(0, visibleBottom - visibleTop);
    const visibleRatio = visibleHeight / viewportHeight;

    const isMobile = window.matchMedia(`(max-width: ${mobileBreakpointPx}px)`).matches;
    const bottomPadding = isMobile ? mobileBottomPaddingPx : 0;

    const tableScroll = panel.querySelector<HTMLElement>('.table-scroll');
    const tableRect = tableScroll?.getBoundingClientRect();
    const processTableMaxHeight = tableRect
      ? Math.max(minProcessTableHeightPx, Math.floor(viewportHeight - tableRect.top - bottomPadding - 12))
      : 0;

    setState((prev) => {
      if (!enabled) {
        return {
          mode: 'page',
          processTableMaxHeight,
        };
      }

      const shouldEnterProcessMode = panelRect.top >= 0 && visibleRatio >= processVisibleRatioThreshold;
      const shouldExitProcessMode = panelRect.top < 0 || visibleRatio < processVisibleRatioRelease;

      let nextMode: AdaptiveScrollMode = prev.mode;
      if (prev.mode === 'page' && shouldEnterProcessMode) nextMode = 'process';
      if (prev.mode === 'process' && shouldExitProcessMode) nextMode = 'page';

      if (nextMode === prev.mode && processTableMaxHeight === prev.processTableMaxHeight) {
        return prev;
      }

      return {
        mode: nextMode,
        processTableMaxHeight,
      };
    });
  }, [
    enabled,
    mobileBottomPaddingPx,
    mobileBreakpointPx,
    minProcessTableHeightPx,
    processPanelRef,
    processVisibleRatioRelease,
    processVisibleRatioThreshold,
    shellRef,
  ]);

  const scheduleRecompute = useCallback(() => {
    if (typeof window === 'undefined') return;
    if (frameRef.current != null) return;
    frameRef.current = window.requestAnimationFrame(() => {
      frameRef.current = null;
      recompute();
    });
  }, [recompute]);

  useEffect(() => {
    if (typeof window === 'undefined') return;
    if (!enabled) return;

    let cleanup: null | (() => void) = null;
    let canceled = false;

    const tryAttach = () => {
      if (canceled) return;

      const shell = shellRef.current;
      const panel = processPanelRef.current;

      if (!shell || !panel) {
        window.requestAnimationFrame(tryAttach);
        return;
      }

      scheduleRecompute();

      const onResize = () => scheduleRecompute();
      window.addEventListener('resize', onResize, { passive: true });

      const onShellScroll = () => scheduleRecompute();
      shell.addEventListener('scroll', onShellScroll, { passive: true });

      const resizeObserver = new ResizeObserver(() => scheduleRecompute());
      resizeObserver.observe(panel);

      cleanup = () => {
        window.removeEventListener('resize', onResize);
        shell.removeEventListener('scroll', onShellScroll);
        resizeObserver.disconnect();
      };
    };

    tryAttach();

    return () => {
      canceled = true;
      cleanup?.();
    };
  }, [enabled, processPanelRef, scheduleRecompute, shellRef]);

  if (!enabled) {
    return {
      mode: 'page',
      processTableMaxHeight: 0,
    };
  }

  return state;
};
