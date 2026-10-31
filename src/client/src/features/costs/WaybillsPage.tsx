import { MISSING } from './lostReferences';
import { useCallback, useRef, useState } from 'react';
import { useQueryClient } from '@tanstack/react-query';
import { useSearchParams } from 'react-router';
import { PALETTE_ACTIONS } from '@/shared/ui/paletteActions';
import { PrimaryActionButton } from '@/shared/ui/PrimaryActionButton';
import { usePaletteIntent } from '@/shared/ui/usePaletteIntent';
import { Icon } from '@/shared/ui/Icon';
import { Add, Checklist, HistoryIcon, Inventory, LocalShipping } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ListDetailShell, NavSearchInput } from '@/shared/ui/ListDetailShell';
import { useListDetailView } from '@/shared/ui/listDetailView';
import { useToast } from '@/shared/ui/Toast';
import { useCloseGone } from '@/shared/hooks/useCloseGone';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import { apiError } from '@/shared/utils/apiError';
import { useCostsConstructions } from '@/shared/api/invoices';
import {
  forgetWaybill, useCreateWaybill, useWaybill, type WaybillListItem,
} from '@/shared/api/waybills';
import { IssuedMaterialsDialog } from './IssuedMaterialsDialog';
import { WaybillForm } from './WaybillForm';
import { useOpenRecord } from './openRecord';
import { RereadFailedNotice } from '@/shared/ui/RereadFailedNotice';
import { formatDate } from '@/shared/format/format';
import { correctionMarkText } from './waybillCorrection';
import { FilterHidesAll, NotInListNotice } from '@/shared/siteFilter/SiteFilterNotes';
import { SitesFilter } from '@/shared/siteFilter/SitesFilter';
import { SEARCH_ALL_SITES, SHOW_ALL_SITES } from '@/shared/siteFilter/siteFilterTexts';
import { NOT_IN_LIST, SEARCH_HIDDEN, hiddenEmpty, useWaybillSites } from './useWaybillSites';
import { Tooltip } from '@/shared/ui/Tooltip';
import { Checkbox } from '@/shared/ui/Checkbox';

const PARAM = 'waybill';

/**
 * Расходные накладные: список слева, форма справа (задача D1 этапа 2, issue #1083).
 *
 * <p>Открытая накладная названа в адресе (`?waybill=…`) — как счёт: перезагрузка её не закрывает, а
 * выбор в списке адрес заменяет, а не копит историю (конвенция list-detail, issue #787).</p>
 *
 * <p>⚠️ Отбор «Сопоставить» делает сервер: по загруженному списку можно посчитать ждущие строки, но
 * утверждать по нему, что других таких накладных нет, нельзя. Им же — «Избранные стройки» (#1222): список
 * страничный, перечень уходит параметром `sites`, число скрытых приходит с ответом.</p>
 */
