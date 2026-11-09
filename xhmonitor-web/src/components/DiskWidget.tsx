import { useEffect, useId, useLayoutEffect, useMemo, useRef, useState } from 'react';
import { createPortal } from 'react-dom';
import { Pin, PinOff } from 'lucide-react';
import { useLayout } from '../contexts/useLayout';
import type { DiskUsage } from '../types';

interface DiskWidgetProps {
  disks?: DiskUsage[];
}

const DISK_COLORS = ['#3b82f6', '#10b981', '#f59e0b', '#ef4444', '#a855f7', '#22c55e'];

const formatSize = (bytes: number | null) => {
  if (bytes == null || !Number.isFinite(bytes) || bytes < 0) return '未知';
  const gib = bytes / (1024 * 1024 * 1024);
  if (gib >= 1024) return `${(gib / 1024).toFixed(1)}T`;
  return `${gib.toFixed(0)}G`;
};

const formatSpeed = (value: number | null) => {
  if (value == null || !Number.isFinite(value) || value < 0) return '-';
  const formatted = value < 10 ? value.toFixed(1) : value.toFixed(0);
  return `${formatted.padStart(4, '\u00A0')}M`;
};

const DiskCard = ({ disk, color }: { disk: DiskUsage; color: string }) => {
  const { layoutState, updateLayout } = useLayout();
  const [open, setOpen] = useState(false);
  const [position, setPosition] = useState({ top: 0, left: 0, width: 240, maxHeight: 320 });
  const triggerRef = useRef<HTMLDivElement>(null);
  const panelRef = useRef<HTMLDivElement>(null);
  const timerRef = useRef<number | undefined>(undefined);
  const panelId = useId();
  const volumes = disk.volumes.filter((volume) => volume.id.trim() && volume.label.trim() && volume.mountPath.trim());
  const pinId = layoutState.diskPins[disk.id];
  const pinned = volumes.find((volume) => volume.id === pinId);
  const entries = [
    ...(pinId ? [{ kind: 'disk' as const, data: disk }] : []),
    ...volumes.map((volume) => ({ kind: 'volume' as const, data: volume })),
  ];
  const display = pinned ?? disk;
  const label = pinned ? `${pinned.label} (${pinned.mountPath})` : disk.name;
  const hasTotal = display.totalBytes != null && Number.isFinite(display.totalBytes) && display.totalBytes >= 0;
  const hasUsed = display.usedBytes != null && Number.isFinite(display.usedBytes) && display.usedBytes >= 0;
  const usedPercent = hasTotal && hasUsed && display.totalBytes! > 0
    ? Math.min(100, Math.max(0, (display.usedBytes! / display.totalBytes!) * 100)) : null;
  const remainingBytes = hasTotal && hasUsed && display.usedBytes! <= display.totalBytes!
    ? display.totalBytes! - display.usedBytes! : null;
  const temperature = disk.temperatureCelsius;
  const temperatureText = temperature != null && Number.isFinite(temperature)
    ? `${temperature.toFixed(0)}°C` : null;
  const sizeText = `${formatSize(display.usedBytes)}/${formatSize(display.totalBytes)}`;
  const missingPin = pinId && !pinned ? '固定卷不可用' : '';
  const description = `${disk.name} · ${pinned ? label : disk.usedBytes == null ? '容量未知' : '命名卷汇总'}${missingPin ? ` · ${missingPin}` : ''}`;

  const clearTimer = () => {
    window.clearTimeout(timerRef.current);
    timerRef.current = undefined;
  };
  const close = () => {
    clearTimer();
    setOpen(false);
  };
  const scheduleClose = () => {
    clearTimer();
    timerRef.current = window.setTimeout(() => setOpen(false), 140);
  };
  const changePin = (volumeId: string | null) => {
    updateLayout((previous) => {
      const diskPins = { ...previous.diskPins };
      if (volumeId === null) delete diskPins[disk.id];
      else diskPins[disk.id] = volumeId;
      return { ...previous, diskPins };
    });
  };

  useEffect(() => () => {
    window.clearTimeout(timerRef.current);
  }, []);

  useLayoutEffect(() => {
    if (!open) return;
    const reposition = () => {
      const rect = triggerRef.current?.getBoundingClientRect();
      if (!rect) return;
      const width = Math.min(Math.max(240, rect.width), window.innerWidth - 16);
      const top = rect.bottom + 6;
      setPosition({
        top,
        left: Math.max(8, Math.min(rect.left, window.innerWidth - width - 8)),
        width,
        maxHeight: Math.max(0, Math.min(360, window.innerHeight - top - 8)),
      });
    };
    reposition();
    window.addEventListener('resize', reposition);
    window.addEventListener('scroll', reposition, true);
    return () => {
      window.removeEventListener('resize', reposition);
      window.removeEventListener('scroll', reposition, true);
    };
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const outside = (event: PointerEvent) => {
      const target = event.target as Node;
      if (!triggerRef.current?.contains(target) && !panelRef.current?.contains(target)) {
        window.clearTimeout(timerRef.current);
        setOpen(false);
      }
    };
    const escape = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      window.clearTimeout(timerRef.current);
      if (panelRef.current?.contains(document.activeElement)) triggerRef.current?.focus();
      setOpen(false);
    };
    document.addEventListener('pointerdown', outside);
    document.addEventListener('keydown', escape);
    return () => {
      document.removeEventListener('pointerdown', outside);
      document.removeEventListener('keydown', escape);
    };
  }, [open]);

  return (
    <>
      <div
        ref={triggerRef}
        className="disk-item"
        data-disk-id={disk.id}
        data-pinned-volume={pinned?.id}
        role="button"
        tabIndex={0}
        aria-label={`${label}，查看卷列表`}
        aria-haspopup="dialog"
        aria-expanded={open}
        aria-controls={open ? panelId : undefined}
        title={description}
        onPointerEnter={(event) => {
          if (event.pointerType === 'touch') return;
          clearTimer();
          timerRef.current = window.setTimeout(() => setOpen(true), 100);
        }}
        onPointerLeave={scheduleClose}
        onFocus={(event) => {
          if (event.currentTarget.matches(':focus-visible')) {
            clearTimer();
            setOpen(true);
          }
        }}
        onBlur={(event) => {
          if (!panelRef.current?.contains(event.relatedTarget)) close();
        }}
        onClick={() => {
          clearTimer();
          setOpen((previous) => !previous);
        }}
        onKeyDown={(event) => {
          if (event.key === 'Enter' || event.key === ' ' || event.key === 'ArrowDown') {
            event.preventDefault();
            clearTimer();
            setOpen(true);
            requestAnimationFrame(() => panelRef.current?.querySelector<HTMLButtonElement>('button')?.focus());
          } else if (event.key === 'Tab' && !event.shiftKey && open) {
            const firstButton = panelRef.current?.querySelector<HTMLButtonElement>('button');
            if (firstButton) {
              event.preventDefault();
              firstButton.focus();
            }
          }
        }}
      >
        <div className="disk-title-row">
          <div className="disk-label">{label}</div>
          {(remainingBytes != null || temperatureText != null) && (
            <div className="disk-title-metrics">
              {remainingBytes != null && <span className="disk-info-text" title={`剩余空间：${formatSize(remainingBytes)}`}>{formatSize(remainingBytes)}</span>}
              {temperatureText != null && <span className="disk-info-text" title={`物理盘温度：${temperatureText}`}>{temperatureText}</span>}
            </div>
          )}
        </div>
        <div className="disk-details-row">
          <div className="disk-bar-container" title={usedPercent == null ? description : `已用空间：${usedPercent.toFixed(0)}%。${description}`}>
            <div className="disk-bar-bg">
              <div className="disk-bar-fill" style={{ width: `${usedPercent ?? 0}%`, backgroundColor: color }} />
            </div>
          </div>
          <span className="disk-info-text" title={sizeText}>{sizeText}</span>
          <div className="disk-speed-group" title="物理盘读写速率">
            <span className="speed-r">R:<span className="speed-val">{formatSpeed(disk.readSpeed)}</span></span>
            <span className="speed-w">W:<span className="speed-val">{formatSpeed(disk.writeSpeed)}</span></span>
          </div>
        </div>
      </div>
      {open && createPortal(
        <div
          id={panelId}
          ref={panelRef}
          className="disk-volume-panel xh-glass-panel"
          role="dialog"
          aria-label={`${disk.name} 的卷`}
          data-disk-id={disk.id}
          style={position}
          onPointerEnter={clearTimer}
          onPointerLeave={scheduleClose}
          onFocus={clearTimer}
          onBlur={(event) => {
            if (!event.currentTarget.contains(event.relatedTarget) && !triggerRef.current?.contains(event.relatedTarget)) close();
          }}
          onKeyDown={(event) => {
            if (event.key === 'Tab' && event.shiftKey && event.target === panelRef.current?.querySelector('button')) {
              event.preventDefault();
              triggerRef.current?.focus();
            }
          }}
        >
          {volumes.length === 0 && <div className="disk-volume-empty">暂无可显示的卷</div>}
          <div className="disk-volume-list">
            {entries.map((entry) => {
              const { totalBytes, usedBytes } = entry.data;
              const percent = totalBytes != null && totalBytes > 0 && usedBytes != null
                ? Math.min(100, Math.max(0, usedBytes / totalBytes * 100)) : null;
              const capacity = `${formatSize(usedBytes)}/${formatSize(totalBytes)}`;
              const volumeName = entry.kind === 'disk' ? entry.data.name
                : `${entry.data.mountPath.replace(/\\$/, '')} ${entry.data.label}`;
              const isPinned = entry.kind === 'volume' && pinId === entry.data.id;
              const actionLabel = entry.kind === 'disk' ? '取消固定，显示磁盘'
                : `${isPinned ? '取消固定' : '固定'} ${entry.data.label}`;
              return (
                <div className="disk-item disk-volume-row" key={`${entry.kind}:${entry.data.id}`} data-volume-id={entry.kind === 'volume' ? entry.data.id : undefined} data-physical-disk={entry.kind === 'disk' ? disk.id : undefined} data-pinned={isPinned}>
                  <div className="disk-volume-summary">
                    <div className="disk-volume-title">
                      <span className="disk-label" title={volumeName}>{volumeName}</span>
                      <span className="disk-info-text">{entry.kind === 'disk' ? '汇总' : entry.data.fileSystem}</span>
                    </div>
                    <div className="disk-details-row">
                      <div className="disk-bar-container" title={percent == null ? '未知' : `${percent.toFixed(0)}%`}>
                        <div className="disk-bar-bg">
                          <div className="disk-bar-fill" style={{ width: `${percent ?? 0}%`, backgroundColor: color }} />
                        </div>
                      </div>
                      <span className="disk-info-text" title={capacity}>{capacity}</span>
                      <span className="disk-info-text disk-volume-percent">{percent == null ? '—' : `${percent.toFixed(0)}%`}</span>
                    </div>
                  </div>
                  <button
                    type="button"
                    className={entry.kind === 'disk' ? 'disk-unpin' : 'disk-volume-pin'}
                    aria-label={actionLabel}
                    aria-pressed={entry.kind === 'volume' ? isPinned : undefined}
                    title={actionLabel}
                    onClick={() => changePin(entry.kind === 'disk' || isPinned ? null : entry.data.id)}
                  >{entry.kind === 'disk' ? <PinOff size={14} aria-hidden="true" /> : <Pin size={14} aria-hidden="true" />}</button>
                </div>
              );
            })}
          </div>
        </div>, document.body,
      )}
    </>
  );
};

