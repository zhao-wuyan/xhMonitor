import type { ProcessInfo, SystemUsage } from './types';

const MB_IN_BYTES = 1024 * 1024;

export const formatBytes = (bytes: number): string => {
  if (bytes === 0) return '0 B';
  const k = 1024;
  const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
  const i = Math.floor(Math.log(bytes) / Math.log(k));
  return `${(bytes / Math.pow(k, i)).toFixed(2)} ${sizes[i]}`;
};

export const formatBytesParts = (bytes: number): { value: string; unit: string } => {
  const formatted = formatBytes(bytes);
  const [value, unit] = formatted.split(' ');
  return { value: value ?? '0', unit: unit ?? 'B' };
};

export const formatMegabytesParts = (megabytes: number): { value: string; unit: string } => {
  const safe = Number.isFinite(megabytes) && megabytes > 0 ? megabytes : 0;
  return formatBytesParts(safe * MB_IN_BYTES);
};

export const formatMegabytesLabel = (megabytes: number): string => {
  const safe = Number.isFinite(megabytes) && megabytes > 0 ? megabytes : 0;
  return formatBytes(safe * MB_IN_BYTES);
};

export const formatNetworkRateParts = (
  megabytesPerSecond: number
): { value: string; unit: string; compact: string } => {
  const safe = Number.isFinite(megabytesPerSecond) && megabytesPerSecond > 0 ? megabytesPerSecond : 0;

  if (safe >= 1024) {
    const gbPerSecond = safe / 1024;
    const v = gbPerSecond.toFixed(1);
    return { value: v, unit: 'GB/s', compact: `${v}G` };
  }

  if (safe >= 1) {
    const v = safe.toFixed(1);
    return { value: v, unit: 'MB/s', compact: `${v}M` };
  }

  const kbPerSecond = Math.round(safe * 1024);
  return { value: String(kbPerSecond), unit: 'KB/s', compact: `${kbPerSecond}K` };
};

export const formatNetworkRateLabel = (megabytesPerSecond: number): string => {
  const parts = formatNetworkRateParts(megabytesPerSecond);
  return `${parts.value} ${parts.unit}`;
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
