import { useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { getAccessKey, onAccessKeyChanged } from '../config/accessKey';
import { AuthContext } from './useAuth';
import type { AuthState } from './useAuth';

const AUTH_REQUIRED_EVENT = 'xh-auth-required';

export const AuthProvider = ({ children }: { children: ReactNode }) => {
  const [requiresAccessKey, setRequiresAccessKey] = useState(false);
  const [authEpoch, setAuthEpoch] = useState(0);

  useEffect(() => {
    if (typeof window === 'undefined') return;
    const handler = () => setRequiresAccessKey(true);
    window.addEventListener(AUTH_REQUIRED_EVENT, handler);
    return () => window.removeEventListener(AUTH_REQUIRED_EVENT, handler);
  }, []);

  useEffect(() => {
    const unsubscribe = onAccessKeyChanged(() => {
      const key = getAccessKey();
      if (key) {
        setRequiresAccessKey(false);
        setAuthEpoch((prev) => prev + 1);
      }
    });
    return unsubscribe;
  }, []);

  const value = useMemo<AuthState>(
    () => ({
      requiresAccessKey,
      authEpoch,
      clearAuthRequired: () => setRequiresAccessKey(false),
    }),
    [requiresAccessKey, authEpoch]
  );

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
};
