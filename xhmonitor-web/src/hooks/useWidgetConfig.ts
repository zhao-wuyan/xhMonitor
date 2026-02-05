import { useState, useEffect } from 'react';
import { API_V1_BASE } from '../config/endpoints';
import { apiFetch } from '../utils/apiFetch';

export interface MetricClickConfig {
  enabled: boolean;
  action: string;
  parameters?: Record<string, string>;
}

export interface WidgetSettings {
  enableMetricClick: boolean;
  metricClickActions: Record<string, MetricClickConfig>;
}

const DEFAULT_SETTINGS: WidgetSettings = {
  enableMetricClick: false,
  metricClickActions: {}
};

export const useWidgetConfig = () => {
  const [settings, setSettings] = useState<WidgetSettings>(DEFAULT_SETTINGS);
  const [loading, setLoading] = useState(true);

  // 加载配置
  useEffect(() => {
    fetchSettings();
  }, []);

  const fetchSettings = async () => {
    try {
      const response = await apiFetch(`${API_V1_BASE}/widgetconfig`);
      if (response.ok) {
        const data = await response.json();
        setSettings(data);
      }
    } catch (error) {
      console.error('Failed to load widget settings:', error);
    } finally {
      setLoading(false);
    }
  };

  const updateSettings = async (newSettings: WidgetSettings) => {
    try {
      const response = await apiFetch(`${API_V1_BASE}/widgetconfig`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(newSettings)
      });

      if (response.ok) {
        setSettings(newSettings);
        return true;
      }
      return false;
    } catch (error) {
      console.error('Failed to update widget settings:', error);
      return false;
    }
  };

  const updateMetricConfig = async (metricId: string, config: MetricClickConfig) => {
    try {
      const response = await apiFetch(`${API_V1_BASE}/widgetconfig/${metricId}`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(config)
      });

      if (response.ok) {
        setSettings(prev => ({
          ...prev,
          metricClickActions: {
            ...prev.metricClickActions,
            [metricId]: config
          }
        }));
        return true;
      }
      return false;
    } catch (error) {
      console.error(`Failed to update metric config for ${metricId}:`, error);
      return false;
    }
  };

  const isMetricClickEnabled = (metricId: string): boolean => {
    if (!settings.enableMetricClick) return false;
    return settings.metricClickActions[metricId]?.enabled ?? false;
  };

  const getMetricAction = (metricId: string): string => {
    return settings.metricClickActions[metricId]?.action ?? 'none';
  };

  return {
    settings,
    loading,
    updateSettings,
    updateMetricConfig,
    isMetricClickEnabled,
    getMetricAction
  };
};
