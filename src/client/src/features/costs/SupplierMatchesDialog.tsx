import { useState } from 'react';
import { Search, Trash2 } from 'lucide-react';
import { ArchivedMark } from '@/shared/ui/ArchivedMark';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { Modal } from '@/shared/ui/Modal';
import { useToast } from '@/shared/ui/Toast';
import { formatDate } from '@/shared/format/format';
import { apiError } from '@/shared/utils/apiError';
import {
  useForgetSupplierMatch, usePointSupplierMatch, useSupplierMatches,
  type SupplierMatchFilter, type SupplierMatchItem,
} from '@/shared/api/supplierMatches';
import { NomenclaturePicker } from './NomenclaturePicker';
import { forgetQuestion, issueNote, shownOf, supplierLabel } from './supplierMatchList';

const PAGE = 50;

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
    supplierId: initial?.supplierId ?? null, query: initial?.query ?? '', issue: null, take: PAGE,
  });
  const list = useSupplierMatches(filter);
  const point = usePointSupplierMatch();
  const forget = useForgetSupplierMatch();
  const toast = useToast();
  const [forgetting, setForgetting] = useState<SupplierMatchItem | null>(null);

  /** Любая смена отбора возвращает к первой порции: «ещё 50» относилось к прежнему отбору. */
  const narrow = (patch: Partial<SupplierMatchFilter>) => setFilter(f => ({ ...f, ...patch, take: PAGE }));
  const filtered = filter.supplierId !== null || filter.query.trim() !== '' || filter.issue !== null;
  const data = list.data;

  async function repoint(item: SupplierMatchItem, nomenclatureId: string) {
    if (nomenclatureId === item.nomenclatureId) return;
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
          {data?.suppliers.map(s => (
            <option key={s.id} value={s.id}>{supplierLabel(s)} ({s.count})</option>
          ))}
        </select>
        <label className="flex flex-1 min-w-[14rem] items-center gap-1 rounded border border-stroke bg-surface px-2 py-1">
          <Search size={14} className="text-fg4" />
          <input value={filter.query} placeholder="Артикул или наименование у поставщика"
            aria-label="Поиск по артикулу или наименованию у поставщика"
            onChange={e => narrow({ query: e.target.value })}
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
            <button type="button" className="text-brand hover:underline"
              onClick={() => setFilter({ supplierId: null, query: '', issue: null, take: PAGE })}>
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
                      <button type="button" title="Забыть соответствие" onClick={() => setForgetting(item)}
                        className="text-fg3 hover:text-danger p-1"><Trash2 size={14} /></button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
          <div className="mt-2 flex items-center gap-3 text-xs text-fg3">
            <span>{shownOf(data.items.length, data.total)}</span>
            {data.items.length < data.total && (
              <Button size="sm" variant="text" loading={list.isFetching}
                onClick={() => setFilter(f => ({ ...f, take: f.take + PAGE }))}>
                Показать ещё
              </Button>
            )}
          </div>
        </>
      )}

      <ConfirmDialog open={forgetting !== null} onOpenChange={o => { if (!o) setForgetting(null); }}
        title="Забыть соответствие?" description={forgetting ? forgetQuestion(forgetting) : undefined}
        confirmLabel="Забыть" errorTitle="Соответствие не забыто"
        onConfirm={() => forget.mutateAsync(forgetting!)} />
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
