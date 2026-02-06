import { useMemo, useRef, useState } from 'react';
import type { ChangeEventHandler } from 'react';
import { useLayout } from '../contexts/LayoutContext';
import { useTheme } from '../hooks/useTheme';
import { t } from '../i18n';
import { clearBackgroundImage, saveBackgroundImageBlob } from '../utils/backgroundImageStore';

interface SettingsDrawerProps {
  open?: boolean;
  onOpenChange?: (open: boolean) => void;
  className?: string;
  showTrigger?: boolean;
}

const GRID_PRESETS = [1, 2, 3];
const THEME_COLOR_ITEMS = [
  { key: 'cpu', label: 'CPU' },
  { key: 'ram', label: 'RAM' },
  { key: 'gpu', label: 'GPU' },
  { key: 'vram', label: 'VRAM' },
  { key: 'net', label: 'NET' },
  { key: 'pwr', label: 'PWR' },
] as const;

export const SettingsDrawer = ({
  open,
  onOpenChange,
  className,
  showTrigger = false,
}: SettingsDrawerProps) => {
  const { layoutState, updateLayout, resetLayout } = useLayout();
  const [internalOpen, setInternalOpen] = useState(false);
  const isOpen = open ?? internalOpen;
  const setOpen = onOpenChange ?? setInternalOpen;
  const imageInputRef = useRef<HTMLInputElement | null>(null);

  useTheme();

  const gridColumns = layoutState.gridColumns;
  const gridGap = layoutState.gaps.grid;

  const gridValue = useMemo(() => Math.max(1, Math.min(6, gridColumns)), [gridColumns]);

  const handleGridPreset = (value: number) => {
    updateLayout({ gridColumns: value });
  };

  const handleGridColumns = (value: number) => {
    const nextValue = Math.max(1, Math.min(6, value || 1));
    updateLayout({ gridColumns: nextValue });
  };

  const handleGridGap = (value: number) => {
    updateLayout({ gaps: { grid: value } });
  };

  const handleDragModeChange: ChangeEventHandler<HTMLSelectElement> = (event) => {
    const value = event.target.value;
    updateLayout({ dragMode: value === 'swap' ? 'swap' : 'sort' });
  };

  const handleVisibilityChange = (key: keyof typeof layoutState.visibility, value: boolean) => {
    updateLayout({ visibility: { [key]: value } });
  };

  const handlePeakValleyMarkersToggle = (value: boolean) => {
    updateLayout({ showPeakValleyMarkers: value });
  };

  const handleGradientToggle = (value: boolean) => {
    updateLayout({ background: { gradient: value } });
  };

  const handleBlurOpacity = (value: number) => {
    updateLayout({ background: { blurOpacity: value } });
  };

  const handleGlassOpacity = (value: number) => {
    updateLayout({ background: { glassOpacity: value } });
  };

  const handleBackgroundImageBlur = (value: number) => {
    updateLayout({ background: { imageBlurPx: value } });
  };

  const handlePickBackgroundImage = () => {
    imageInputRef.current?.click();
  };

  const handleBackgroundImageSelected = (file: File) => {
    void saveBackgroundImageBlob(file).then(() => {
      const url = URL.createObjectURL(file);
      updateLayout({ background: { imageDataUrl: url, imageStored: true } });
    });
  };

  const handleBackgroundImageChange: ChangeEventHandler<HTMLInputElement> = (event) => {
    const file = event.target.files?.[0];
    if (!file) return;
    handleBackgroundImageSelected(file);
    event.target.value = '';
  };

  const handleRemoveBackgroundImage = () => {
    void clearBackgroundImage().finally(() => {
      updateLayout({ background: { imageDataUrl: null, imageStored: false } });
    });
  };

  const handleThemeColorChange = (key: (typeof THEME_COLOR_ITEMS)[number]['key'], value: string) => {
    updateLayout({ themeColors: { [key]: value } });
  };

  return (
    <>
      {showTrigger && (
        <button
          type="button"
          className="settings-trigger"
          onClick={() => setOpen(true)}
        >
          {t('Settings')}
        </button>
      )}
      <div
        className={`settings-backdrop ${isOpen ? 'is-open' : ''}`.trim()}
        onClick={() => setOpen(false)}
        aria-hidden={!isOpen}
      />
      <aside
        className={`settings-drawer ${isOpen ? 'is-open' : ''} ${className ?? ''}`.trim()}
        aria-hidden={!isOpen}
      >
        <div className="settings-drawer__header">
          <span>{t('Layout Settings')}</span>
          <div className="settings-drawer__header-actions">
            <button type="button" className="settings-pill" onClick={resetLayout}>
              {t('Restore Defaults')}
            </button>
            <button
              type="button"
              className="settings-close"
              onClick={() => setOpen(false)}
              aria-label={t('Close settings')}
            >
              ×
            </button>
          </div>
        </div>
        <div className="settings-drawer__content">
          <div className="settings-group">
            <div className="settings-group-title">{t('Grid Columns')}</div>
            <div className="settings-row settings-pills">
              {GRID_PRESETS.map((value) => (
                <button
                  key={`preset-${value}`}
                  type="button"
                  className={`settings-pill ${gridValue === value ? 'active' : ''}`.trim()}
                  onClick={() => handleGridPreset(value)}
                >
                  {value}
                </button>
              ))}
            </div>
            <div className="settings-row">
              <input
                type="number"
                className="settings-input"
                min={1}
                max={6}
                value={gridValue}
                onChange={(event) => handleGridColumns(Number(event.target.value))}
              />
            </div>
          </div>

          <div className="settings-group">
            <div className="settings-group-title">{t('Grid Gap')}</div>
            <div className="settings-inline-row">
              <div className="settings-inline-label">{t('Current')}</div>
              <input
                type="range"
                className="settings-range"
                min={8}
                max={32}
                step={1}
                value={gridGap}
                onChange={(event) => handleGridGap(Number(event.target.value))}
              />
              <div className="settings-inline-value">{gridGap}px</div>
            </div>
          </div>

          <div className="settings-group">
            <div className="settings-group-title">{t('Card Drag Mode')}</div>
            <div className="settings-row">
              <select
                className="settings-input"
                value={layoutState.dragMode}
                onChange={handleDragModeChange}
              >
                <option value="sort">{t('Sort (Reorder)')}</option>
                <option value="swap">{t('Swap (Drop)')}</option>
              </select>
            </div>
          </div>

          <div className="settings-group">
            <div className="settings-group-title">{t('Visibility')}</div>
            <div className="settings-toggle-list">
              <label className="settings-toggle">
                <input
                  type="checkbox"
                  checked={layoutState.visibility.disk}
                  onChange={(event) => handleVisibilityChange('disk', event.target.checked)}
                />
                <span>{t('Disk')}</span>
              </label>
              <label className="settings-toggle">
                <input
                  type="checkbox"
                  checked={layoutState.visibility.cards}
                  onChange={(event) => handleVisibilityChange('cards', event.target.checked)}
                />
                <span>{t('Cards')}</span>
              </label>
              <label className="settings-toggle">
                <input
                  type="checkbox"
                  checked={layoutState.visibility.process}
                  onChange={(event) => handleVisibilityChange('process', event.target.checked)}
                />
                <span>{t('Process')}</span>
              </label>
            </div>
          </div>

            <div className="settings-group">
              <div className="settings-group-title">{t('Chart')}</div>
              <div className="settings-inline-row settings-inline-row--actions">
                <label className="settings-inline-label" htmlFor="setting-peak-valley-markers">
                  {t('Peak/Valley Labels')}
                </label>
                <label className="settings-switch">
                  <input
                    id="setting-peak-valley-markers"
                    type="checkbox"
                    checked={layoutState.showPeakValleyMarkers}
                    onChange={(event) => handlePeakValleyMarkersToggle(event.target.checked)}
                  />
                  <span className="settings-switch__track" aria-hidden="true" />
                </label>
              </div>
            </div>

          <div className="settings-group">
            <div className="settings-group-title">{t('Background')}</div>
            <div className="settings-inline-row settings-inline-row--actions">
              <label className="settings-toggle">
                <input
                  type="checkbox"
                  checked={layoutState.background.gradient}
                  onChange={(event) => handleGradientToggle(event.target.checked)}
                />
                <span>{t('Gradient')}</span>
              </label>
            </div>

            <div className="settings-inline-row">
              <div className="settings-inline-label">{t('Mask')}</div>
              <input
                type="range"
                className="settings-range"
                min={0}
                max={0.6}
                step={0.05}
                value={layoutState.background.blurOpacity}
                onChange={(event) => handleBlurOpacity(Number(event.target.value))}
              />
              <div className="settings-inline-value">
                {(layoutState.background.blurOpacity * 100).toFixed(0)}%
              </div>
            </div>

            <div className="settings-subsection-title">{t('Panel Opacity')}</div>
            <div className="settings-inline-row">
              <div className="settings-inline-label">{t('Opacity')}</div>
              <input
                type="range"
                className="settings-range"
                min={0.1}
                max={1}
                step={0.05}
                value={layoutState.background.glassOpacity}
                onChange={(event) => handleGlassOpacity(Number(event.target.value))}
              />
              <div className="settings-inline-value">
                {(layoutState.background.glassOpacity * 100).toFixed(0)}%
              </div>
            </div>

            <div className="settings-inline-row settings-inline-row--actions">
              <div className="settings-inline-label settings-inline-label--title">{t('Background Image')}</div>
              <div className="settings-inline-actions settings-pills">
                <button type="button" className="settings-pill" onClick={handlePickBackgroundImage}>
                  {t('Choose Image')}
                </button>
                {layoutState.background.imageDataUrl && (
                  <button type="button" className="settings-pill" onClick={handleRemoveBackgroundImage}>
                    {t('Remove Image')}
                  </button>
                )}
              </div>
            </div>
            <input
              ref={imageInputRef}
              type="file"
              accept="image/*"
              onChange={handleBackgroundImageChange}
              style={{ display: 'none' }}
            />

            <div className="settings-inline-row">
              <div className="settings-inline-label">{t('Blur')}</div>
              <input
                type="range"
                className="settings-range"
                min={0}
                max={48}
                step={1}
                value={layoutState.background.imageBlurPx}
                disabled={!layoutState.background.imageDataUrl}
                onChange={(event) => handleBackgroundImageBlur(Number(event.target.value))}
              />
              <div className="settings-inline-value">
                {layoutState.background.imageBlurPx}px
              </div>
            </div>
          </div>

          <div className="settings-group">
            <div className="settings-group-title">{t('Theme Colors')}</div>
            <div className="settings-color-grid">
              {THEME_COLOR_ITEMS.map((item) => (
                <label key={item.key} className="settings-color-item">
                  <span>{t(item.label)}</span>
                  <input
                    type="color"
                    value={layoutState.themeColors[item.key]}
                    onChange={(event) => handleThemeColorChange(item.key, event.target.value)}
                  />
                </label>
              ))}
            </div>
          </div>
        </div>
      </aside>
    </>
  );
};
