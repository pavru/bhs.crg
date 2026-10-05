import { useCallback, useRef, useState } from 'react';
import { useSearchParams } from 'react-router';
import { ListChecks, PackageCheck, Plus, Truck } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { EmptyState } from '@/shared/ui/EmptyState';
import { ListDetailShell, NavSearchInput } from '@/shared/ui/ListDetailShell';
import { useToast } from '@/shared/ui/Toast';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import { apiError } from '@/shared/utils/apiError';
import { useCostsConstructions } from '@/shared/api/invoices';
import { useCreateWaybill, useWaybill, useWaybills, type WaybillListItem } from '@/shared/api/waybills';
import { IssuedMaterialsDialog } from './IssuedMaterialsDialog';
import { WaybillForm } from './WaybillForm';
import { formatDate } from './invoiceFields';

const PARAM = 'waybill';

/**
 * Расходные накладные: список слева, форма справа (задача D1 этапа 2, issue #1083).
 *
 * <p>Открытая накладная названа в адресе (`?waybill=…`) — как счёт: перезагрузка её не закрывает, а
 * выбор в списке адрес заменяет, а не копит историю (конвенция list-detail, issue #787).</p>
 *
 * <p>⚠️ Отбор «Сопоставить» делает сервер: по загруженному списку можно посчитать ждущие строки, но
 * утверждать по нему, что других таких накладных нет, нельзя.</p>
 */
export function WaybillsPage() {
  const [params, setParams] = useSearchParams();
  const selected = params.get(PARAM) || null;
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
    if (id === selected) return;
    if (leaveGuard.current) leaveGuard.current(() => open(id)); else open(id);
  };

  const [query, setQuery] = useState('');
  const [unmatched, setUnmatched] = useState(false);
  const [materialsOpen, setMaterialsOpen] = useState(false);
  const { data: access = NO_ACCESS } = useAccess();
  const canEdit = hasPermission(access, 'costs.waybill.edit');

  const waybills = useWaybills(unmatched);
  const sites = useCostsConstructions();
  const view = useWaybill(selected ?? undefined);
  const create = useCreateWaybill();
  const toast = useToast();

  const items = (waybills.data?.items ?? []).filter(i => matches(i, query));

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

  return (
    <ListDetailShell
      title="Расходные накладные"
      subtitle="Проведённая накладная — материалы, выданные на стройку"
      headerAction={
        <div className="flex items-center gap-2">
          <Button variant="outlined" icon={<PackageCheck size={16} />} onClick={() => setMaterialsOpen(true)}>
            Материалы на объекте
          </Button>
          {canEdit && (
            <Button variant="filled" icon={<Plus size={16} />} loading={create.isPending} onClick={addDraft}>
              Новая накладная
            </Button>
          )}
          {materialsOpen && (
            <IssuedMaterialsDialog sites={sites.data ?? []} sitesFailed={sites.isError}
              onClose={() => setMaterialsOpen(false)} />
          )}
        </div>
      }
      nav={
        <>
          <NavSearchInput value={query} onChange={setQuery} placeholder="Номер, стройка, склад…" />
          <label className="flex items-center gap-2 px-3 py-1.5 text-xs text-fg3 cursor-pointer">
            <input type="checkbox" checked={unmatched} onChange={e => setUnmatched(e.target.checked)} />
            <ListChecks size={13} />
            Только «Сопоставить»
          </label>
          <div className="flex-1 overflow-y-auto">
            {waybills.isPending && <p className="px-3 py-2 text-xs text-fg3">Загрузка…</p>}
            {waybills.isError && (
              <p className="px-3 py-2 text-xs text-danger">
                Список не пришёл. Это отказ чтения, а не пустой список: накладные могут быть.
              </p>
            )}
            {!waybills.isPending && !waybills.isError && items.length === 0 && (
              <p className="px-3 py-2 text-xs text-fg3">
                {query ? 'Ничего не найдено.'
                  : unmatched ? 'Сопоставлять нечего: строк без позиции номенклатуры нет ни у одной накладной.'
                    : 'Накладных пока нет.'}
              </p>
            )}
            {items.map(item => (
              <ListRow key={item.id} item={item} active={item.id === selected} onClick={() => setSelected(item.id)} />
            ))}
            {/* Обрезанный молча список читался бы как «такой накладной нет». */}
            {waybills.data?.more && (
              <p className="px-3 py-2 text-xs text-warning">
                Показаны самые свежие накладные, это не все. Поиск идёт по показанным: не нашли нужную —
                включите отбор «Сопоставить» или откройте её по ссылке.
              </p>
            )}
          </div>
        </>
      }
      detail={
        // Три состояния, как у счёта: «не выбрано», «грузится» и «не пришло» — разные вещи.
        !selected ? (
          <div className="flex-1 grid place-items-center">
            <EmptyState icon={<Truck size={28} />} title="Выберите накладную"
              description={canEdit ? 'Или заведите новую — черновик сохранится пустым.' : undefined} />
          </div>
        ) : view.isPending ? (
          <div className="flex-1 grid place-items-center text-xs text-fg3">Накладная загружается…</div>
        ) : view.isError || !view.data ? (
          <div className="flex-1 grid place-items-center p-6">
            <div className="max-w-md text-center space-y-2">
              <p className="text-sm text-danger">Накладная не открылась: {apiError(view.error, 'сервер отказал')}</p>
              <p className="text-xs text-fg3">Это отказ чтения, а не пустая накладная.</p>
              <Button size="sm" variant="outlined" onClick={() => void view.refetch()}>Повторить</Button>
            </div>
          </div>
        ) : (
          <WaybillForm key={view.data.id} view={view.data} sites={sites.data ?? []}
            sitesFailed={sites.isError} canEdit={canEdit} onLeaveGuard={onLeaveGuard} />
        )
      }
    />
  );
}

function ListRow({ item, active, onClick }: { item: WaybillListItem; active: boolean; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick}
      className={`w-full text-left px-3 py-2 border-b border-stroke/60 transition-colors ` +
        `${active ? 'bg-brand-subtle' : 'hover:bg-surface2'}`}>
      <div className="flex items-center gap-2">
        <span className="text-sm text-fg1 font-medium truncate">{item.number ?? 'без номера'}</span>
        {item.issuedOn && <span className="text-xs text-fg3 shrink-0">{formatDate(item.issuedOn)}</span>}
        <div className="flex-1" />
        <span className={`text-xs shrink-0 ${item.state === 'Posted' ? 'text-success' : 'text-fg4'}`}>
          {item.state === 'Posted' ? 'проведена' : 'черновик'}
        </span>
      </div>
      <div className="flex items-center gap-2 mt-0.5">
        <span className={`text-xs truncate ${item.constructionName ? 'text-fg3' : item.constructionId ? 'text-danger' : 'text-fg4'}`}>
          {item.constructionName ?? (item.constructionId ? 'стройка не найдена' : 'стройка не выбрана')}
        </span>
        <div className="flex-1" />
        {/* Ноль не показываем: число «0» у каждой строки читается как шум. */}
        {item.unmatched > 0 && (
          <span className="inline-flex items-center gap-0.5 text-xs text-warning shrink-0"
            title={`Не сопоставлено строк: ${item.unmatched} из ${item.lines}`}>
            <ListChecks size={11} />{item.unmatched}
          </span>
        )}
      </div>
    </button>
  );
}

function matches(item: WaybillListItem, query: string): boolean {
  const text = query.trim().toLowerCase();
  if (!text) return true;
  return [item.number, item.constructionName, item.warehouse]
    .some(value => (value ?? '').toLowerCase().includes(text));
}
