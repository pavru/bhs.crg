import { useState } from 'react';
import { Grid3x3 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { useToast } from '@/shared/ui/Toast';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import type { InvoiceView } from '@/shared/api/invoices';
import { usePreviewAllocation, useReplaceMatrix, type AllocationPreview } from '@/shared/api/allocationMatrix';
import { AllocationMatrix } from './AllocationMatrix';
import { K } from './invoiceFields';
import { toNumber } from './invoiceLines';
import { headerObject, newTarget, targetName, targetsOf, type MatrixTarget } from './matrix';
import { PlaceSelect } from './PlaceSelect';
import { NO_PLACE, chosen, samePlace, usePlaces, type Place } from './places';

/**
 * «Объект» в шапке счёта (задача F2, issue #1086, ТЗ COST-6.2, COST-12): выбрать объект — значит разнести
 * на него весь счёт. Для большинства счетов разноска на этом и заканчивается; «на несколько объектов»
 * открывает матрицу.
 *
 * <p>Раскладку и здесь считает сервер: выбор объекта — тот же предпросмотр «поровну» на одну цель. Счёт
 * ещё не разнесён — записывается сразу (показывать нечего: всё на один объект). Разнесён иначе — выбор
 * открывает матрицу с предпросмотром поверх прежней разноски: заменять чужое решение молча нельзя.</p>
 */
export function InvoiceObject({ view, locked }: {
  view: InvoiceView;
  /** Счёт заперт закрытым периодом: разноска только читается. */
  locked: boolean;
}) {
  const { data: access = NO_ACCESS } = useAccess();
  const canEdit = hasPermission(access, 'costs.allocation.edit') && !locked;
  const places = usePlaces();
  const previewing = usePreviewAllocation();
  const replace = useReplaceMatrix();
  const toast = useToast();
  const [matrix, setMatrix] = useState<null | { initial?: { preview: AllocationPreview; targets: MatrixTarget[]; stamp: string } }>(null);

  const current = headerObject(view);
  const raw = view.requisites[K.total];
  const total = typeof raw === 'number' ? raw : toNumber(String(raw ?? ''));

  async function pick(place: Place) {
    if (!chosen(place)) return;
    try {
      const preview = await previewing.mutateAsync({ id: view.id, method: 'equal', targets: [place] });
      // Отметка версии — ТОГО вида, по которому решено «счёт не разнесён»: сосед успел разнести — запись
      // откажет, а не заменит его разноску молча.
      const stamp = view.allocation.stamp;
      if (current.kind === 'none') await replace.mutateAsync({ id: view.id, seen: view.version, state: { ...preview.apply, stamp } });
      else {
        // Прежние объекты — колонками рядом с новым: предпросмотр показывает, что именно заменит «Применить».
        const columns = targetsOf(view).filter(t => !samePlace(t, place));
        setMatrix({ initial: { preview, targets: [...columns, newTarget(place)], stamp } });
      }
    } catch (e) {
      toast.apiError(e, 'Счёт не разнесён на объект');
    }
  }

  const status = current.kind === 'none' ? 'не разнесён'
    : current.kind === 'many' ? `разнесён на ${current.count} ${plural(current.count)}`
      : current.complete ? 'весь счёт на этот объект' : 'разнесено не полностью';

  return (
    <div className="flex items-center gap-2 flex-wrap text-xs">
      <span className="text-fg4">Объект</span>
      {canEdit ? (
        <PlaceSelect label="Объект счёта" places={places} disabled={previewing.isPending || replace.isPending}
          value={current.kind === 'one' ? current.place : NO_PLACE}
          placeholder={current.kind === 'many' ? '— несколько объектов —' : '— выберите —'}
          onChange={place => void pick(place)}
          className="min-w-56 rounded border border-stroke bg-surface px-1.5 py-1 text-xs text-fg outline-none
            focus:border-primary" />
      ) : (
        <span className="text-fg">
          {current.kind === 'one' ? targetName(current.place, places) : '—'}
        </span>
      )}
      <span className={current.kind === 'one' && current.complete ? 'text-fg4' : 'text-warning'}>{status}</span>
      <Button size="sm" variant="text" icon={<Grid3x3 size={13} />} onClick={() => setMatrix({})}>
        {canEdit ? 'на несколько объектов' : 'разноска по объектам'}
      </Button>

      {matrix && (
        <AllocationMatrix view={view} total={total} canEdit={canEdit} initialPreview={matrix.initial}
          onClose={() => setMatrix(null)} />
      )}
    </div>
  );
}

function plural(count: number): string {
  const tail = count % 100;
  if (tail >= 11 && tail <= 14) return 'объектов';
  if (count % 10 === 1) return 'объект';
  if (count % 10 >= 2 && count % 10 <= 4) return 'объекта';
  return 'объектов';
}
