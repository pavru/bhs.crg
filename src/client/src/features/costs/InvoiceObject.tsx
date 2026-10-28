import { useState } from 'react';
import { Grid3x3 } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { useToast } from '@/shared/ui/Toast';
import { NO_ACCESS, hasPermission, useAccess } from '@/shared/api/access';
import type { InvoiceView } from '@/shared/api/invoices';
import { usePreviewAllocation, useReplaceMatrix } from '@/shared/api/allocationMatrix';
import { AllocationMatrix } from './AllocationMatrix';
import { useCorrecting } from './useCorrectionDraft';
import { K } from './invoiceFields';
import { toNumber } from './invoiceLines';
import { headerObject, headerPickMode, matrixSignature, newTarget, targetsOf } from './matrix';
import { InvoicePlaceField } from './InvoicePlaceField';
import { fieldText, statusOf } from './placeChoices';
import { chosen, samePlace, usePlaces, type Place } from './places';

/**
 * «Объект» в шапке счёта (задача F2, issue #1086, ТЗ COST-6.2, COST-12): выбрать объект — значит разнести
 * на него весь счёт. Для большинства счетов разноска на этом и заканчивается; «на несколько объектов»
 * открывает матрицу.
 *
 * <p>Раскладку и здесь считает сервер: выбор объекта — тот же предпросмотр «поровну» на одну цель, в том
 * числе на раздел стройки (COST-12.1). Пишется сразу или открывает матрицу — решает `headerPickMode` (D3):
 * сразу — когда счёт не разнесён или весь лежит на одном месте той же стройки (уточнили раздел, остаток —
 * туда же); несколько мест, другая стройка или статья — матрица с предпросмотром поверх прежней разноски:
 * заменять чужое решение молча нельзя.</p>
 */
export function InvoiceObject({ view, locked }: {
  view: InvoiceView;
  /** Счёт заперт закрытым периодом: разноска только читается. */
  locked: boolean;
}) {
  const { data: access = NO_ACCESS } = useAccess();
  const allocate = hasPermission(access, 'costs.allocation.edit');
  const canEdit = allocate && !locked;
  // Исправление (E2, issue #1100, PR 6b-1): выбор объекта записал бы разноску сразу — его нет; матрица
  // открывается в черновом режиме и отдаёт набор исправлению.
  const correcting = useCorrecting();
  const places = usePlaces();
  const previewing = usePreviewAllocation();
  const replace = useReplaceMatrix();
  const toast = useToast();
  const [matrix, setMatrix] = useState<null | { initial?: InitialPreview }>(null);

  const current = headerObject(view);
  const raw = view.requisites[K.total];
  const total = typeof raw === 'number' ? raw : toNumber(String(raw ?? ''));

  async function pick(place: Place) {
    if (!chosen(place)) return;
    const mode = headerPickMode(current, place);
    if (mode === 'keep') return;
    try {
      const preview = await previewing.mutateAsync({ id: view.id, method: 'equal', targets: [place] });
      // Отметка версии — ТОГО вида, по которому решено «пишем сразу»: сосед успел разнести иначе — запись
      // откажет, а не заменит его разноску молча.
      const stamp = view.allocation.stamp;
      if (mode === 'write') await replace.mutateAsync({ id: view.id, seen: view.version, state: { ...preview.apply, stamp } });
      else {
        // Прежние объекты — колонками рядом с новым: предпросмотр показывает, что именно заменит «Применить».
        const columns = targetsOf(view).filter(t => !samePlace(t, place));
        // Основа — вид ЭТОГО замыкания, по которому предпросмотр спрошен: к открытию матрицы вид мог
        // смениться (сосед поправил количество), и матрица приняла бы за основу то, чего предпросмотр
        // не видел.
        const base = { version: view.version, signature: matrixSignature(view) };
        setMatrix({ initial: { preview, targets: [...columns, newTarget(place)], stamp, base } });
      }
    } catch (e) {
      toast.apiError(e, 'Счёт не разнесён на объект');
    }
  }

  const { status, calm } = statusOf(current);

  return (
    <div className="flex items-center gap-2 flex-wrap text-xs" data-readiness="allocation">
      <span className="text-fg4">Объект</span>
      {canEdit && !correcting ? (
        <InvoicePlaceField label="Объект счёта" places={places} current={current}
          disabled={previewing.isPending || replace.isPending} onPick={place => void pick(place)} />
      ) : (
        <span className="text-on-surface">{current.kind === 'one' ? fieldText(current, places).text : '—'}</span>
      )}
      <span className={calm ? 'text-fg4' : 'text-warning'}>{status}</span>
      {correcting?.allocation && <span className="text-warning">разноска изменена в исправлении — запишется с ним</span>}
      <Button size="sm" variant="text" icon={<Grid3x3 size={13} />} onClick={() => setMatrix({})}>
        {canEdit && !correcting ? 'на несколько объектов' : 'разноска по объектам'}
      </Button>

      {matrix && (
        <AllocationMatrix view={view} total={total} canEdit={correcting ? allocate : canEdit}
          initialPreview={matrix.initial} onClose={() => setMatrix(null)}
          draft={correcting
            ? { lines: correcting.lines, state: correcting.allocation, onApply: correcting.setAllocation }
            : undefined} />
      )}
    </div>
  );
}

type InitialPreview = NonNullable<Parameters<typeof AllocationMatrix>[0]['initialPreview']>;

