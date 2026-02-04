import { forwardRef, useMemo, useState } from 'react';
import type { CSSProperties } from 'react';
import type { ProcessInfo, MetricMetadata } from '../types';
import { formatPercent, formatBytes } from '../utils';
import { t } from '../i18n';

interface ProcessListProps {
  processes: ProcessInfo[];
  metricMetadata: MetricMetadata[];
  colorMap: Record<string, string>;
  maxMemoryMb?: number;
  maxVramMb?: number;
}

type SortField = 'processName' | string;
type SortOrder = 'asc' | 'desc';

const DEFAULT_RESOURCE_SORT_FIELD = 'gpuVramMemoryTotal';

interface ProcessListScrollProps {
  scrollMode?: 'page' | 'process';
  processTableMaxHeight?: number;
}

const getProcessDisplayName = (process: ProcessInfo): string => {
  const displayName = (process.displayName ?? '').trim();
  if (displayName) return displayName;
  return process.processName;
};

export const ProcessList = forwardRef<HTMLDivElement, ProcessListProps & ProcessListScrollProps>(
  ({ processes, metricMetadata, colorMap, maxMemoryMb, maxVramMb, scrollMode = 'page', processTableMaxHeight = 0 }, ref) => {
  const [sortField, setSortField] = useState<SortField>(DEFAULT_RESOURCE_SORT_FIELD);
  const [sortOrder, setSortOrder] = useState<SortOrder>('desc');
  const [searchTerm, setSearchTerm] = useState('');

  const orderedMetricMetadata = useMemo(() => {
    const rank = (metricId: string) => {
      const id = metricId.toLowerCase();
      if (id === 'cpu') return 0;
      if (id === 'memory' || id === 'ram') return 1;
      if (id === 'gpu') return 2;
      if (id === 'vram') return 3;
      return 100;
    };

    return [...metricMetadata].sort((a, b) => {
      const diff = rank(a.metricId) - rank(b.metricId);
      if (diff !== 0) return diff;
      return a.metricId.localeCompare(b.metricId);
    });
  }, [metricMetadata]);

  const handleSort = (field: SortField) => {
    if (sortField === field) {
      setSortOrder(sortOrder === 'asc' ? 'desc' : 'asc');
    } else {
      setSortField(field);
      setSortOrder('desc');
    }
  };

  const sortedAndFilteredProcesses = useMemo(() => {
    const filtered = processes.filter(
      (p) =>
        p.processName.toLowerCase().includes(searchTerm.toLowerCase()) ||
        getProcessDisplayName(p).toLowerCase().includes(searchTerm.toLowerCase()) ||
        (p.commandLine ?? '').toLowerCase().includes(searchTerm.toLowerCase())
    );

    filtered.sort((a, b) => {
      if (sortField === 'processName') {
        const aName = getProcessDisplayName(a);
        const bName = getProcessDisplayName(b);
        return sortOrder === 'asc'
          ? aName.localeCompare(bName)
          : bName.localeCompare(aName);
      }

      if (sortField === 'processId') {
        return sortOrder === 'asc' ? a.processId - b.processId : b.processId - a.processId;
      }

      const getSortValue = (p: ProcessInfo) => {
        if (sortField === DEFAULT_RESOURCE_SORT_FIELD) {
          const gpu = p.metrics.gpu ?? 0;
          const vram = p.metrics.vram ?? 0;
          const memory = p.metrics.memory ?? 0;
          return gpu + vram + memory;
        }

        return p.metrics[sortField] ?? 0;
      };

      const aValue = getSortValue(a);
      const bValue = getSortValue(b);

      const diff = sortOrder === 'asc' ? aValue - bValue : bValue - aValue;
      if (diff !== 0) return diff;
      return getProcessDisplayName(b).localeCompare(getProcessDisplayName(a));
    });

    return filtered;
  }, [processes, sortField, sortOrder, searchTerm]);

  const formatValue = (value: number, unit: string): string => {
    if (unit === '%') {
      return formatPercent(value);
    } else if (unit === 'MB' || unit === 'GB') {
      return formatBytes(value * 1024 * 1024);
    } else {
      return `${value.toFixed(1)} ${unit}`;
    }
  };

  const getProgressMaxValue = (metric: MetricMetadata): number | null => {
    if (metric.unit === '%') {
      return 100;
    }

    const metricId = metric.metricId.toLowerCase();
    if (metricId === 'memory' || metricId === 'ram') {
      return typeof maxMemoryMb === 'number' && maxMemoryMb > 0 ? maxMemoryMb : null;
    }

    if (metricId === 'vram') {
      return typeof maxVramMb === 'number' && maxVramMb > 0 ? maxVramMb : null;
    }

    return null;
  };

  const tableHeaderRow = (
    <tr>
      <th
        onClick={() => handleSort('processName')}
        className={sortField === 'processName' ? 'active-sort' : ''}
      >
        {t('Process')} {sortField === 'processName' && (sortOrder === 'asc' ? ' ↑' : ' ↓')}
      </th>
      <th
        onClick={() => handleSort('processId')}
        className={sortField === 'processId' ? 'active-sort' : ''}
      >
        {t('PID')} {sortField === 'processId' && (sortOrder === 'asc' ? ' ↑' : ' ↓')}
      </th>
      {orderedMetricMetadata.map((metric) => (
        <th
          key={metric.metricId}
          onClick={() => handleSort(metric.metricId)}
          className={sortField === metric.metricId ? 'active-sort' : ''}
        >
          {t(metric.displayName)} {sortField === metric.metricId && (sortOrder === 'asc' ? ' ↑' : ' ↓')}
        </th>
      ))}
    </tr>
  );

  const processOnlyScrollEnabled = scrollMode === 'process' && processTableMaxHeight > 0;
  const processPanelClassName = `process-panel xh-glass-panel${processOnlyScrollEnabled ? ' process-panel--scroll' : ''}`;
  const processPanelStyle: (CSSProperties & { ['--xh-process-scroll-max-height']?: string }) | undefined =
    processOnlyScrollEnabled
      ? { ['--xh-process-scroll-max-height']: `${processTableMaxHeight}px` }
      : undefined;

  return (
    <div ref={ref} className={processPanelClassName} style={processPanelStyle}>
      <div className="panel-title">
        {t('Process Monitor')}
        <div className="search-box">
          <span className="search-icon">🔍</span>
          <input
            type="text"
            className="search-input"
            placeholder={t('Search processes...')}
            value={searchTerm}
            onChange={(e) => setSearchTerm(e.target.value)}
          />
        </div>
      </div>

      <div className="table-scroll">
        <table>
          <thead>
            {tableHeaderRow}
          </thead>
        </table>
        <div className="table-body-scroll">
          <table>
          <tbody>
            {sortedAndFilteredProcesses.map((process) => (
              <tr key={process.processId}>
                <td>
                  <div className="proc-name-cell">
                    <div className="proc-icon">
                      {process.processName.charAt(0).toUpperCase()}
                    </div>
                    <div className="proc-info">
                      <div className="proc-name" title={getProcessDisplayName(process)}>
                        {getProcessDisplayName(process)}
                      </div>
                      <div className="proc-cmd" title={process.commandLine ?? ''}>
                        {process.commandLine ?? ''}
                      </div>
                    </div>
                  </div>
                </td>
                <td className="pid-cell">{process.processId}</td>
                {orderedMetricMetadata.map((metric) => {
                  const metricValue = process.metrics[metric.metricId];
                  const color = colorMap[metric.metricId] || '#6b7280';
                  const progressMax = getProgressMaxValue(metric);
                  const widthPercent =
                    metricValue !== undefined && progressMax != null && progressMax > 0
                      ? Math.min((Math.max(metricValue, 0) / progressMax) * 100, 100)
                      : 0;

                  return (
                    <td key={metric.metricId}>
                      {metricValue !== undefined ? (
                        <div className="metric-cell">
                          <span className="metric-val" style={{ color }}>
                            {formatValue(metricValue, metric.unit)}
                          </span>
                          <div className="progress-bg">
                            <div
                              className="progress-fill"
                              style={{
                                width: `${widthPercent}%`,
                                backgroundColor: color
                              }}
                            />
                          </div>
                        </div>
                      ) : (
                        <span style={{ color: 'var(--xh-color-text-secondary)', opacity: 0.5 }}>-</span>
                      )}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
          </table>
        </div>
      </div>

      {sortedAndFilteredProcesses.length === 0 && (
        <div style={{ textAlign: 'center', padding: '2rem 0', color: 'var(--xh-color-text-secondary)' }}>
          {t('No processes found matching')} "{searchTerm}"
        </div>
      )}
    </div>
  );
  }
);

ProcessList.displayName = 'ProcessList';
