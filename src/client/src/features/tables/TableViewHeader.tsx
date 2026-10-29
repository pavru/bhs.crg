import { useState, type ReactNode } from 'react';
import { Link, useLocation, useNavigate } from 'react-router';
import * as Popover from '@radix-ui/react-popover';
import { Icon } from '@/shared/ui/Icon';
import { Check, KeyboardArrowDown, Table } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { PageHeader } from '@/shared/ui/PageHeader';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { RowActionsMenu, type RowAction } from '@/shared/ui/RowActionsMenu';
import { useToast } from '@/shared/ui/Toast';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import type { TableDeclaration } from '@/shared/api/tables';
import { useDeleteTableView, useSaveTableView, type SavedTableView } from '@/shared/api/tableViews';
import { SaveViewDialog } from './SaveViewDialog';
import {
  basedOnOf, copyTitle, keeping, listActions, lookupTitle, placeHash, rebasedHash, saveActions, stateOf, viewGroups,
  viewModified, withViewReset, type ViewLookup,
} from './savedViews';
import { wholeTableHash, type TableView, type ViewChange } from './tableViewState';
import { Tooltip } from '@/shared/ui/Tooltip';

/** Право заводить и править общие представления (сервер проверяет его сам — здесь только показ). */
const SHARE = 'core.views.share';

type Asked = { kind: 'create' } | { kind: 'rename' | 'remove'; saved: SavedTableView };

/**
 * Шапка экрана таблицы (задача G3a, issue #1095): заголовок — он же выбор представления, пометка
 * несохранённых изменений и действия над ними.
 *
 * <p><b>Заголовок — кнопка.</b> Список под ним — «Таблица целиком», готовые представления модуля,
 * свои и общие. Другого входа в сохранённые представления нет, и другого места, где видно, ЧТО
 * сейчас открыто, тоже.</p>
 *
 * <p><b>Невыполнимое не показано</b> (решение владельца, 11.10.2026). Представление модуля заперто,
 * чужое общее без права не правится — у обоих «Сохранить» нет вовсе, есть «Сохранить как своё».
 * Сервер отказывает и сам (409 «заперто», 403), но до отказа человека не доводим.</p>
 */