export const DiskWidget = ({ disks }: DiskWidgetProps) => {
  const { layoutState } = useLayout();
  const [isMobile, setIsMobile] = useState(() => {
    if (typeof window === 'undefined') return false;
    return window.matchMedia('(max-width: 1023px)').matches;
  });

  useEffect(() => {
    if (typeof window === 'undefined') return;
    const media = window.matchMedia('(max-width: 1023px)');
    const handler = (event: MediaQueryListEvent) => setIsMobile(event.matches);
    media.addEventListener('change', handler);
    return () => media.removeEventListener('change', handler);
  }, []);

  const validDisks = useMemo(() => disks?.filter((disk) => disk.id?.trim() && disk.name?.trim()) ?? [], [disks]);
  if (!layoutState.visibility.disk || validDisks.length === 0) return null;
  const positionClass = layoutState.diskPosition === 'right' ? 'disk-widget--right' : 'disk-widget--left';
  const mobileOrder = isMobile ? (layoutState.diskPosition === 'right' ? 2 : 1) : undefined;

  return (
    <div
      className={`disk-widget ${positionClass}${isMobile ? ' disk-widget--mobile' : ''}`}
      data-position={layoutState.diskPosition}
      data-mobile={isMobile ? 'true' : 'false'}
      style={mobileOrder ? { order: mobileOrder } : undefined}
    >
      <div className="disk-info-container">
        {validDisks.map((disk, index) => <DiskCard key={disk.id} disk={disk} color={DISK_COLORS[index % DISK_COLORS.length]} />)}
      </div>
    </div>
  );
};
