// Web endpoint configuration.
//
// Design goals:
// - Production build (Desktop embedded web): always use same-origin relative URLs,
//   so LAN access goes through Desktop reverse proxy (35180) and never hits localhost:35179.
// - Development (Vite dev server): allow env overrides, defaulting to local Service port.

const DEV_DEFAULT_SERVICE_ORIGIN = 'http://localhost:35179';

const apiBaseUrl = import.meta.env.DEV
  ? ((import.meta.env.VITE_API_BASE_URL as string | undefined) ?? DEV_DEFAULT_SERVICE_ORIGIN)
  : '';

export const API_BASE_URL = apiBaseUrl;
export const API_V1_BASE = `${apiBaseUrl.replace(/\/$/, '')}/api/v1`;

const devMetricsHubUrl =
  (import.meta.env.VITE_METRICS_HUB_URL as string | undefined) ??
  `${apiBaseUrl.replace(/\/$/, '')}/hubs/metrics`;

export const METRICS_HUB_URL = import.meta.env.DEV ? devMetricsHubUrl : '/hubs/metrics';