export function WaybillsPage() {
  const [params, setParams] = useSearchParams();
  const selected = params.get(PARAM) || null;
  // Узкое место (#1355): накладная, названная адресом, показывается карточкой — и при открытии, и при смене адреса.
  const ld = useListDetailView(selected);
  const open = (id: string) => setParams(prev => {
    const next = new URLSearchParams(prev);
    next.set(PARAM, id);
    return next;
  }, { replace: true });
  // Несохранённое в открытой форме: выбор другой накладной пересоздаёт форму, и без вопроса набранное
  // с бумаги пропало бы молча (ревью PR #1206). Вопрос задаёт сама форма — тем же диалогом, что и при
  // уходе со страницы.
  const leaveGuard = useRef<((proceed: () => void) => void) | null>(null);
  const onLeaveGuard = useCallback((ask: ((proceed: () => void) => void) | null) => { leaveGuard.current = ask; }, []);
  const setSelected = (id: string) => {
    ld.showDetail();
    if (id === selected) return;
    if (leaveGuard.current) leaveGuard.current(() => open(id)); else open(id);
  };

  const [query, setQuery] = useState('');
  const [unmatched, setUnmatched] = useState(false);
  const [materialsOpen, setMaterialsOpen] = useState(false);
  const { data: access = NO_ACCESS } = useAccess();
  const canEdit = hasPermission(access, 'costs.waybill.edit');

  const view = useWaybill(selected ?? undefined);
  const filter = useWaybillSites({ unmatched, selected, open: view.data });
  const { waybills, stale, hidden } = filter;
  const sites = useCostsConstructions();
  const create = useCreateWaybill();
  const toast = useToast();
  const qc = useQueryClient();

  // Накладную удалили, пока она была открыта (issue #1283): чтение отвечает «нет такой», а прежний
  // вид ещё в руках. Форма остаётся с набранным, а не сменяется отказом (ревью PR #1299): фоновое
  // перечитывание иначе стирало бы строки, вписанные с бумаги, в обход вопроса о правках. Узнанное
  // запоминается — следующее перечитывание может кончиться обрывом связи, а не тем же 404.
  // Тем же правилом форма переживает и любое другое неудачное перечитывание (issue #1287) — см. `openRecord`.
  const opened = useOpenRecord(selected, view);
  const gone = opened.kind === 'open' && opened.gone;
  const missing = gone || opened.kind === 'missing';

  // Вопроса о правках при закрытии нет: сохранять их уже некуда. Следующая накладная сама не открывается.
  const closeGone = useCloseGone(selected, missing, id => {
    leaveGuard.current = null;
    setParams(prev => {
      if (prev.get(PARAM) !== id) return prev;
      const next = new URLSearchParams(prev);
      next.delete(PARAM);
      return next;
    }, { replace: true });
  }, id => forgetWaybill(qc, id));

  const items = (waybills.data?.items ?? []).filter(i => matches(i, query));
  const searching = !!query.trim();
  // Прежний список, удержанный на время переключения фильтра, — не ответ на новый (ревью PR #1335): он
  // показан приглушённым, а пустые состояния и свидетельство молчат, пока не пришёл настоящий.
  const settled = !waybills.isPending && !waybills.isError && !stale;

  async function createDraft() {
    try {
      // Пустой черновик: накладную заводят, чтобы вписать её с бумаги, и требовать реквизиты до
      // первой строки — это требовать заполнить форму, которой ещё нет.
      const created = await create.mutateAsync({});
      open(created.id);
    } catch (e) { toast.apiError(e, 'Накладная не заведена'); }
  }
  // Сначала вопрос о правках, потом новая накладная: иначе черновик завёлся бы и при «Отмене».
  const addDraft = () => {
    if (leaveGuard.current) leaveGuard.current(() => void createDraft()); else void createDraft();
  };
  // Та же кнопка из командной палитры (issue #1433).
  usePaletteIntent(PALETTE_ACTIONS.waybillNew, addDraft, { busy: create.isPending });

  return (
    <ListDetailShell view={ld}
      title="Расходные накладные"
      subtitle="Проведённая накладная — материалы, выданные на стройку"
      actions={
        <>
          <Button variant="outlined" icon={<Icon icon={Inventory} size={18} />} onClick={() => setMaterialsOpen(true)}>
            Материалы на объекте
          </Button>
          {/* На телефоне — плавающей кнопкой (#1434). */}
          {canEdit && <PrimaryActionButton action={PALETTE_ACTIONS.waybillNew} icon={<Icon icon={Add} size={18} />}
            loading={create.isPending} onClick={addDraft} />}
          {materialsOpen && (
            <IssuedMaterialsDialog sites={sites.data ?? []} sitesFailed={sites.isError}
              onClose={() => setMaterialsOpen(false)} />
          )}
        </>
      }
      nav={
        <>
          <NavSearchInput value={query} onChange={setQuery} placeholder="Номер, стройка, склад…" />
          <div className="flex flex-wrap items-center gap-x-3 gap-y-1.5 px-3 py-1.5">
            <label className="flex items-center gap-2 text-body-small text-on-surface-variant cursor-pointer">
              <Checkbox size="sm" checked={unmatched} onChange={setUnmatched} />
              <Icon icon={Checklist} size={18} />
              Только «Сопоставить»
            </label>
            {/* Очередь «Сопоставить» стройками не урезается (#1222) — отбора «Стройки» под ней нет. Под
                поиском — без числа: оно о всём отборе, а поиск видит только загруженное. */}
            {!unmatched && <SitesFilter hidden={hidden ?? undefined} partial={searching} />}
          </div>
          <div className="flex-1 overflow-y-auto">
            {waybills.isPending && <p className="px-3 py-2 text-body-small text-on-surface-variant">Загрузка…</p>}
            {waybills.isError && (
              <p className="px-3 py-2 text-body-small text-error">
                Список не пришёл. Это отказ чтения, а не пустой список: накладные могут быть.
              </p>
            )}
            {stale && <p className="px-3 py-2 text-body-small text-on-surface-variant">Обновляется…</p>}
            {settled && items.length === 0 && !!hidden && (
              <FilterHidesAll testId="waybill-list-sites-empty" onShowAll={filter.showAll}
                text={searching ? SEARCH_HIDDEN : hiddenEmpty(hidden)} action={searching ? SEARCH_ALL_SITES : SHOW_ALL_SITES} />
            )}
            {settled && items.length === 0 && !hidden && (
              <p className="px-3 py-2 text-body-small text-on-surface-variant">
                {searching ? 'Ничего не найдено.'
                  : unmatched ? 'Сопоставлять нечего: строк без позиции номенклатуры нет ни у одной накладной.'
                    : 'Накладных пока нет.'}
              </p>
            )}
            {/* Прежние строки не нажимаются: строка, которая вот-вот исчезнет, открыла бы «не ту» накладную. */}
            <div className={stale ? 'opacity-50 pointer-events-none' : undefined} aria-busy={stale || undefined}
              inert={stale || undefined}>
              {items.map(item => (
                <ListRow key={item.id} item={item} active={item.id === selected} onClick={() => setSelected(item.id)} />
              ))}
            </div>
            {/* Обрезанный молча список читался бы как «такой накладной нет». */}
            {settled && waybills.data?.more && (
              <p className="px-3 py-2 text-body-small text-warning">
                Показаны самые свежие накладные, это не все. Поиск идёт по показанным: не нашли нужную —
                включите отбор «Сопоставить» или откройте её по ссылке.
              </p>
            )}
          </div>
        </>
      }
      detail={
        // «Не выбрано», «грузится», «нет такой», «не пришло» и «открыта» — разные вещи: см. `openRecord`.
        opened.kind === 'none' ? (
          <div className="flex-1 grid place-items-center">
            <EmptyState icon={<Icon icon={LocalShipping} size={40} />} title="Выберите накладную"
              description={canEdit ? 'Или заведите новую — черновик сохранится пустым.' : undefined} />
          </div>
        ) : opened.kind === 'loading' ? (
          <div className="flex-1 grid place-items-center text-body-small text-on-surface-variant">Накладная загружается…</div>
        ) : opened.kind === 'missing' ? (
          // «Повторить» здесь нет: накладной нет, и повтор ответит тем же.
          <div className="flex-1 grid place-items-center p-6" data-testid="waybill-gone">
            <div className="max-w-md text-center space-y-2">
              <p className="text-body-medium text-on-surface-variant">Накладной нет: её удалили или ссылка неверна.</p>
              <Button variant="outlined" onClick={() => closeGone(opened.id)}>Закрыть</Button>
            </div>
          </div>
        ) : opened.kind === 'unread' ? (
          // Только для накладной, не прочитанной НИ РАЗУ: неудачное перечитывание открытой форму не снимает.
          <div className="flex-1 grid place-items-center p-6">
            <div className="max-w-md text-center space-y-2">
              <p className="text-body-medium text-error">Накладная не открылась: {apiError(opened.error, 'сервер отказал')}</p>
              <p className="text-body-small text-on-surface-variant">Это отказ чтения, а не пустая накладная.</p>
              <Button variant="outlined" onClick={() => void view.refetch()}>Повторить</Button>
            </div>
          </div>
        ) : (
          <div className="flex-1 min-h-0 flex flex-col">
            {opened.rereadFailed != null && (
              <RereadFailedNotice what="Накладная не перечиталась" error={opened.rereadFailed}
                retrying={view.isFetching} onRetry={() => void view.refetch()} />
            )}
            {filter.openHidden && <NotInListNotice testId="waybill-not-in-list" text={NOT_IN_LIST} onShowAll={filter.showAll} />}
            <WaybillForm key={opened.id} view={opened.data} sites={sites.data ?? []}
              sitesFailed={sites.isError} canEdit={canEdit} gone={gone} onLeaveGuard={onLeaveGuard}
              onClose={() => closeGone(opened.id)} />
          </div>
        )
      }
    />
  );
}

