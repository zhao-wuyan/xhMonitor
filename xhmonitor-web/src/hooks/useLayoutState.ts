import { useCallback, useEffect, useRef, useState } from 'react';
import { clearBackgroundImage, loadBackgroundImageBlob } from '../utils/backgroundImageStore';

export interface LayoutGaps {
  grid: number;
}

export interface LayoutVisibility {
  header: boolean;
  disk: boolean;
  cards: boolean;
  process: boolean;
}

export interface LayoutBackground {
  gradient: boolean;
  blurOpacity: number;
  imageDataUrl: string | null;
  imageBlurPx: number;
  imageStored: boolean;
}

export interface ThemeColors {
  cpu: string;
  ram: string;
  gpu: string;
  vram: string;
  net: string;
  pwr: string;
}

export type DiskPosition = 'left' | 'right';
export type DragMode = 'sort' | 'swap';

export interface LayoutState {
  gridColumns: number;
  gaps: LayoutGaps;
  cardOrder: string[];
  visibility: LayoutVisibility;
  background: LayoutBackground;
  themeColors: ThemeColors;
  diskPosition: DiskPosition;
  dragMode: DragMode;
}

export type LayoutStatePatch = Omit<
  Partial<LayoutState>,
  'gaps' | 'visibility' | 'background' | 'themeColors'
> & {
  gaps?: Partial<LayoutGaps>;
  visibility?: Partial<LayoutVisibility>;
  background?: Partial<LayoutBackground>;
  themeColors?: Partial<ThemeColors>;
};

export type LayoutStateUpdate = LayoutStatePatch | ((prev: LayoutState) => LayoutState);

const STORAGE_KEY = 'xhmonitor.layoutState';
const LAYOUT_STATE_VERSION = '1.0' as const;

const DEFAULT_LAYOUT_STATE: LayoutState = {
  gridColumns: 3,
  gaps: {
    grid: 16,
  },
  cardOrder: ['cpu', 'ram', 'gpu', 'vram', 'net', 'pwr'],
  visibility: {
    header: true,
    disk: true,
    cards: true,
    process: true,
  },
  background: {
    gradient: true,
    blurOpacity: 0.3,
    imageDataUrl: null,
    imageBlurPx: 18,
    imageStored: false,
  },
  themeColors: {
    cpu: '#3b82f6',
    ram: '#8b5cf6',
    gpu: '#10b981',
    vram: '#f59e0b',
    net: '#0ea5e9',
    pwr: '#f43f5e',
  },
  diskPosition: 'left',
  dragMode: 'sort',
};

const canUseStorage = () =>
  typeof window !== 'undefined' && typeof window.localStorage !== 'undefined';

const isRecord = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null;

const toFiniteNumber = (value: unknown, fallback: number, min?: number): number => {
  if (typeof value !== 'number' || !Number.isFinite(value)) return fallback;
  if (min === undefined) return value;
  return Math.max(min, value);
};

const toStringValue = (value: unknown, fallback: string): string =>
  typeof value === 'string' ? value : fallback;

const toBooleanValue = (value: unknown, fallback: boolean): boolean =>
  typeof value === 'boolean' ? value : fallback;

const toOpacityValue = (value: unknown, fallback: number): number => {
  if (typeof value !== 'number' || !Number.isFinite(value)) return fallback;
  return Math.min(1, Math.max(0, value));
};

const toBlurPxValue = (value: unknown, fallback: number): number => {
  if (typeof value !== 'number' || !Number.isFinite(value)) return fallback;
  return Math.min(48, Math.max(0, value));
};

const toNullableStringValue = (value: unknown, fallback: string | null): string | null => {
  if (value === null) return null;
  if (typeof value === 'string') return value;
  return fallback;
};

const toDiskPosition = (value: unknown, fallback: DiskPosition): DiskPosition =>
  value === 'right' ? 'right' : fallback;

const toDragMode = (value: unknown, fallback: DragMode): DragMode =>
  value === 'swap' ? 'swap' : fallback;

