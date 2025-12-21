import { useState, useMemo } from 'react';
import { ArrowUpDown } from 'lucide-react';
import type { ProcessInfo, MetricMetadata } from '../types';
import { formatPercent, formatBytes } from '../utils';

interface ProcessListProps {
  processes: ProcessInfo[];
  metricMetadata: MetricMetadata[];
  colorMap: Record<string, string>;
}

type SortField = 'processName' | string;
type SortOrder = 'asc' | 'desc';

export const ProcessList = ({ processes, metricMetadata, colorMap }: ProcessListProps) => {
  const [sortField, setSortField] = useState<SortField>('processName');
  const [sortOrder, setSortOrder] = useState<SortOrder>('desc');
  const [searchTerm, setSearchTerm] = useState('');

  const handleSort = (field: SortField) => {
    if (sortField === field) {
      setSortOrder(sortOrder === 'asc' ? 'desc' : 'asc');
    } else {
      setSortField(field);
      setSortOrder('desc');
    }
  };

  const sortedAndFilteredProcesses = useMemo(() => {
    let filtered = processes.filter(
      (p) =>
        p.processName.toLowerCase().includes(searchTerm.toLowerCase()) ||
        p.commandLine.toLowerCase().includes(searchTerm.toLowerCase())
    );

    filtered.sort((a, b) => {
      if (sortField === 'processName') {
        return sortOrder === 'asc'
          ? a.processName.localeCompare(b.processName)
          : b.processName.localeCompare(a.processName);
      }

      const aValue = a.metrics[sortField]?.value || 0;
      const bValue = b.metrics[sortField]?.value || 0;

      return sortOrder === 'asc' ? aValue - bValue : bValue - aValue;
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

  const renderMetricBar = (value: number, metricId: string, max: number = 100) => {
    const percentage = Math.min((value / max) * 100, 100);
    const color = colorMap[metricId] || '#6b7280';

    return (
      <div className="w-full bg-gray-700 rounded-full h-2 overflow-hidden">
        <div
          className="metric-bar"
          style={{ width: `${percentage}%`, backgroundColor: color }}
        />
      </div>
    );
  };

  return (
    <div className="glass rounded-xl p-6">
      <div className="flex items-center justify-between mb-4">
        <h2 className="text-2xl font-bold">Process Monitor</h2>
        <input
          type="text"
          placeholder="Search processes..."
          value={searchTerm}
          onChange={(e) => setSearchTerm(e.target.value)}
          className="px-4 py-2 bg-gray-800 border border-gray-700 rounded-lg focus:outline-none focus:border-cpu"
        />
      </div>

      <div className="overflow-x-auto">
        <table className="w-full">
          <thead>
            <tr className="border-b border-gray-700">
              <th className="text-left py-3 px-4">
                <button
                  onClick={() => handleSort('processName')}
                  className="flex items-center gap-2 hover:text-cpu transition-colors"
                >
                  Process
                  <ArrowUpDown className="w-4 h-4" />
                </button>
              </th>
              <th className="text-left py-3 px-4">PID</th>
              {metricMetadata.map((metric) => (
                <th key={metric.metricId} className="text-left py-3 px-4">
                  <button
                    onClick={() => handleSort(metric.metricId)}
                    className="flex items-center gap-2 hover:opacity-80 transition-colors"
                    style={{ color: colorMap[metric.metricId] }}
                  >
                    {metric.displayName}
                    <ArrowUpDown className="w-4 h-4" />
                  </button>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {sortedAndFilteredProcesses.map((process) => (
              <tr
                key={process.processId}
                className="border-b border-gray-800 hover:bg-gray-800/50 transition-colors"
              >
                <td className="py-3 px-4">
                  <div className="font-medium">{process.processName}</div>
                  <div className="text-xs text-gray-400 truncate max-w-xs">
                    {process.commandLine}
                  </div>
                </td>
                <td className="py-3 px-4 font-mono text-sm">
                  {process.processId}
                </td>
                {metricMetadata.map((metric) => {
                  const metricValue = process.metrics[metric.metricId];
                  return (
                    <td key={metric.metricId} className="py-3 px-4">
                      {metricValue ? (
                        <div className="space-y-1">
                          <div className="text-sm font-mono">
                            {formatValue(metricValue.value, metric.unit)}
                          </div>
                          {renderMetricBar(
                            metricValue.value,
                            metric.metricId,
                            metric.unit === '%' ? 100 : 1024
                          )}
                        </div>
                      ) : (
                        <span className="text-gray-500">-</span>
                      )}
                    </td>
                  );
                })}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      {sortedAndFilteredProcesses.length === 0 && (
        <div className="text-center py-8 text-gray-400">
          No processes found matching "{searchTerm}"
        </div>
      )}
    </div>
  );
};
