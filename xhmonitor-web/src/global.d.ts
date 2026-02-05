interface Window {
  chrome?: {
    webview?: {
      postMessage(message: unknown): void;
    };
  };
}

declare const __APP_VERSION__: string;

declare module '../../components/charts/MiniChart.js' {
  export type ChartFormatFn = (value: number) => string;

  export default class MiniChart {
    constructor(
      canvasId: string,
      containerId: string,
      color: string,
      formatFn?: ChartFormatFn
    );
    draw(data: number[], maxValue?: number): void;
    resize(): void;
    destroy(): void;
    color?: string;
    formatFn?: ChartFormatFn;
  }
}

declare module '*.js' {
  const mod: unknown;
  export default mod;
}
