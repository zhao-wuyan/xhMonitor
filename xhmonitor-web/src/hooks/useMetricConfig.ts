import { useEffect, useState } from 'react';
import type { MetricMetadata, MetricConfig } from '../types';

const API_BASE_URL = import.meta.env.VITE_API_BASE_URL || 'http://localhost:35179';
const API_BASE = `${String(API_BASE_URL).replace(/\/$/, '')}/api/v1`;

const DEFAULT_COLORS: Record<string, string> = {
  cpu: '#3b82f6',
  memory: '#10b981',
  gpu: '#8b5cf6',
  vram: '#f59e0b',
};

const DEFAULT_ICONS: Record<string, string> = {
  cpu: 'Cpu',
  memory: 'MemoryStick',
  gpu: 'Gpu',
  vram: 'HardDrive',
};

export const useMetricConfig = () => {
  const [config, setConfig] = useState<MetricConfig | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const fetchMetricConfig = async () => {
      try {
        const response = await fetch(`${API_BASE}/config/metrics`);
        if (!response.ok) {
          throw new Error(`HTTP ${response.status}`);
        }

        const metadata: MetricMetadata[] = await response.json();

        const colorMap: Record<string, string> = {};
        const iconMap: Record<string, string> = {};

        metadata.forEach((m) => {
          colorMap[m.metricId] = m.color || DEFAULT_COLORS[m.metricId] || '#6b7280';
          iconMap[m.metricId] = m.icon || DEFAULT_ICONS[m.metricId] || 'Activity';
        });

        setConfig({ metadata, colorMap, iconMap });
        setError(null);
      } catch (err) {
        setError(err instanceof Error ? err.message : 'Failed to fetch metric config');
        console.error('Failed to fetch metric config:', err);
      } finally {
        setLoading(false);
      }
    };

    fetchMetricConfig();
  }, []);

  return { config, loading, error };
};
