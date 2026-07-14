import { createContext, useContext } from 'react';

export type UserRole = 'Admin' | 'User';

export interface AuthUser {
  sub: string;
  email: string;
  displayName: string;
  role: UserRole;
}

export interface AuthContextValue {
  user: AuthUser | null;
  login: (email: string, password: string, remember?: boolean) => Promise<void>;
  logout: () => void;
}

export const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}
