import type { ProcessInfo, SystemUsage } from './types';

export const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${(bytes / Math.pow(k, i)).toFixed(2)} ${sizes[i]}`;
};

export const formatPercent = (value: number): string => {
  return `${value.toFixed(1)}%`;
};

export const formatTimestamp = (timestamp: string): string => {
  const date = new Date(timestamp);
  return date.toLocaleTimeString('zh-CN', {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  });
};

export const calculateSystemSummary = (
  processes: ProcessInfo[],
  systemUsage?: SystemUsage | null
): Record<string, number> & { processCount: number } => {
  const summary: Record<string, number> = {};

  processes.forEach((p) => {
    Object.entries(p.metrics).forEach(([metricId, metricValue]) => {
      if (!summary[metricId]) {
        summary[metricId] = 0;
      }
      summary[metricId] += metricValue;
    });
  });

  if (systemUsage) {
    if (Number.isFinite(systemUsage.totalCpu)) summary.cpu = systemUsage.totalCpu;
    if (Number.isFinite(systemUsage.totalGpu)) summary.gpu = systemUsage.totalGpu;
    if (Number.isFinite(systemUsage.totalMemory)) summary.memory = systemUsage.totalMemory;
    if (Number.isFinite(systemUsage.totalVram)) summary.vram = systemUsage.totalVram;
  }

  return {
    ...summary,
    processCount: processes.length,
  };
};
