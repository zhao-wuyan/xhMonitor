import { useMemo, useState } from 'react';
import { useLayout } from '../contexts/LayoutContext';
import { useTheme } from '../hooks/useTheme';

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
  showTrigger = true,
}: SettingsDrawerProps) => {
  const { layoutState, updateLayout } = useLayout();
  const [internalOpen, setInternalOpen] = useState(false);
  const isOpen = open ?? internalOpen;
  const setOpen = onOpenChange ?? setInternalOpen;

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

  const handleVisibilityChange = (key: keyof typeof layoutState.visibility, value: boolean) => {
    updateLayout({ visibility: { [key]: value } });
  };

  const handleGradientToggle = (value: boolean) => {
    updateLayout({ background: { gradient: value } });
  };

  const handleBlurOpacity = (value: number) => {
    updateLayout({ background: { blurOpacity: value } });
  };

  const handleThemeColorChange = (key: (typeof THEME_COLOR_ITEMS)[number]['key'], value: string) => {
    updateLayout({ themeColors: { [key]: value } });
  };

  const handleDiskPosition = (value: 'left' | 'right') => {
    updateLayout({ diskPosition: value });
  };

  return (
    <>
      {showTrigger && (
        <button
          type="button"
          className="settings-trigger"
          onClick={() => setOpen(true)}
        >
          Settings
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
          <span>Layout Settings</span>
          <button
            type="button"
            className="settings-close"
            onClick={() => setOpen(false)}
            aria-label="Close settings"
          >
            ×
          </button>
        </div>
        <div className="settings-drawer__content">
          <div className="settings-group">
            <div className="settings-label">Grid Columns</div>
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
            <div className="settings-label">Grid Gap</div>
            <input
              type="range"
              className="settings-range"
              min={8}
              max={32}
              step={1}
              value={gridGap}
              onChange={(event) => handleGridGap(Number(event.target.value))}
            />
            <div className="settings-hint">Current: {gridGap}px</div>
          </div>

          <div className="settings-group">
            <div className="settings-label">Visibility</div>
            <div className="settings-toggle-list">
              <label className="settings-toggle">
                <input
                  type="checkbox"
                  checked={layoutState.visibility.header}
                  onChange={(event) => handleVisibilityChange('header', event.target.checked)}
                />
                <span>Header</span>
              </label>
              <label className="settings-toggle">
                <input
                  type="checkbox"
                  checked={layoutState.visibility.disk}
                  onChange={(event) => handleVisibilityChange('disk', event.target.checked)}
                />
                <span>Disk</span>
              </label>
              <label className="settings-toggle">
                <input
                  type="checkbox"
                  checked={layoutState.visibility.cards}
                  onChange={(event) => handleVisibilityChange('cards', event.target.checked)}
                />
                <span>Cards</span>
              </label>
              <label className="settings-toggle">
                <input
                  type="checkbox"
                  checked={layoutState.visibility.process}
                  onChange={(event) => handleVisibilityChange('process', event.target.checked)}
                />
                <span>Process</span>
              </label>
            </div>
          </div>

          <div className="settings-group">
            <div className="settings-label">Background</div>
            <label className="settings-toggle">
              <input
                type="checkbox"
                checked={layoutState.background.gradient}
                onChange={(event) => handleGradientToggle(event.target.checked)}
              />
              <span>Gradient</span>
            </label>
            <div className="settings-row">
              <input
                type="range"
                className="settings-range"
                min={0}
                max={0.6}
                step={0.05}
                value={layoutState.background.blurOpacity}
                onChange={(event) => handleBlurOpacity(Number(event.target.value))}
              />
            </div>
            <div className="settings-hint">
              Mask: {(layoutState.background.blurOpacity * 100).toFixed(0)}%
            </div>
          </div>

          <div className="settings-group">
            <div className="settings-label">Disk Position</div>
            <div className="settings-row settings-radio-group">
              <label className="settings-radio">
                <input
                  type="radio"
                  name="disk-position"
                  checked={layoutState.diskPosition === 'left'}
                  onChange={() => handleDiskPosition('left')}
                />
                <span>Left</span>
              </label>
              <label className="settings-radio">
                <input
                  type="radio"
                  name="disk-position"
                  checked={layoutState.diskPosition === 'right'}
                  onChange={() => handleDiskPosition('right')}
                />
                <span>Right</span>
              </label>
            </div>
          </div>

          <div className="settings-group">
            <div className="settings-label">Theme Colors</div>
            <div className="settings-color-grid">
              {THEME_COLOR_ITEMS.map((item) => (
                <label key={item.key} className="settings-color-item">
                  <span>{item.label}</span>
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
