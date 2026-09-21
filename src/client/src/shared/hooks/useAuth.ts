import { createContext, useContext } from 'react';

export interface AuthUser {
  sub: string;
  email: string;
  displayName: string;
  /*
   * Роли здесь НЕТ (issue #984). Поле держало первую роль из токена и не использовалось нигде:
   * решений по имени роли не принимают — что доступно, отвечает `/api/account/access` (AUTH-14),
   * а подпись приходит с сервера. Оставленное, оно выглядело бы ответом на вопрос «какая у меня
   * роль» — и отвечало бы на него неверно у всякого, у кого ролей больше одной.
   */
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
