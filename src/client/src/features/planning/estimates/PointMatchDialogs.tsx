import { useId, useRef, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Search } from '@/shared/ui/icons';
import { planRefusal, type PlanRefusal } from '@/shared/api/planningEstimates';
import {
  usePlanNomenclature, usePointRateMatch, usePointResourceMatch, type RateMatch, type ResourceMatch,
} from '@/shared/api/planningMatches';
import { Button } from '@/shared/ui/Button';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { useToast } from '@/shared/ui/Toast';
import { apiError } from '@/shared/utils/apiError';
import { PickerOption } from '@/features/costs/PickerRows';
import { foundRows } from '@/features/costs/pickerCandidates';
import { usePickerSelection } from '@/features/costs/pickerSelection';
import { RateChoiceFields } from './RateChoiceFields';
import { useRateChoice } from './useRateChoice';

/**
 * «Перенацелить» запомненное (ТЗ PLAN-6.1, D8 зонтика): один шифр в разных сметах может значить разные работы, и
 * неверно запомненное исправляют здесь. Ключ — слово файла — не правится. Отказ 409 — словами сервера над
 * кнопкой, окно остаётся открытым с набранным: список перечитан, повтор идёт по новой версии.
 */
export function PointRateDialog({ item, onClose }: { item: RateMatch; onClose: () => void }) {
  const point = usePointRateMatch();
  const toast = useToast();
  const state = useRateChoice(item.sourceUnit, { workTypeId: item.workTypeId, factor: item.factor });
  const [refusal, setRefusal] = useState<PlanRefusal | null>(null);
  // Версия — из перечитанного списка: строку окна держит вызывающий, и после отказа она уже новая.
  const submit = () => {
    if (!state.choice) return;
    setRefusal(null);
    point.mutate({ item, choice: state.choice }, {
      onSuccess: done => { toast.success(`Шифр «${done.rateCode}» → «${done.workTypeName ?? 'вид работы'}»`); onClose(); },
      onError: e => setRefusal(planRefusal(e)),
    });
  };
  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title={`Перенацелить шифр «${item.rateCode}»`} wide busy={point.isPending}>
      <div className="space-y-4">
        <p className="text-sm text-on-surface-variant">
          Следующие импорты подставят новый выбор. Строки, уже сопоставленные прежним, остаются как есть.
        </p>
        <RateChoiceFields state={state} />
        {refusal && <p role="alert" className="text-sm text-error">{refusal.text}</p>}
        <div className="flex flex-wrap items-center justify-end gap-2 border-t border-outline-variant pt-3">
          {state.missing && <span className="mr-auto text-xs text-on-surface-variant">{state.missing}</span>}
          <ModalExit />
          <Button variant="filled" disabled={!state.choice} loading={point.isPending} onClick={submit}>Перенацелить</Button>
        </div>
      </div>
    </Modal>
  );
}

/** Перенацелить запомненный ресурс: поиск позиции номенклатуры — тем же поиском ядра, что окно материала. */
export function PointResourceDialog({ item, onClose }: { item: ResourceMatch; onClose: () => void }) {
  const point = usePointResourceMatch();
  const toast = useToast();
  const [typed, setTyped] = useState(item.sourceText);
  const [query, setQuery] = useState(item.sourceText.trim());
  const timer = useRef<number | null>(null);
  const [refusal, setRefusal] = useState<PlanRefusal | null>(null);
  const found = usePlanNomenclature(query);
  const rows = found.data ? foundRows(found.data.items, []).filter(r => r.id !== item.nomenclatureId) : [];
  const selection = usePickerSelection(rows.map(r => r.id));
  const listId = useId();

  function type(next: string) {
    setTyped(next);
    if (timer.current !== null) window.clearTimeout(timer.current);
    timer.current = window.setTimeout(() => setQuery(next.trim()), 250);
  }
  const choose = (id: string) => {
    setRefusal(null);
    point.mutate({ item, nomenclatureId: id }, {
      onSuccess: done => { toast.success(`«${done.sourceText}» → «${done.nomenclatureName ?? 'позиция без названия'}»`); onClose(); },
      onError: e => setRefusal(planRefusal(e)),
    });
  };

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Перенацелить материал" wide busy={point.isPending}>
      <div className="space-y-3">
        <div className="rounded-lg bg-surface-container px-3 py-2 space-y-0.5">
          <p className="text-caption text-on-surface-variant">{item.kind === 'code' ? 'Код ресурса' : 'Наименование'}</p>
          <p className="text-sm text-on-surface">{item.sourceText}</p>
          <p className="text-xs text-on-surface-variant">Сейчас: {item.nomenclatureName ?? 'позиция без названия'}</p>
        </div>
        <div className="flex items-center gap-2 rounded-md border border-outline px-2.5 h-10">
          <Icon icon={Search} size={18} className="text-on-surface-variant shrink-0" />
          <input value={typed} onChange={e => type(e.target.value)} aria-label="Поиск позиции"
            placeholder="наименование или часть наименования" className="flex-1 bg-transparent text-sm outline-none text-on-surface placeholder:text-on-surface-variant" />
        </div>
        {found.isError && <p role="alert" className="text-xs text-error">Справочник не прочитан: {apiError(found.error, 'сервер отказал')}. Это не «позиций нет».</p>}
        <div id={listId} role="listbox" aria-label="Найденные позиции" onMouseLeave={selection.leave} className="-mx-2 space-y-0.5">
          {rows.map(row => (
            <PickerOption key={row.id} row={row} id={`${listId}-${row.id}`} active={row.id === selection.active} enter={false}
              narrow={false} kinds={false} onChoose={r => choose(r.id)} onHover={selection.hover} />
          ))}
        </div>
        {found.data && rows.length === 0 && <p className="text-xs text-on-surface-variant">По этому запросу других позиций нет.</p>}
        {found.data?.more && <p className="text-xs text-warning">Показаны первые {rows.length} — подходящих больше. Уточните запрос.</p>}
        {refusal && <p role="alert" className="text-sm text-error">{refusal.text}</p>}
        <div className="flex justify-end border-t border-outline-variant pt-3"><ModalExit /></div>
      </div>
    </Modal>
  );
}
