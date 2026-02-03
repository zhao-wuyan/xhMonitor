import { useEffect, useMemo, useState } from 'react';
import { useLayout } from '../contexts/LayoutContext';

interface DiskInfo {
  name: string;
  total: number;
  used: number;
  color: string;
  readSpeed: number;
  writeSpeed: number;
}

const DEFAULT_DISKS: DiskInfo[] = [
  {
    name: 'Samsung SSD 980 PRO 1TB',
    total: 1024,
    used: 45,
    color: '#3b82f6',
    readSpeed: 0,
    writeSpeed: 0,
  },
  {
    name: 'WDC WD40EZAZ-00SF3B0',
    total: 4096,
    used: 85,
    color: '#10b981',
    readSpeed: 0,
    writeSpeed: 0,
  },
];

const formatSize = (gb: number) => {
  if (gb >= 1000) return `${(gb / 1024).toFixed(1)}T`;
  return `${gb.toFixed(0)}G`;
};

const formatSpeed = (value: number) => {
  const formatted = value < 10 ? value.toFixed(1) : value.toFixed(0);
  return formatted.padStart(4, '\u00A0');
};

export const DiskWidget = () => {
  const { layoutState } = useLayout();
  const [disks, setDisks] = useState<DiskInfo[]>(DEFAULT_DISKS);
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

  useEffect(() => {
    const interval = window.setInterval(() => {
      setDisks((prev) =>
        prev.map((disk) => ({
          ...disk,
          readSpeed: Math.random() > 0.7 ? Math.random() * 200 : 0,
          writeSpeed: Math.random() > 0.8 ? Math.random() * 100 : 0,
        }))
      );
    }, 1000);

    return () => window.clearInterval(interval);
  }, []);

  const containerClass = useMemo(() => {
    const positionClass = layoutState.diskPosition === 'right' ? 'disk-widget--right' : 'disk-widget--left';
    const mobileClass = isMobile ? 'disk-widget--mobile' : '';
    return `disk-widget ${positionClass} ${mobileClass}`.trim();
  }, [layoutState.diskPosition, isMobile]);

  if (!layoutState.visibility.disk) return null;

  const mobileOrder = isMobile ? (layoutState.diskPosition === 'right' ? 2 : 1) : undefined;

  return (
    <div
      className={containerClass}
      data-position={layoutState.diskPosition}
      data-mobile={isMobile ? 'true' : 'false'}
      style={mobileOrder ? { order: mobileOrder } : undefined}
    >
      <div className="disk-info-container">
        {disks.map((disk, index) => {
          const usedGB = disk.total * (disk.used / 100);
          const totalStr = formatSize(disk.total);
          const usedStr = formatSize(usedGB);

          return (
            <div key={`${disk.name}-${index}`} className="disk-item">
              <div className="disk-label" title={disk.name}>
                {disk.name}
              </div>
              <div className="disk-details-row">
                <div className="disk-bar-container" title={`Usage: ${disk.used}%`}>
                  <div className="disk-bar-bg">
                    <div
                      className="disk-bar-fill"
                      style={{ width: `${disk.used}%`, backgroundColor: disk.color }}
                    />
                  </div>
                </div>
                <span className="disk-info-text">{usedStr}/{totalStr}</span>
                <div className="disk-speed-group">
                  <span className="speed-r">
                    R:<span className="speed-val">{formatSpeed(disk.readSpeed)}M</span>
                  </span>
                  <span className="speed-w">
                    W:<span className="speed-val">{formatSpeed(disk.writeSpeed)}M</span>
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
