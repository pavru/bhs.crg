import { workNav, settingsNav } from '@/shared/ui/navConfig';
import { visibleNav } from '@/shared/ui/navAccess';
import { useAccess, NO_ACCESS } from '@/shared/api/access';
import { useWorkspace } from '@/shared/ui/workspaceContext';
import { splitNav } from '@/shared/ui/workspaces';
import { buildRail } from '@/shared/ui/railSections';

/**
 * Разделы меню для этого человека и этого пространства — одно место на панель, выдвижное меню и
 * плашку «открыто вне пространства» (issue #1355). Читали они одно и то же правило, а считали
 * каждый по-своему; разойдись подсчёты — плашка звала бы «вернуться» туда, чего нет в меню.
 */
export function useNavSections() {
  // Навигация строится ТОЛЬКО по ответу /api/account/access (ТЗ AUTH-14). Роли здесь больше нет:
  // она приходила из токена, то есть устаревала ровно тогда, когда это опаснее всего — при снятии
  // прав, — и показывала разделы, которые отвечают отказом (issue #952).
  const { data: access } = useAccess();
  const allWork = visibleNav(workNav, access ?? NO_ACCESS);
  const settings = visibleNav(settingsNav, access ?? NO_ACCESS);
  // Пространство сужает только рабочие разделы (ТЗ AUTH-16.1, issue #1001). «Система» —
  // администрирование, а не контекст работы: прятать «Пользователей» за пространством «Счета» значило
  // бы заставлять администратора переключаться ради своего же меню. Скрытое не теряется — оно в
  // строке «Ещё N разделов вне пространства» и в палитре команд.
  const workspace = useWorkspace();
  // Делит то же правило, что проверяют тесты (`splitNav`), а не его копия здесь (ревью PR #1330).
  const { inside: work, outside } = splitNav(allWork, workspace.current, access ?? NO_ACCESS);
  // Разделы рейла и их подпанели (issue #1429) — из тех же отобранных списков, а не своим отбором.
  const rail = buildRail(work, settings);
  return { allWork, work, outside, settings, rail };
}
