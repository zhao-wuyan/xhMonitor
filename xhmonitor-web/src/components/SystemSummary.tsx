import * as LucideIcons from 'lucide-react';
import type { MetricMetadata } from '../types';
import { formatPercent, formatBytes } from '../utils';

interface SystemSummaryProps {
  summary: Record<string, number>;
  metricMetadata: MetricMetadata[];
  colorMap: Record<string, string>;
  iconMap: Record<string, string>;
}

export const SystemSummary = ({ summary, metricMetadata, colorMap, iconMap }: SystemSummaryProps) => {
  const getIcon = (iconName: string) => {
    const Icon = (LucideIcons as any)[iconName] || LucideIcons.Activity;
    return Icon;
  };

  const formatValue = (value: number, unit: string): string => {
    if (unit === '%') {
      return formatPercent(value);
    } else if (unit === 'MB' || unit === 'GB') {
      return formatBytes(value * 1024 * 1024);
    } else {
      return `${value.toFixed(1)} ${unit}`;
    }
  };

  const getGlowClass = (metricId: string): string => {
    const color = colorMap[metricId];
    if (!color) return '';

    const rgb = color.match(/\w\w/g)?.map((x) => parseInt(x, 16));
    if (!rgb) return '';

    return `shadow-[0_0_20px_rgba(${rgb[0]},${rgb[1]},${rgb[2]},0.3)]`;
  };

  return (
    <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4 mb-6">
      {metricMetadata.map((metric) => {
        const Icon = getIcon(iconMap[metric.metricId]);
        const value = summary[metric.metricId] || 0;
        const color = colorMap[metric.metricId] || '#6b7280';

        return (
          <div
            key={metric.metricId}
            className={`glass glass-hover rounded-xl p-6 ${getGlowClass(metric.metricId)}`}
          >
            <div className="flex items-center justify-between mb-4">
              <Icon className="w-8 h-8" style={{ color }} />
              <span className="text-sm text-gray-400">{metric.displayName}</span>
            </div>
            <div className="text-3xl font-bold font-mono">
              {formatValue(value, metric.unit)}
            </div>
            <div className="mt-2 text-sm text-gray-400">
              {summary.processCount} processes
            </div>
          </div>
        );
      })}
    </div>
  );
};
