export interface MetricValue {
  value: number;
  unit: string;
  displayName: string;
  timestamp: string;
}

export interface ProcessMetrics {
  [key: string]: MetricValue;
}

export interface ProcessInfo {
  processId: number;
  processName: string;
  commandLine: string;
  metrics: ProcessMetrics;
}

export interface MetricsData {
  timestamp: string;
  processCount: number;
  processes: ProcessInfo[];
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
