import { createContext, useContext } from 'react';

export interface AuthState {
  requiresAccessKey: boolean;
  authEpoch: number;
  clearAuthRequired: () => void;
}

export const AuthContext = createContext<AuthState | null>(null);

export const useAuth = () => {
  const ctx = useContext(AuthContext);
  if (!ctx) {
    throw new Error('useAuth must be used within AuthProvider');
  }
  return ctx;
};

