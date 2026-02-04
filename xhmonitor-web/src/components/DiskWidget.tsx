import { useEffect, useMemo, useState } from 'react';
import { useLayout } from '../contexts/LayoutContext';
import type { DiskUsage } from '../types';

interface DiskWidgetProps {
  disks?: DiskUsage[];
}

interface DiskViewModel extends DiskUsage {
  color: string;
}

const DISK_COLORS = ['#3b82f6', '#10b981', '#f59e0b', '#ef4444', '#a855f7', '#22c55e'];

const formatSize = (bytes: number) => {
  if (!Number.isFinite(bytes) || bytes <= 0) return '-';
  const gib = bytes / (1024 * 1024 * 1024);
  if (gib >= 1024) return `${(gib / 1024).toFixed(1)}T`;
  return `${gib.toFixed(0)}G`;
};

const formatSpeed = (value: number) => {
  const formatted = value < 10 ? value.toFixed(1) : value.toFixed(0);
  return formatted.padStart(4, '\u00A0');
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

  const validDisks = useMemo(() => {
    if (!disks || disks.length === 0) return [];
    return disks.filter((d) => {
      const name = d.name?.trim();
      if (!name) return false;
      return d.totalBytes != null || d.usedBytes != null || d.readSpeed != null || d.writeSpeed != null;
    });
  }, [disks]);

  const viewModels = useMemo(() => {
    return validDisks.map((d, index) => ({
      ...d,
      color: DISK_COLORS[index % DISK_COLORS.length],
    })) satisfies DiskViewModel[];
  }, [validDisks]);

  const containerClass = useMemo(() => {
    const positionClass = layoutState.diskPosition === 'right' ? 'disk-widget--right' : 'disk-widget--left';
    const mobileClass = isMobile ? 'disk-widget--mobile' : '';
    return `disk-widget ${positionClass} ${mobileClass}`.trim();
  }, [layoutState.diskPosition, isMobile]);

  if (!layoutState.visibility.disk) return null;
  if (viewModels.length === 0) return null;

  const mobileOrder = isMobile ? (layoutState.diskPosition === 'right' ? 2 : 1) : undefined;

  return (
    <div
      className={containerClass}
      data-position={layoutState.diskPosition}
      data-mobile={isMobile ? 'true' : 'false'}
      style={mobileOrder ? { order: mobileOrder } : undefined}
    >
      <div className="disk-info-container">
        {viewModels.map((disk, index) => {
          const totalBytes = disk.totalBytes;
          const usedBytes = disk.usedBytes;
          const hasTotalBytes = typeof totalBytes === 'number' && Number.isFinite(totalBytes) && totalBytes > 0;
          const hasUsedBytes = typeof usedBytes === 'number' && Number.isFinite(usedBytes) && usedBytes >= 0;
          const hasBytes = hasTotalBytes && hasUsedBytes;

          const usedPercent = hasBytes ? Math.min(100, Math.max(0, (usedBytes / totalBytes) * 100)) : null;
          const totalStr = hasTotalBytes ? formatSize(totalBytes) : '-';
          const usedStr = hasUsedBytes ? formatSize(usedBytes) : '-';
          const sizeText = hasTotalBytes && !hasUsedBytes ? totalStr : `${usedStr}/${totalStr}`;

          const readSpeedText = disk.readSpeed == null ? '-' : `${formatSpeed(disk.readSpeed)}M`;
          const writeSpeedText = disk.writeSpeed == null ? '-' : `${formatSpeed(disk.writeSpeed)}M`;

          return (
            <div key={`${disk.name}-${index}`} className="disk-item">
              <div className="disk-label" title={disk.name}>
                {disk.name}
              </div>
              <div className="disk-details-row">
                <div className="disk-bar-container" title={usedPercent == null ? 'Usage: -' : `Usage: ${usedPercent.toFixed(0)}%`}>
                  <div className="disk-bar-bg">
                    <div
                      className="disk-bar-fill"
                      style={{ width: `${usedPercent ?? 0}%`, backgroundColor: disk.color }}
                    />
                  </div>
                </div>
                <span className="disk-info-text">{sizeText}</span>
                <div className="disk-speed-group">
                  <span className="speed-r">
                    R:<span className="speed-val">{readSpeedText}</span>
                  </span>
                  <span className="speed-w">
                    W:<span className="speed-val">{writeSpeedText}</span>
                  </span>
                </div>
              </div>
            </div>
          );
        })}
      </div>
    </div>
  );
};
