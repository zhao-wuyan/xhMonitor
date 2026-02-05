import { createContext, useContext, useEffect, useMemo, useState } from 'react';
import type { ReactNode } from 'react';
import { getAccessKey, onAccessKeyChanged } from '../config/accessKey';

const AUTH_REQUIRED_EVENT = 'xh-auth-required';

interface AuthState {
  requiresAccessKey: boolean;
  authEpoch: number;
  clearAuthRequired: () => void;
}

const AuthContext = createContext<AuthState | null>(null);

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

export const useAuth = () => {
  const ctx = useContext(AuthContext);
  if (!ctx) {
    throw new Error('useAuth must be used within AuthProvider');
  }
  return ctx;
};

