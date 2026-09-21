import { createContext, useContext } from 'react';

export interface AuthUser {
  sub: string;
  email: string;
  displayName: string;
  /**
   * Техническое имя роли из токена.
   *
   * ⚠️ Решений по нему не принимают: что доступно — отвечает `/api/account/access` (AUTH-14).
   * Перечисления здесь больше нет НАРОЧНО: ролей девять системных плюс те, что заводит
   * администратор (issue #951), и тип из двух имён означал бы, что про остальные клиент не знает.
   */
  role: string;
}

export interface AuthContextValue {
  user: AuthUser | null;
  login: (email: string, password: string, remember?: boolean) => Promise<void>;
  /** Обновить сессию по свежей паре токенов (напр. re-issue при смене пароля). */
  updateSession: (accessToken: string, refreshToken: string) => void;
  logout: () => void;
}

export const AuthContext = createContext<AuthContextValue | null>(null);

export function useAuth() {
  const ctx = useContext(AuthContext);
  if (!ctx) throw new Error('useAuth must be used inside AuthProvider');
  return ctx;
}