export function TableViewHeader({ address, decl, lookup, view, base, saved, savedProblem, onChange, onLeaving }: {
  address: string;
  decl: TableDeclaration;
  lookup: ViewLookup;
  view: TableView;
  /** От чего отсчитаны изменения: умолчания таблицы либо настройка открытого представления. */
  base: TableView;
  saved: SavedTableView[] | undefined;
  /** Список не пришёл — причина. Пустые группы тогда НЕ «представлений нет». */
  savedProblem: string | null;
  onChange: (change: ViewChange) => void;
  /** Открытое представление удалено этим человеком, экран уходит: его идентификатор. */
  onLeaving: (id: string) => void;
}) {
  const { data: access = NO_ACCESS } = useAccess();
  const navigate = useNavigate();
  const { hash } = useLocation();
  const toast = useToast();
  // «Сохранить» открытого и правки из списка — разными мутациями: иначе «Сделать общим» у соседнего
  // представления крутило бы кнопку «Сохранить», будто сохраняется открытое.
  const save = useSaveTableView(address);
  const change = useSaveTableView(address);
  const remove = useDeleteTableView(address);
  const [open, setOpen] = useState(false);
  const [asked, setAsked] = useState<Asked | null>(null);

  const canShare = hasPermission(access, SHARE);
  const current = lookup.state === 'saved' ? lookup.saved : null;
  const title = lookupTitle(lookup, decl.title);
  const off = decl.state === 'module-off';
  const modified = !off && viewModified(view, base);
  const can = saveActions(view, lookup);
  const groups = viewGroups(decl.views, saved);
  const path = `/tables/${encodeURIComponent(address)}`;
  // Диалог открывается из списка — список за ним закрывается, иначе он остался бы висеть под диалогом.
  const ask = (next: Asked) => { setOpen(false); setAsked(next); };

  // Сохранить поверх. Адрес после этого — только место в таблице: основа стала тем, что на экране.
  async function saveOver() {
    if (!current) return;
    try {
      const stored = await save.mutateAsync({
        saved: current,
        body: { title: current.title, shared: current.shared, state: stateOf(view), basedOn: current.basedOn },
      });
      // От адреса на момент ответа: правка, сделанная за время запроса, остаётся и помечена.
      void navigate({ hash: rebasedHash(window.location.hash, base, stored.state) }, { replace: true });
      toast.success(`Представление «${current.title}» сохранено`);
    } catch (refusal) {
      toast.apiError(refusal, 'Представление не сохранено');
    }
  }

  async function create(name: string, shared: boolean) {
    const created = await change.mutateAsync({
      body: { title: name, shared, state: stateOf(view), basedOn: basedOnOf(lookup) },
    });
    void navigate(`${path}/${created.id}${placeHash(view)}`);
  }

  async function setShared(target: SavedTableView, shared: boolean) {
    try {
      await change.mutateAsync({ saved: target, body: keeping(target, { shared }) });
      toast.success(shared ? `«${target.title}» теперь общее` : `«${target.title}» возвращено в свои`);
    } catch (refusal) {
      toast.apiError(refusal, 'Представление не изменено');
    }
  }

  async function removeAsked(target: SavedTableView) {
    // Удалено открытое — уходим к таблице целиком с тем же отбором. Переход маршрутизатор отдаёт
    // отложенной перерисовкой, а из списка представление пропадает сразу: экрану говорим «ухожу» тем
    // же шагом, иначе он успел бы нарисовать «представление удалено».
    const leave = target.id === current?.id
      ? () => { onLeaving(target.id); void navigate(`${path}${wholeTableHash(view)}`, { replace: true }); }
      : undefined;
    await remove.mutateAsync({ saved: target, leave });
  }

  function actionsOf(target: SavedTableView): RowAction[] {
    const allowed = listActions(target, canShare);
    const all: (RowAction | false)[] = [
      allowed.rename && { key: 'rename', label: 'Переименовать', onSelect: () => ask({ kind: 'rename', saved: target }) },
      allowed.share && { key: 'share', label: 'Сделать общим', disabled: change.isPending, onSelect: () => void setShared(target, true) },
      allowed.unshare && { key: 'unshare', label: 'Вернуть в свои', disabled: change.isPending, onSelect: () => void setShared(target, false) },
      allowed.remove && { key: 'remove', label: 'Удалить', danger: true, onSelect: () => ask({ kind: 'remove', saved: target }) },
    ];
    return all.filter((a): a is RowAction => !!a);
  }

  const row = (key: string, to: string, label: string, chosen: boolean, note?: string | null, actions: RowAction[] = []) => (
    <li key={key} className="flex items-center gap-1 rounded-md hover:bg-surface-container">
      {/* Открытое ведёт туда же, где человек стоит: щелчок по отмеченному не сбрасывает несохранённое. */}
      <Link to={chosen ? { hash } : to} onClick={() => setOpen(false)} aria-current={chosen ? 'page' : undefined}
        className="flex-1 min-w-0 flex items-center gap-2 px-2 py-1.5 text-sm text-on-surface">
        <Icon icon={Check} size={18} aria-hidden className={chosen ? 'text-primary shrink-0' : 'invisible shrink-0'} />
        <span className="truncate">{label}</span>
        {note && <span className="shrink-0 text-xs text-on-surface-variant">{note}</span>}
      </Link>
      {actions.length > 0 && <RowActionsMenu actions={actions} ariaLabel={`Действия с представлением «${label}»`} />}
    </li>
  );
  const savedRow = (v: SavedTableView) =>
    row(v.id, `${path}/${v.id}`, v.title, v.id === current?.id, v.shared && !v.mine ? v.author : null, actionsOf(v));

  return (
    <div className="mb-3">
      <PageHeader
        title={
          <Popover.Root open={open} onOpenChange={setOpen}>
            <Tooltip content="Выбрать представление">
              <Popover.Trigger asChild>
                <button type="button"
                  className="flex items-center gap-2 rounded-md -mx-1.5 px-1.5 py-0.5 hover:bg-surface-container max-w-[calc(100%+0.75rem)]">
                  <Icon icon={Table} size={18} className="text-on-surface-variant shrink-0" aria-hidden />
                  <span className="truncate">{title}</span>
                  <Icon icon={KeyboardArrowDown} size={18} className="text-on-surface-variant shrink-0" aria-hidden />
                </button>
              </Popover.Trigger>
            </Tooltip>
            <Popover.Portal>
              <Popover.Content align="start" sideOffset={6} aria-label="Представления таблицы"
                className="z-40 w-80 max-h-[70vh] overflow-auto rounded-lg bg-surface border border-outline-variant p-2 focus:outline-none"
                style={{ boxShadow: 'var(--md-sys-elevation-level2)' }}>
                <ul>
                  {/* К таблице целиком — с тем же отбором: из представления к ней уходят посмотреть те же строки всеми колонками. */}
                  {row('table', `${path}${wholeTableHash(view)}`, 'Таблица целиком', lookup.state === 'table')}
                </ul>
                <Group title="Готовые" empty={null}>
                  {groups.ready.map(p => row(p.code, `${path}/${encodeURIComponent(p.code)}`, p.title,
                    lookup.state === 'found' && lookup.preset.code === p.code))}
                </Group>
                {!off && savedProblem && <p role="alert" className="mt-2 px-2 text-xs text-error">{savedProblem}</p>}
                {!off && !savedProblem && !saved && <p className="mt-2 px-2 text-xs text-on-surface-variant">Сохранённые представления загружаются…</p>}
                {!off && saved && (
                  <>
                    <Group title="Мои" empty="Настройте таблицу и сохраните её — представление появится здесь.">
                      {groups.own.map(savedRow)}
                    </Group>
                    <Group title="Общие" empty={null}>{groups.shared.map(savedRow)}</Group>
                  </>
                )}
              </Popover.Content>
            </Popover.Portal>
          </Popover.Root>
        }
        badge={modified && (
          <>
            <span className="text-sm text-warning" role="status">· изменено</span>
            {can.save && <Button size="sm" variant="tonal" loading={save.isPending} onClick={() => void saveOver()}>Сохранить</Button>}
            {can.saveAs && <Button size="sm" variant="text" onClick={() => setAsked({ kind: 'create' })}>Сохранить как своё</Button>}
            <Button size="sm" variant="text" onClick={() => onChange(v => withViewReset(v, base))}>Сбросить</Button>
          </>
        )}
        subtitle={`Строка — ${decl.grain}. ${decl.boundary.replace(/\.$/, '')}.`} />

      {asked?.kind === 'create' && (
        <SaveViewDialog heading="Сохранить представление" action="Сохранить" share={canShare}
          initial={current || lookup.state === 'found' ? copyTitle(title) : ''}
          onSave={create} onClose={() => setAsked(null)} />
      )}
      {asked?.kind === 'rename' && (
        <SaveViewDialog heading="Переименовать представление" action="Переименовать" initial={asked.saved.title}
          onSave={name => change.mutateAsync({ saved: asked.saved, body: keeping(asked.saved, { title: name }) })}
          onClose={() => setAsked(null)} />
      )}
      {asked?.kind === 'remove' && (
        <ConfirmDialog open onOpenChange={next => { if (!next) setAsked(null); }}
          title={`Удалить представление «${asked.saved.title}»?`}
          description={asked.saved.shared
            ? 'Представление общее: оно пропадёт у всех, а закладки на него перестанут открываться.'
            : 'Закладки на него перестанут открываться. Сама таблица и её строки не меняются.'}
          confirmLabel="Удалить представление" errorTitle="Представление не удалено"
          onConfirm={() => removeAsked(asked.saved)} />
      )}
    </div>
  );
}

/** Группа списка. Пустая не показывается вовсе, если ей нечего сказать (`empty` не назван). */
function Group({ title, empty, children }: { title: string; empty: string | null; children: ReactNode[] }) {
  if (children.length === 0 && !empty) return null;
  return (
    <section className="mt-2">
      <h2 className="px-2 text-caption uppercase tracking-wide text-on-surface-variant">{title}</h2>
      {children.length > 0 ? <ul>{children}</ul> : <p className="px-2 py-1 text-xs text-on-surface-variant">{empty}</p>}
    </section>
  );
}
