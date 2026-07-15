import { useState, useCallback, type ReactNode } from 'react';
import { jwtDecode } from 'jwt-decode';
import { apiClient } from '@/shared/api/client';
import { getToken, getRefreshToken, setTokens, clearToken, replaceTokens } from '@/shared/api/token';
import { AuthContext, type AuthUser, type UserRole } from '@/shared/hooks/useAuth';

function decodeUser(token: string): AuthUser {
  const payload = jwtDecode<{ sub: string; email: string; displayName: string; role?: string | string[] }>(token);
  const roles = Array.isArray(payload.role) ? payload.role : payload.role ? [payload.role] : [];
  const role: UserRole = roles.includes('Admin') ? 'Admin' : 'User';
  return { sub: payload.sub, email: payload.email, displayName: payload.displayName, role };
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(() => {
    const token = getToken();
    return token ? decodeUser(token) : null;
  });

  const login = useCallback(async (email: string, password: string, remember = true) => {
    const { data } = await apiClient.post<{ accessToken: string; refreshToken: string }>(
      '/auth/login', { email, password });
    setTokens(data.accessToken, data.refreshToken, remember);
    setUser(decodeUser(data.accessToken));
  }, []);

  const updateSession = useCallback((accessToken: string, refreshToken: string) => {
    replaceTokens(accessToken, refreshToken);
    setUser(decodeUser(accessToken));
  }, []);

  const logout = useCallback(() => {
    // Отзываем refresh-токен на сервере (best-effort), затем чистим локально.
    const refresh = getRefreshToken();
    if (refresh) void apiClient.post('/auth/logout', { refreshToken: refresh }).catch(() => {});
    clearToken();
    setUser(null);
  }, []);

  return (
    <AuthContext.Provider value={{ user, login, updateSession, logout }}>
      {children}
    </AuthContext.Provider>
  );
}
