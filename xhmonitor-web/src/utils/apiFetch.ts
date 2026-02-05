import { getAccessKey } from '../config/accessKey';

const AUTH_REQUIRED_EVENT = 'xh-auth-required';

export const apiFetch = (input: RequestInfo | URL, init: RequestInit = {}) => {
  const headers = new Headers(init.headers);
  const accessKey = getAccessKey();
  if (accessKey) {
    headers.set('X-Access-Key', accessKey);
  }

  return fetch(input, { ...init, headers }).then((response) => {
    if (response.status === 401) {
      try {
        window.dispatchEvent(new Event(AUTH_REQUIRED_EVENT));
      } catch {
        // ignore
      }
    }

    return response;
  });
};
