import { createContext, useContext } from 'react';
import { useCan } from '@/shared/api/access';
import type { UserSettings } from '@/shared/api/userSettings';
import type { NavItem } from './navConfig';
import { ALL, ALL_NAME, type OwnWorkspace, type Workspace } from './workspaces';

/**
 * Открытое рабочее пространство и действия с ним (issue #1001). Поставщик — `WorkspaceProvider` в
 * оболочке приложения; правила — `workspaces.ts`.
 */
export interface WorkspaceContextValue {
  /** Переключатель виден: доступно больше одного модуля (AUTH-16.1). */
  shown: boolean;
  current: Workspace;
  /** Список переключателя: системные, затем свои. */
  list: Workspace[];
  own: OwnWorkspace[];
  /** Умолчание из профиля; null — «открывать последнее выбранное». */
  defaultChoice: string | null;
  choose: (key: string) => void;
  /**
   * Изменить свои пространства. Операция применяется к СВЕЖЕМУ списку с сервера, а не к кэшу
   * (ревью PR #1330): пространство, заведённое на другом компьютере минуту назад, иначе стёрлось бы
   * молча. Вернула строку — это отказ с причиной. `also` — прочие настройки той же правкой
   * (умолчание), посчитанные по тем же свежим настройкам.
   */
  updateOwn: (
    change: (fresh: OwnWorkspace[]) => OwnWorkspace[] | string,
    also?: (fresh: UserSettings) => Record<string, string | null>,
  ) => Promise<void>;
  setDefault: (key: string | null) => Promise<void>;
  /** Модуль показывается в этом пространстве (страница стройки, AUTH-16.1). Доступ — отдельно. */
  shows: (module: string) => boolean;
  /** Раздел ядра показывается в этом пространстве (панель «Наборы данных» стройки). */
  showsSection: (section: string) => boolean;
  /** Пункт навигации — в пространстве. */
  inside: (item: NavItem) => boolean;
}

const ALL_WORKSPACE: Workspace = { key: ALL, name: ALL_NAME, kind: 'all', modules: [] };

/**
 * Вне поставщика (тесты страниц, экран входа) — «все доступные»: ничего не сужено. Падать здесь
 * было бы хуже: страница стройки в тесте не обязана поднимать оболочку приложения.
 */
const NEUTRAL: WorkspaceContextValue = {
  shown: false,
  current: ALL_WORKSPACE,
  list: [ALL_WORKSPACE],
  own: [],
  defaultChoice: null,
  choose: () => {},
  updateOwn: () => Promise.resolve(),
  setDefault: () => Promise.resolve(),
  shows: () => true,
  showsSection: () => true,
  inside: () => true,
};

export const WorkspaceContext = createContext<WorkspaceContextValue>(NEUTRAL);

export function useWorkspace(): WorkspaceContextValue {
  return useContext(WorkspaceContext);
}

/**
 * Модуль доступен И показан в открытом пространстве — для пунктов модуля на общих страницах
 * (страница стройки: комплекты, готовность, «требует разбора»). Одно место вместо проверки,
 * скопированной в каждую страницу (ревью PR #1330): копии расходятся, и пункт модуля остаётся
 * виден там, где соседний уже скрыт.
 */
export function useModuleShown(code: string): boolean {
  const can = useCan();
  const workspace = useWorkspace();
  return can.module(code) && workspace.shows(code);
}