const normalizeLayoutState = (state: LayoutState): LayoutState => {
  const gridColumns = Math.max(1, Math.round(state.gridColumns));
  const cardOrder =
    Array.isArray(state.cardOrder) && state.cardOrder.every((id) => typeof id === 'string')
      ? state.cardOrder
      : DEFAULT_LAYOUT_STATE.cardOrder;
  const diskPosition = toDiskPosition(state.diskPosition, DEFAULT_LAYOUT_STATE.diskPosition);
  const dragMode = toDragMode(state.dragMode, DEFAULT_LAYOUT_STATE.dragMode);

  return {
    gridColumns,
    gaps: {
      grid: toFiniteNumber(state.gaps.grid, DEFAULT_LAYOUT_STATE.gaps.grid, 0),
    },
    cardOrder: cardOrder.length > 0 ? cardOrder : DEFAULT_LAYOUT_STATE.cardOrder,
    visibility: {
      header: toBooleanValue(state.visibility.header, DEFAULT_LAYOUT_STATE.visibility.header),
      disk: toBooleanValue(state.visibility.disk, DEFAULT_LAYOUT_STATE.visibility.disk),
      cards: toBooleanValue(state.visibility.cards, DEFAULT_LAYOUT_STATE.visibility.cards),
      process: toBooleanValue(state.visibility.process, DEFAULT_LAYOUT_STATE.visibility.process),
    },
    background: {
      gradient: toBooleanValue(state.background.gradient, DEFAULT_LAYOUT_STATE.background.gradient),
      blurOpacity: toOpacityValue(
        state.background.blurOpacity,
        DEFAULT_LAYOUT_STATE.background.blurOpacity
      ),
      imageDataUrl: toNullableStringValue(
        state.background.imageDataUrl,
        DEFAULT_LAYOUT_STATE.background.imageDataUrl
      ),
      imageBlurPx: toBlurPxValue(state.background.imageBlurPx, DEFAULT_LAYOUT_STATE.background.imageBlurPx),
      imageStored: toBooleanValue(state.background.imageStored, DEFAULT_LAYOUT_STATE.background.imageStored),
    },
    themeColors: {
      cpu: toStringValue(state.themeColors.cpu, DEFAULT_LAYOUT_STATE.themeColors.cpu),
      ram: toStringValue(state.themeColors.ram, DEFAULT_LAYOUT_STATE.themeColors.ram),
      gpu: toStringValue(state.themeColors.gpu, DEFAULT_LAYOUT_STATE.themeColors.gpu),
      vram: toStringValue(state.themeColors.vram, DEFAULT_LAYOUT_STATE.themeColors.vram),
      net: toStringValue(state.themeColors.net, DEFAULT_LAYOUT_STATE.themeColors.net),
      pwr: toStringValue(state.themeColors.pwr, DEFAULT_LAYOUT_STATE.themeColors.pwr),
    },
    diskPosition,
    dragMode,
  };
};

const mergeLayoutState = (state: LayoutState, patch: LayoutStatePatch): LayoutState => {
  return normalizeLayoutState({
    gridColumns: patch.gridColumns ?? state.gridColumns,
    gaps: {
      ...state.gaps,
      ...patch.gaps,
    },
    cardOrder: patch.cardOrder ?? state.cardOrder,
    visibility: {
      ...state.visibility,
      ...patch.visibility,
    },
    background: {
      ...state.background,
      ...patch.background,
    },
    themeColors: {
      ...state.themeColors,
      ...patch.themeColors,
    },
    diskPosition: patch.diskPosition ?? state.diskPosition,
    dragMode: patch.dragMode ?? state.dragMode,
  });
};

