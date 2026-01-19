interface Window {
  chrome?: {
    webview?: {
      postMessage(message: unknown): void;
    };
  };
}
