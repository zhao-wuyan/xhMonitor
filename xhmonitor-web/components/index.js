/**
 * XhMonitor Components Library
 * 版本: 1.0.0
 *
 * 从 ui-preview-v2.html 提取的设计系统
 * 提供可复用的 UI 组件和图表引擎
 */

// 导出图表组件
import MiniChart from './charts/MiniChart.js';
import DynamicScaler from './charts/DynamicScaler.js';

export { MiniChart, DynamicScaler };

// 组件库信息
export const version = '1.0.0';
export const name = '@xhmonitor/components';

// 设计 Tokens
export const tokens = {
  colors: {
    bg: '#0f172a',
    textPrimary: '#f8fafc',
    textSecondary: '#94a3b8',
    glassBg: 'rgba(30, 41, 59, 0.6)',
    glassBorder: 'rgba(255, 255, 255, 0.08)',
    glassHighlight: 'rgba(255, 255, 255, 0.05)',
    cpu: '#3b82f6',
    ram: '#8b5cf6',
    gpu: '#10b981',
    vram: '#f59e0b',
    net: '#0ea5e9',
    pwr: '#f43f5e'
  },
  fonts: {
    sans: "'Segoe UI', system-ui, -apple-system, sans-serif",
    mono: "'Consolas', 'Monaco', 'Courier New', monospace"
  },
  spacing: {
    xs: '2px',
    sm: '4px',
    md: '6px',
    base: '10px',
    lg: '12px',
    xl: '16px',
    '2xl': '20px'
  },
  radius: {
    sm: '4px',
    md: '8px',
    lg: '16px',
    full: '9999px'
  },
  duration: {
    instant: 0,
    fast: 200,
    normal: 300,
    slow: 500,
    slower: 1000,
    pulse: 2000
  },
  effects: {
    glowOpacity: 0.1,
    glowBlur: '40px'
  }
};

// 工具函数
export const utils = {
  /**
   * 格式化百分比
   */
  formatPercent: (value) => value.toFixed(0) + '%',

  /**
   * 格式化 GB
   */
  formatGB: (value, total) => (value / 100 * total).toFixed(1) + 'G',

  /**
   * 格式化网络流量
   */
  formatNetwork: (value) => {
    if (value > 1024 * 1024) return (value / (1024 * 1024)).toFixed(1) + 'G';
    if (value > 1024) return (value / 1024).toFixed(1) + 'M';
    return value.toFixed(0) + 'K';
  },

  /**
   * 格式化功耗
   */
  formatPower: (value) => value.toFixed(0) + 'W',

  /**
   * 十六进制颜色转 RGBA
   */
  hexToRgba: (hex, alpha) => {
    let r = 0, g = 0, b = 0;
    if (hex.length === 4) {
      r = parseInt(hex[1] + hex[1], 16);
      g = parseInt(hex[2] + hex[2], 16);
      b = parseInt(hex[3] + hex[3], 16);
    } else if (hex.length === 7) {
      r = parseInt(hex[1] + hex[2], 16);
      g = parseInt(hex[3] + hex[4], 16);
      b = parseInt(hex[5] + hex[6], 16);
    }
    return `rgba(${r},${g},${b},${alpha})`;
  }
};

// 默认导出
export default {
  version,
  name,
  tokens,
  utils,
  MiniChart,
  DynamicScaler
};
