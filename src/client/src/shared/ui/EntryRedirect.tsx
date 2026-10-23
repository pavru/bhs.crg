import { Navigate } from 'react-router';
import { NO_ACCESS, useAccess } from '@/shared/api/access';
import { useUserSettings } from '@/shared/api/userSettings';
import { useAuth } from '@/shared/hooks/useAuth';
import { entryPage } from './entryPage';
import { settingsNav, workNav } from './navConfig';
import { useNavSections } from './navSections';
import { NoAccessPage } from './NoAccessPage';

/**
 * Адрес `/` и вход (ТЗ AUTH-16.1, plan-02): ведёт на страницу входа, вычисленную из того, что
 * показывает меню (`useNavSections`, `entryPage.ts`); показанных пунктов нет — страница «нет
 * доступа» (AUTH-15).
 *
 * Ждёт и доступ, и настройки пользователя: пространство приходит настройкой, и решать по «всем
 * модулям», пока она в пути, значило бы открыть страницу не того пространства.
 *
 * ⚠️ Сам адрес `/` занят этим перенаправлением: пункт меню с ним не откроется никогда (ловит
 * `entryRankProblems`). Главная (`shell-08`) встаёт своим адресом.
 */
export function EntryRedirect() {
  const { user } = useAuth();
  const { data: access, isPending } = useAccess();
  const { isPending: settingsPending } = useUserSettings(!!user);
  const { work, settings } = useNavSections();
  if (isPending || settingsPending) return null;

  const to = entryPage([...work, ...settings], access ?? NO_ACCESS, [...workNav, ...settingsNav]);
  return to ? <Navigate to={to} replace /> : <NoAccessPage missing={null} />;
}