const parseStoredLayoutState = (raw: unknown): LayoutState | null => {
  if (!isRecord(raw)) return null;
  if (raw.version !== LAYOUT_STATE_VERSION) return null;
  if (!isRecord(raw.state)) return null;

  const stateValue = raw.state;
  const gapsValue = isRecord(stateValue.gaps) ? stateValue.gaps : {};
  const visibilityValue = isRecord(stateValue.visibility) ? stateValue.visibility : {};
  const backgroundValue = isRecord(stateValue.background) ? stateValue.background : {};
  const themeColorsValue = isRecord(stateValue.themeColors) ? stateValue.themeColors : {};

  const candidate: LayoutState = {
    gridColumns: toFiniteNumber(stateValue.gridColumns, DEFAULT_LAYOUT_STATE.gridColumns, 1),
    gaps: {
      grid: toFiniteNumber(gapsValue.grid, DEFAULT_LAYOUT_STATE.gaps.grid, 0),
    },
    cardOrder: Array.isArray(stateValue.cardOrder)
      ? stateValue.cardOrder.filter((id) => typeof id === 'string')
      : DEFAULT_LAYOUT_STATE.cardOrder,
    visibility: {
      header: toBooleanValue(visibilityValue.header, DEFAULT_LAYOUT_STATE.visibility.header),
      disk: toBooleanValue(visibilityValue.disk, DEFAULT_LAYOUT_STATE.visibility.disk),
      cards: toBooleanValue(visibilityValue.cards, DEFAULT_LAYOUT_STATE.visibility.cards),
      process: toBooleanValue(visibilityValue.process, DEFAULT_LAYOUT_STATE.visibility.process),
    },
    background: {
      gradient: toBooleanValue(backgroundValue.gradient, DEFAULT_LAYOUT_STATE.background.gradient),
      blurOpacity: toOpacityValue(
        backgroundValue.blurOpacity,
        DEFAULT_LAYOUT_STATE.background.blurOpacity
      ),
      imageDataUrl: toNullableStringValue(
        backgroundValue.imageDataUrl,
        DEFAULT_LAYOUT_STATE.background.imageDataUrl
      ),
      imageBlurPx: toBlurPxValue(backgroundValue.imageBlurPx, DEFAULT_LAYOUT_STATE.background.imageBlurPx),
      imageStored: toBooleanValue(backgroundValue.imageStored, DEFAULT_LAYOUT_STATE.background.imageStored),
    },
    themeColors: {
      cpu: toStringValue(themeColorsValue.cpu, DEFAULT_LAYOUT_STATE.themeColors.cpu),
      ram: toStringValue(themeColorsValue.ram, DEFAULT_LAYOUT_STATE.themeColors.ram),
      gpu: toStringValue(themeColorsValue.gpu, DEFAULT_LAYOUT_STATE.themeColors.gpu),
      vram: toStringValue(themeColorsValue.vram, DEFAULT_LAYOUT_STATE.themeColors.vram),
      net: toStringValue(themeColorsValue.net, DEFAULT_LAYOUT_STATE.themeColors.net),
      pwr: toStringValue(themeColorsValue.pwr, DEFAULT_LAYOUT_STATE.themeColors.pwr),
    },
    diskPosition: toDiskPosition(stateValue.diskPosition, DEFAULT_LAYOUT_STATE.diskPosition),
    dragMode: toDragMode(stateValue.dragMode, DEFAULT_LAYOUT_STATE.dragMode),
  };

  return normalizeLayoutState(candidate);
};

const loadLayoutState = (): LayoutState => {
  if (!canUseStorage()) return DEFAULT_LAYOUT_STATE;

  try {
    const stored = window.localStorage.getItem(STORAGE_KEY);
    if (!stored) return DEFAULT_LAYOUT_STATE;
    const parsed = JSON.parse(stored) as unknown;
    return parseStoredLayoutState(parsed) ?? DEFAULT_LAYOUT_STATE;
  } catch (error) {
    console.warn('Failed to load layout state from storage, using defaults.', error);
    return DEFAULT_LAYOUT_STATE;
  }
};