function ListRow({ item, active, onClick }: { item: WaybillListItem; active: boolean; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick}
      className={`w-full text-left px-3 py-2 border-b border-outline-variant/60 ` +
        `state-layer ${active ? 'bg-primary-container' : ''}`}>
      <div className="flex items-center gap-2">
        <span className="text-body-medium text-on-surface font-medium truncate">{item.number ?? 'без номера'}</span>
        {item.issuedOn && <span className="text-body-small text-on-surface-variant shrink-0">{formatDate(item.issuedOn)}</span>}
        <div className="flex-1" />
        <span className={`text-body-small shrink-0 ${item.state === 'Posted' ? 'text-success' : 'text-on-surface-variant'}`}>
          {item.state === 'Posted' ? 'проведена' : 'черновик'}
        </span>
      </div>
      <div className="flex items-center gap-2 mt-0.5">
        <span className={`text-body-small truncate ${item.constructionName ? 'text-on-surface-variant' : item.constructionId ? 'text-error' : 'text-on-surface-variant'}`}>
          {item.constructionName ?? (item.constructionId ? MISSING.construction : 'стройка не выбрана')}
        </span>
        <div className="flex-1" />
        {/* Ноль не показываем: число «0» у каждой строки читается как шум. */}
        {item.unmatched > 0 && (
          <Tooltip content={`Не сопоставлено строк: ${item.unmatched} из ${item.lines}`}>
            <span className="inline-flex items-center gap-0.5 text-body-small text-warning shrink-0">
              <Icon icon={Checklist} size={18} />{item.unmatched}
            </span>
          </Tooltip>
        )}
      </div>
      {/* Исправленная после закрытия — видимой строкой с днём и причиной (D6, D10): на телефоне подсказка не
          открывается, а без пометки накладная закрытого месяца выглядела бы такой, какой её закрыли. */}
      {item.correctedAfterClosing && (
        <div className="flex items-center gap-1 mt-0.5 text-body-small text-on-surface-variant" data-testid="waybill-corrected">
          <Icon icon={HistoryIcon} size={18} className="shrink-0" aria-hidden />
          <span className="truncate">{correctionMarkText(item.correctedAfterClosing)}</span>
        </div>
      )}
    </button>
  );
}

function matches(item: WaybillListItem, query: string): boolean {
  const text = query.trim().toLowerCase();
  if (!text) return true;
  return [item.number, item.constructionName, item.warehouse]
    .some(value => (value ?? '').toLowerCase().includes(text));
}
