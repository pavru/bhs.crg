import type { ReactNode } from 'react';
import { Navigate, useNavigate, useParams } from 'react-router';
import { NO_ACCESS, useAccess } from '@/shared/api/access';
import { panelAt, panelsAt, defaultPanel, type PanelLevel, type ShownPanel } from '@/shared/ui/constructionPanels';
import { NavItem, NavSection } from '@/shared/ui/ListDetailShell';
import { missingFor, allowed } from '@/shared/ui/navAccess';
import { NoAccessPage } from '@/shared/ui/NoAccessPage';
import { useWorkspace } from '@/shared/ui/workspaceContext';
import { DataSetsResource } from '@/features/datasets/DataSetsResource';
import { CatalogPanel } from './CatalogPanel';
import { SubscribersResource } from './SubscribersResource';

/** Чья это панель: стройки или её раздела. Панель получает только это — не внутренности карточки. */
export interface PanelScope {
  constructionId: string;
  sectionId?: string;
}

/**
 * Что рисует панель реестра (`constructionPanels.ts`). Отдельно от реестра нарочно: реестр читают
 * навигация и пространства, и экраны панелей им ни к чему. Запись реестра без содержимого здесь ловит
 * `constructionPanels.test.ts`.
 */
export const panelViews: Record<string, (scope: PanelScope) => ReactNode> = {
  catalog: scope => <CatalogPanel {...scope} />,
  datasets: ({ constructionId, sectionId }) => sectionId
    ? <DataSetsResource scope="Section" scopeId={sectionId} />
    : <DataSetsResource scope="Construction" scopeId={constructionId} />,
  subscribers: ({ constructionId, sectionId }) => (
    <div className="mx-auto max-w-5xl">
      {sectionId
        ? <SubscribersResource scope="Section" scopeId={sectionId} />
        : <SubscribersResource scope="Construction" scopeId={constructionId} />}
    </div>
  ),
};

/**
 * Панели страницы стройки или раздела: пункты блоков «Работы» и «Эта стройка» («Этот раздел»),
 * содержимое открытой панели и перенаправление голого или неизвестного адреса на панель по умолчанию.
 *
 * Пока доступ в пути, ни пунктов, ни перенаправления нет: решить «первую видимую» по пустому доступу
 * значило бы увести человека с «Ходом работ» на «Каталог».
 */
export function useScopePanels(level: PanelLevel, base: string, scope: PanelScope) {
  const { panel } = useParams<{ panel?: string }>();
  const navigate = useNavigate();
  const { data, isPending } = useAccess();
  const access = data ?? NO_ACCESS;
  const workspace = useWorkspace();
  const active = panelAt(level, panel);
  const { work, own } = panelsAt(level, access, workspace.inside, active?.id);

  const items = (list: ShownPanel[]) => list.map(p => (
    <NavItem key={p.id} icon={<p.icon size={17} />} label={p.label} active={p.id === active?.id}
      note={p.outside ? 'вне пространства' : undefined} onClick={() => navigate(`${base}/${p.id}`)} />
  ));

  let detail: ReactNode = null;
  if (!isPending) {
    if (!active) {
      const first = defaultPanel(level, access, workspace.inside);
      detail = first ? <Navigate to={`${base}/${first.id}`} replace /> : null;
    } else if (!allowed(active, access)) {
      detail = <NoAccessPage what={active.label} missing={missingFor(active, access)} />;
    } else {
      detail = panelViews[active.id]?.(scope) ?? null;
    }
  }

  return {
    /** Блок «Работы» — над разделами; нет панелей — нет и блока. */
    workNav: !isPending && work.length > 0 ? <><NavSection label="Работы" />{items(work)}</> : null,
    ownNav: (label: string) => (isPending ? null : <><NavSection label={label} />{items(own)}</>),
    detail,
  };
}
