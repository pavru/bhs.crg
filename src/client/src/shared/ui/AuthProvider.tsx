import { useState, useCallback, useEffect, type ReactNode } from 'react';
import { jwtDecode } from 'jwt-decode';
import { apiClient } from '@/shared/api/client';
import {
  getToken, getRefreshToken, setTokens, clearToken, replaceTokens, onTokenChanged,
} from '@/shared/api/token';
import { AuthContext, type AuthUser } from '@/shared/hooks/useAuth';

function decodeUser(token: string): AuthUser {
  // Роли из токена не читаются вовсе (issue #984). Раньше отсюда брали ПЕРВУЮ — и у человека с
  // двумя ролями это была ложь ровно в том месте, где её не проверишь. Решений по имени роли не
  // принимают: что доступно, отвечает /api/account/access (AUTH-14), а подписи — /api/account.
  const payload = jwtDecode<{ sub: string; email: string; displayName: string }>(token);
  return { sub: payload.sub, email: payload.email, displayName: payload.displayName };
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AuthUser | null>(() => {
    const token = getToken();
    return token ? decodeUser(token) : null;
  });

  // Токен меняется и мимо этого провайдера: перехватчик ответов обменивает его по refresh после
  // 401 — например, когда администратор снял роль, и сервер перестал принимать прежний токен
  // (issue #946). Без этой подписки экран остался бы с прежней ролью до перезагрузки страницы:
  // кнопки на месте, действия отвечают отказом.
  useEffect(() => onTokenChanged(token => setUser(token ? decodeUser(token) : null)), []);

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
