import { useEffect, useState } from 'react';
import { Search, Trash2 } from 'lucide-react';
import { ArchivedMark } from '@/shared/ui/ArchivedMark';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { Modal } from '@/shared/ui/Modal';
import { useToast } from '@/shared/ui/Toast';
import { formatDate } from '@/shared/format/format';
import { apiError } from '@/shared/utils/apiError';
import {
  useForgetSupplierMatch, useForgetSupplierMatches, usePointSupplierMatch, useSupplierMatches,
  useSupplierMatchSuppliers,
  type SupplierMatchFilter, type SupplierMatchItem,
} from '@/shared/api/supplierMatches';
import { NomenclaturePicker } from './NomenclaturePicker';
import { forgetAllQuestion, forgetQuestion, issueNote, shownOf, supplierLabel } from './supplierMatchList';

/** Сколько ждать после последней буквы, прежде чем искать: запрос на каждое нажатие — это десяток запросов на слово. */
const TYPING_PAUSE = 300;

/**
 * Список соответствий наименований поставщика (задача C3, issue #1079, ТЗ COST-7.1).
 *
 * <p><b>Диалог, а не страница:</b> его открывают из открытого счёта — исправить правило и вернуться к
 * строке, — а страница упёрлась бы в сторож ухода с несохранёнными строками.</p>
 *
 * <p><b>Соответствие нельзя завести здесь руками и нельзя переписать его ключ.</b> Ключ — слова бумаги:
 * набранный по памяти, он не совпадёт с ней и не сработает никогда. Неверное забывают; верное
 * запомнится само первым же выбором позиции в строке счёта.</p>
 *
 * <p>⚠️ Соответствие держит поставщика и позицию от удаления из справочника — и этот список есть то,
 * чем держателя снимают: «Забыть» либо смена позиции.</p>
 */
