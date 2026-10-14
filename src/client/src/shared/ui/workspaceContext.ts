import { createContext, useContext } from 'react';
import { useCan } from '@/shared/api/access';
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
  /** Записать свои пространства — вместе с другими настройками одной правкой (умолчание, выбор). */
  saveOwn: (next: OwnWorkspace[], also?: Record<string, string | null>) => Promise<void>;
  setDefault: (key: string | null) => Promise<void>;
  /** Модуль показывается в этом пространстве (страница стройки, AUTH-16.1). Доступ — отдельно. */
  shows: (module: string) => boolean;
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
  saveOwn: () => Promise.resolve(),
  setDefault: () => Promise.resolve(),
  shows: () => true,
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