const persistLayoutState = (state: LayoutState) => {
  if (!canUseStorage()) return;

  try {
    // Do not persist large background images in localStorage (quota). The blob is stored in IndexedDB.
    const safeState =
      state.background.imageDataUrl != null
        ? {
            ...state,
            background: {
              ...state.background,
              imageDataUrl: null,
              imageStored: true,
            },
          }
        : state;
    const payload = {
      version: LAYOUT_STATE_VERSION,
      state: safeState,
    };
    window.localStorage.setItem(STORAGE_KEY, JSON.stringify(payload));
  } catch (error) {
    console.error('Failed to persist layout state:', error);
  }
};

export const useLayoutState = () => {
  const [layoutState, setLayoutState] = useState<LayoutState>(() => loadLayoutState());
  const backgroundUrlRef = useRef<string | null>(null);
  const prevBgUrlRef = useRef<string | null>(null);

  const resetLayout = useCallback(() => {
    if (backgroundUrlRef.current) {
      URL.revokeObjectURL(backgroundUrlRef.current);
      backgroundUrlRef.current = null;
    }
    void clearBackgroundImage();
    setLayoutState(() =>
      normalizeLayoutState({
        gridColumns: DEFAULT_LAYOUT_STATE.gridColumns,
        gaps: { ...DEFAULT_LAYOUT_STATE.gaps },
        cardOrder: [...DEFAULT_LAYOUT_STATE.cardOrder],
        visibility: { ...DEFAULT_LAYOUT_STATE.visibility },
        background: { ...DEFAULT_LAYOUT_STATE.background },
        themeColors: { ...DEFAULT_LAYOUT_STATE.themeColors },
        diskPosition: DEFAULT_LAYOUT_STATE.diskPosition,
        dragMode: DEFAULT_LAYOUT_STATE.dragMode,
      })
    );
  }, []);

  const updateLayout = useCallback((update: LayoutStateUpdate) => {
    setLayoutState((prev) => {
      if (typeof update === 'function') {
        return normalizeLayoutState(update(prev));
      }
      return mergeLayoutState(prev, update);
    });
  }, []);

  useEffect(() => {
    persistLayoutState(layoutState);
  }, [layoutState]);

  useEffect(() => {
    if (typeof window === 'undefined') return;

    const shouldLoad = layoutState.background.imageStored && !layoutState.background.imageDataUrl;
    if (!shouldLoad) return;

    let canceled = false;

    loadBackgroundImageBlob()
      .then((blob) => {
        if (canceled) return;
        if (!blob) {
          setLayoutState((prev) =>
            normalizeLayoutState({
              ...prev,
              background: {
                ...prev.background,
                imageStored: false,
              },
            })
          );
          return;
        }

        if (backgroundUrlRef.current) {
          URL.revokeObjectURL(backgroundUrlRef.current);
          backgroundUrlRef.current = null;
        }

        const url = URL.createObjectURL(blob);
        backgroundUrlRef.current = url;
        setLayoutState((prev) =>
          normalizeLayoutState({
            ...prev,
            background: {
              ...prev.background,
              imageDataUrl: url,
              imageStored: true,
            },
          })
        );
      })
      .catch(() => {
        // ignore load errors; user can re-select
      });

    return () => {
      canceled = true;
    };
  }, [layoutState.background.imageDataUrl, layoutState.background.imageStored]);

  useEffect(() => {
    const nextUrl = layoutState.background.imageDataUrl;
    const prevUrl = prevBgUrlRef.current;
    prevBgUrlRef.current = nextUrl;

    if (prevUrl && prevUrl !== nextUrl && prevUrl.startsWith('blob:')) {
      try {
        URL.revokeObjectURL(prevUrl);
      } catch {
        // ignore
      }
    }

    if (nextUrl && nextUrl.startsWith('blob:')) {
      backgroundUrlRef.current = nextUrl;
    }
  }, [layoutState.background.imageDataUrl]);

  useEffect(() => {
    return () => {
      if (backgroundUrlRef.current) {
        URL.revokeObjectURL(backgroundUrlRef.current);
        backgroundUrlRef.current = null;
      }
    };
  }, []);

  return {
    layoutState,
    updateLayout,
    resetLayout,
  };
};
