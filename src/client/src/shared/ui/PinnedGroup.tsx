import type { ReactNode } from 'react';
import { Link } from 'react-router';
import { CountText } from './CountText';
import { Icon } from './Icon';
import { KeepOff, KeyboardArrowDown } from './icons';
import { useGuardedNavClick } from './NavigationGuard';
import { MICRO_HEADING, panelLink } from './panelLink';
import { usePinnedPages } from './pinnedPagesContext';
import { RowActionsMenu, type RowAction } from './RowActionsMenu';
import { PINNED_GROUP_LABEL, UNPIN_LABEL, entryNote, type PinnedEntry } from './pinnedSections';

const GROUP_ID = 'pinned-group-items';

/**
 * Группа «Закреплённые» — первой в подпанели рабочего раздела (макет Р0, стол — вариант А): одна и та
 * же в каждой подпанели, в порядке списка. Своё место пункта в разделе не меняется — человек находит
 * страницу и по-старому.
 *
 * Вторая строка называет раздел страницы и пометку «вне пространства»: группа общая на все разделы, и
 * без неё «Сметы» в подпанели «Затрат» читались бы как страница «Затрат». Пункт группы выбранным не
 * подсвечивается — выбранной стоит страница на своём месте, а две подсвеченные строки одной подпанели
 * читались бы как два разных места.
 *
 * Группа сворачивается заголовком; свёрнутая держит заголовок и число — иначе её не отличить от пустой.
 * Закреплений нет — группы нет вовсе: пустая группа в каждой подпанели звала бы в никуда.
 */
export function PinnedGroup({ entries }: { entries: PinnedEntry[] }) {
  const { collapsed, setCollapsed } = usePinnedPages();
  if (entries.length === 0) return null;

  return (
    <div data-testid="pinned-group" className="mb-3">
      <button type="button" aria-expanded={!collapsed} aria-controls={GROUP_ID}
        onClick={() => setCollapsed(!collapsed)}
        className={`${MICRO_HEADING} w-full flex items-center gap-2 h-8 rounded-full text-left state-layer focus-ring`}>
        <span className="flex-1 min-w-0 truncate">{PINNED_GROUP_LABEL}</span>
        <CountText>{entries.length}</CountText>
        <Icon icon={KeyboardArrowDown} size={18} className={`shrink-0 transition-transform ${collapsed ? '-rotate-90' : ''}`} />
      </button>
      {!collapsed && (
        <div id={GROUP_ID} className="space-y-0.5">
          {entries.map(entry => <PinnedLink key={entry.item.to} entry={entry} />)}
        </div>
      )}
    </div>
  );
}

/**
 * Закреплённая страница — двумя строками: подпись и раздел (с «вне пространства»); «⋮» — «Открепить
 * страницу». Одна на группу подпанели и лист «Закреплённые» телефона (work-03): разойдясь, они показали бы
 * одно и то же закрепление по-разному.
 */
export function PinnedLink({ entry }: { entry: PinnedEntry }) {
  const { unpin } = usePinnedPages();
  const guardedClick = useGuardedNavClick();
  return (
    <PageRow actions={[{ key: 'unpin', label: UNPIN_LABEL, icon: KeepOff, onSelect: () => unpin(entry.item.to) }]}
      label={entry.item.label}>
      <Link to={entry.item.to} onClick={guardedClick(entry.item.to)} className={`${panelLink(false, true)} pr-12`}>
        <span className="flex-1 min-w-0 flex flex-col">
          <span className="truncate">{entry.item.label}</span>
          <span className="truncate text-body-small text-on-surface-variant">{entryNote(entry)}</span>
        </span>
      </Link>
    </PageRow>
  );
}

/**
 * Строка подпанели с меню «⋮» у правого края (решение владельца по Р0: булавка — в «⋮» строки, а не в
 * заголовке страницы). Меню видно при наведении и фокусе строки: двадцать постоянных кнопок рядом с
 * двадцатью подписями отняли бы у них место и спорили бы с ними за взгляд. На касании наведения нет —
 * там кнопка видна всегда (`pointer-coarse`), иначе закрепить было бы нечем, кроме касания пустоты.
 */
export function PageRow({ actions, label, children }: { actions: RowAction[]; label: string; children: ReactNode }) {
  return (
    <div className="group/page relative">
      {children}
      <span className="absolute right-1 top-1/2 -translate-y-1/2 opacity-0 group-hover/page:opacity-100 group-focus-within/page:opacity-100 has-[[aria-expanded=true]]:opacity-100 pointer-coarse:opacity-100">
        <RowActionsMenu ariaLabel={`Действия: ${label}`} actions={actions} />
      </span>
    </div>
  );
}
