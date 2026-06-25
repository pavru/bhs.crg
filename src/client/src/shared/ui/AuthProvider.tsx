import { useState, useCallback, type ReactNode } from 'react';
import { jwtDecode } from 'jwt-decode';
import { apiClient } from '@/shared/api/client';
import { AuthContext, type AuthUser } from '@/shared/hooks/useAuth';

function decodeUser(token: string): AuthUser {
  const payload = jwtDecode<{ sub: string; email: string; displayName: string }>(token);
  return { sub: payload.sub, email: payload.email, displayName: payload.displayName };
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(() => {
    const token = localStorage.getItem('access_token');
    return token ? decodeUser(token) : null;
  });

  const login = useCallback(async (email: string, password: string) => {
    const { data } = await apiClient.post<{ accessToken: string }>('/auth/login', { email, password });
    localStorage.setItem('access_token', data.accessToken);
    setUser(decodeUser(data.accessToken));
  }, []);

  const logout = useCallback(() => {
    localStorage.removeItem('access_token');
    setUser(null);
  }, []);

  return (
    <AuthContext.Provider value={{ user, login, logout }}>
      {children}
    </AuthContext.Provider>
  );
}
