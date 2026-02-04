export interface ProcessMetrics {
  [key: string]: number;
}

export interface ProcessInfo {
  processId: number;
  processName: string;
  commandLine?: string;
  displayName?: string;
  metrics: ProcessMetrics;
}

export interface MetricsData {
  timestamp: string;
  processCount: number;
  processes: ProcessInfo[];
}

export interface DiskUsage {
  name: string;
  totalBytes: number | null;
  usedBytes: number | null;
  readSpeed: number | null;
  writeSpeed: number | null;
}

export interface SystemUsage {
  timestamp: string;
  totalCpu: number;
  totalGpu: number;
  totalMemory: number;
  totalVram: number;
  disks?: DiskUsage[];
  maxMemory: number;
  maxVram: number;
  uploadSpeed: number;
  downloadSpeed: number;
  powerAvailable?: boolean;
  totalPower?: number;
  maxPower?: number;
  powerSchemeIndex?: number | null;
}

export interface ProcessMetaInfo {
  processId: number;
  processName: string;
  commandLine: string;
  displayName: string;
}

export interface ProcessMetaData {
  timestamp: string;
  processCount: number;
  processes: ProcessMetaInfo[];
}

export interface AlertConfig {
  id: number;
  metricId: string;
  threshold: number;
  isEnabled: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface HealthStatus {
  status: string;
  timestamp: string;
  database: string;
}

export interface ChartDataPoint {
  timestamp: string;
  value: number;
}

export interface MetricMetadata {
  metricId: string;
  displayName: string;
  unit: string;
  type: string;
  category?: string;
  color?: string;
  icon?: string;
}

export interface MetricConfig {
  metadata: MetricMetadata[];
  colorMap: Record<string, string>;
  iconMap: Record<string, string>;
}
