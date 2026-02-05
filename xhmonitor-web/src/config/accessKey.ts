const STORAGE_KEY = 'xh.accessKey';
const CHANGE_EVENT = 'xh-access-key-changed';

export const getAccessKey = (): string => {
  if (typeof window === 'undefined') return '';
  try {
    return window.localStorage.getItem(STORAGE_KEY) ?? '';
  } catch {
    return '';
  }
};

export const setAccessKey = (value: string) => {
  if (typeof window === 'undefined') return;
  const trimmed = value.trim();
  try {
    if (trimmed) {
      window.localStorage.setItem(STORAGE_KEY, trimmed);
    } else {
      window.localStorage.removeItem(STORAGE_KEY);
    }
  } catch {
    // ignore
  }

  try {
    window.dispatchEvent(new Event(CHANGE_EVENT));
  } catch {
    // ignore
  }
};

export const onAccessKeyChanged = (handler: () => void) => {
  if (typeof window === 'undefined') return () => {};
  window.addEventListener(CHANGE_EVENT, handler);
  return () => window.removeEventListener(CHANGE_EVENT, handler);
};

