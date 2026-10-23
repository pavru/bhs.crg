import { Navigate } from 'react-router';
import { NO_ACCESS, useAccess } from '@/shared/api/access';
import { useUserSettings } from '@/shared/api/userSettings';
import { useAuth } from '@/shared/hooks/useAuth';
import { entryPage } from './entryPage';
import { settingsNav, workNav } from './navConfig';
import { NoAccessPage } from './NoAccessPage';
import { useWorkspace } from './workspaceContext';

/**
 * Адрес `/` и вход (ТЗ AUTH-16.1, plan-02): ведёт на страницу входа, вычисленную по доступу и
 * пространству (`entryPage.ts`); видимых пунктов нет — страница «нет доступа» (AUTH-15).
 *
 * Ждёт и доступ, и настройки пользователя: пространство приходит настройкой, и решать по «всем
 * модулям», пока она в пути, значило бы открыть страницу не того пространства.
 */
export function EntryRedirect() {
  const { user } = useAuth();
  const { data: access, isPending } = useAccess();
  const { isPending: settingsPending } = useUserSettings(!!user);
  const workspace = useWorkspace();
  if (isPending || settingsPending) return null;

  const to = entryPage([...workNav, ...settingsNav], workspace.current, access ?? NO_ACCESS);
  return to ? <Navigate to={to} replace /> : <NoAccessPage missing={null} />;
}