export function SupplierMatchesDialog({ initial, onClose }: {
  /** С чем открыть: из пометки строки — её поставщик и её ключ, чтобы нужная запись была первой. */
  initial?: { supplierId: string | null; query: string };
  onClose: () => void;
}) {
  const [filter, setFilter] = useState<SupplierMatchFilter>({
    supplierId: initial?.supplierId ?? null, query: initial?.query ?? '', issue: null,
  });
  // Набранное — отдельно от того, по чему ищем: ищем, когда человек остановился.
  const [typed, setTyped] = useState(filter.query);
  useEffect(() => {
    const timer = setTimeout(() => setFilter(f => (f.query === typed ? f : { ...f, query: typed })), TYPING_PAUSE);
    return () => clearTimeout(timer);
  }, [typed]);

  const list = useSupplierMatches(filter);
  const suppliers = useSupplierMatchSuppliers();
  const point = usePointSupplierMatch();
  const forget = useForgetSupplierMatch();
  const forgetAll = useForgetSupplierMatches();
  const [forgettingAll, setForgettingAll] = useState(false);
  const toast = useToast();
  // Идентификатор, а не снимок строки: версию для «Забыть» берём у строки, КАК ОНА ЛЕЖИТ В СПИСКЕ
  // сейчас, — снимок после смены позиции нёс бы прежнюю и получил бы отказ на собственную правку.
  const [forgettingId, setForgettingId] = useState<string | null>(null);

  const narrow = (patch: Partial<SupplierMatchFilter>) => setFilter(f => ({ ...f, ...patch }));
  const reset = () => { setTyped(''); setFilter({ supplierId: null, query: '', issue: null }); };
  const filtered = filter.supplierId !== null || filter.query.trim() !== '' || filter.issue !== null;
  const pages = list.data?.pages;
  const data = pages && { ...pages[pages.length - 1], items: pages.flatMap(page => page.items) };
  const forgetting = data?.items.find(item => item.id === forgettingId) ?? null;
  // Отобранный поставщик, которого среди пунктов нет (список не пришёл либо у него забыли последнее
  // соответствие), всё равно назван: иначе поле показывало бы «Все поставщики» при действующем отборе.
  const chosen = suppliers.data?.find(s => s.id === filter.supplierId) ?? null;
  const chosenMissing = filter.supplierId !== null && chosen === null;

  async function repoint(item: SupplierMatchItem, nomenclatureId: string) {
    // Пока прежняя правка не дочитала список, у строки на экране старая версия — вторая ушла бы с ней.
    if (nomenclatureId === item.nomenclatureId || point.isPending) return;
    try {
      await point.mutateAsync({ item, nomenclatureId });
      toast.success('Соответствие изменено. В уже сохранённых счетах позиция не меняется.');
    } catch (e) { toast.apiError(e, 'Соответствие не изменено'); }
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Соответствия наименований" extraWide>
      <p className="text-xs text-fg3 mb-3">
        Что система запомнила: какую позицию номенклатуры вы выбирали строке поставщика. Следующий счёт
        того же поставщика получает эту позицию сам. Появляются соответствия сами — при сохранении строк счёта.
      </p>

      <div className="flex flex-wrap items-center gap-2 mb-3">
        <select value={filter.supplierId ?? ''} aria-label="Поставщик"
          onChange={e => narrow({ supplierId: e.target.value || null })}
          className="rounded border border-stroke bg-surface px-2 py-1 text-sm text-fg max-w-[18rem]">
          <option value="">Все поставщики</option>
          {chosenMissing && (
            <option value={filter.supplierId!}>
              {suppliers.isSuccess ? 'Выбранный поставщик (соответствий нет)' : 'Выбранный поставщик'}
            </option>
          )}
          {suppliers.data?.map(s => (
            <option key={s.id} value={s.id}>{supplierLabel(s)} ({s.count})</option>
          ))}
        </select>
        <label className="flex flex-1 min-w-[14rem] items-center gap-1 rounded border border-stroke bg-surface px-2 py-1">
          <Search size={14} className="text-fg4" />
          <input value={typed} placeholder="Артикул или наименование у поставщика"
            aria-label="Поиск по артикулу или наименованию у поставщика"
            onChange={e => setTyped(e.target.value)}
            className="flex-1 bg-transparent text-sm text-fg outline-none" />
        </label>
        {/* Нули не рисуются: чип без записей — дверь в пустой список. Нажатый остаётся, чтобы его снять. */}
        {((data?.counts.lost ?? 0) > 0 || filter.issue === 'lost') && (
          <IssueChip active={filter.issue === 'lost'} danger
            onClick={() => narrow({ issue: filter.issue === 'lost' ? null : 'lost' })}>
            Позиция удалена{data ? ` ${data.counts.lost}` : ''}
          </IssueChip>
        )}
        {((data?.counts.archived ?? 0) > 0 || filter.issue === 'archived') && (
          <IssueChip active={filter.issue === 'archived'}
            onClick={() => narrow({ issue: filter.issue === 'archived' ? null : 'archived' })}>
            Позиция в архиве{data ? ` ${data.counts.archived}` : ''}
          </IssueChip>
        )}
      </div>

      {list.isError ? (
        <div className="rounded-lg border border-danger-border px-3 py-2 text-sm text-danger">
          Список соответствий не прочитан: {apiError(list.error)} Это не «соответствий нет».{' '}
          <button type="button" className="underline" onClick={() => void list.refetch()}>Повторить</button>
        </div>
      ) : !data ? (
        <p className="py-6 text-center text-sm text-fg4">Читаем соответствия…</p>
      ) : data.total === 0 ? (
        filtered ? (
          <p className="py-6 text-center text-sm text-fg3">
            По этому отбору соответствий нет.{' '}
            <button type="button" className="text-brand hover:underline" onClick={reset}>
              Сбросить отбор
            </button>
          </p>
        ) : (
          <p className="py-6 text-center text-sm text-fg3">
            Соответствий пока нет. Они появляются сами: выберите позицию строке счёта и сохраните строки —
            выбор запомнится за поставщиком.
          </p>
        )
      ) : (
        <>
          <div className="max-h-[55vh] overflow-auto">
            <table className="w-full text-xs">
              <thead className="sticky top-0 bg-surface text-fg4">
                <tr className="text-left">
                  {filter.supplierId === null && <th className="py-1 pr-2 font-normal">Поставщик</th>}
                  <th className="py-1 pr-2 font-normal">У поставщика</th>
                  <th className="py-1 pr-2 font-normal w-[38%]">Позиция номенклатуры</th>
                  <th className="py-1 pr-2 font-normal">Изменено</th>
                  <th className="w-8" />
                </tr>
              </thead>
              <tbody>
                {data.items.map(item => (
                  <tr key={item.id} className="border-t border-stroke align-top">
                    {filter.supplierId === null && (
                      <td className="py-1.5 pr-2 text-fg2">
                        {supplierLabel({ name: item.supplierName, lost: item.supplierLost })}
                        {item.supplierArchived && <ArchivedMark className="ml-1" />}
                      </td>
                    )}
                    <td className="py-1.5 pr-2 text-fg" title={item.source}>
                      <span className="text-fg4">{item.by === 'code' ? 'артикул ' : ''}</span>
                      {item.source}
                    </td>
                    <td className="py-1.5 pr-2">
                      {/* Тот же выбор позиции, что в строке счёта: смена сохраняется сразу. Снять позицию
                          здесь нельзя — соответствие без позиции есть забытое соответствие. */}
                      <NomenclaturePicker chosen name={item.nomenclatureName} clearable={false}
                        lost={item.issue === 'lost'} archived={item.issue === 'archived'}
                        from={item.by === 'code' ? { code: item.source } : { name: item.source }}
                        onPick={id => void repoint(item, id)} onClear={() => {}} />
                      {issueNote(item.issue) && (
                        <p className={`mt-0.5 ${item.issue === 'lost' ? 'text-danger' : 'text-fg4'}`}>
                          {issueNote(item.issue)}
                        </p>
                      )}
                    </td>
                    <td className="py-1.5 pr-2 text-fg3 whitespace-nowrap">
                      {formatDate(item.updatedAt)}{item.updatedBy ? ` · ${item.updatedBy}` : ''}
                    </td>
                    <td className="py-1.5">
                      <button type="button" title="Забыть соответствие" disabled={point.isPending} onClick={() => setForgettingId(item.id)}
                        className="text-fg3 hover:text-danger p-1"><Trash2 size={14} /></button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="mt-2 flex items-center gap-3 text-xs text-fg3">
            <span>{shownOf(data.items.length, data.total)}</span>
            {list.hasNextPage && (
              <Button size="sm" variant="text" loading={list.isFetchingNextPage}
                onClick={() => void list.fetchNextPage()}>
                Показать ещё
              </Button>
            )}
            {/* Разом — только у одного поставщика: его соответствия держат его запись от удаления, и
                забывать сотню по одному значило бы не забыть никогда. */}
            {chosen && (
              <button type="button" className="ml-auto text-danger hover:underline" onClick={() => setForgettingAll(true)}>
                Забыть все соответствия поставщика ({chosen.count})
              </button>
            )}
          </div>
        </>
      )}

      <ConfirmDialog open={forgettingId !== null} onOpenChange={o => { if (!o) setForgettingId(null); }}
        title="Забыть соответствие?" description={forgetting ? forgetQuestion(forgetting) : undefined}
        confirmLabel="Забыть" errorTitle="Соответствие не забыто"
        onConfirm={() => (forgetting
          ? forget.mutateAsync(forgetting)
          : Promise.reject(new Error('Этого соответствия в списке уже нет — его забыли или отбор изменился.')))} />

      <ConfirmDialog open={forgettingAll} onOpenChange={o => { if (!o) setForgettingAll(false); }}
        title="Забыть все соответствия поставщика?" description={chosen ? forgetAllQuestion(chosen) : undefined}
        requireCheckbox="Понимаю, что запомненное придётся выбирать заново"
        confirmLabel="Забыть все" errorTitle="Соответствия не забыты"
        onConfirm={async () => {
          const done = await forgetAll.mutateAsync(filter.supplierId!);
          toast.success(`Забыто соответствий: ${done.forgotten}.`);
        }} />
    </Modal>
  );
}

function IssueChip({ active, danger = false, onClick, children }: {
  active: boolean; danger?: boolean; onClick: () => void; children: React.ReactNode;
}) {
  return (
    <button type="button" onClick={onClick} aria-pressed={active}
      className={`rounded-full border px-2 py-0.5 text-xs
        ${active ? 'border-brand bg-surface2 text-fg' : danger ? 'border-danger-border text-danger' : 'border-stroke text-fg3'}`}>
      {children}
    </button>
  );
}
