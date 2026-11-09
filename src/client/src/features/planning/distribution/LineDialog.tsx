import { useState } from 'react';
import {
  distributionRefusal, useRereadDistribution, useWriteLines, type Distribution, type DistributionLine,
  type DistributionRefusal,
} from '@/shared/api/planningDistribution';
import { Button, IconButton } from '@/shared/ui/Button';
import { Icon } from '@/shared/ui/Icon';
import { Add, Close } from '@/shared/ui/icons';
import { Modal, ModalAction, ModalButtons, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { ModalBanner } from '@/shared/ui/ModalBanner';
import { NumberInput } from '@/shared/ui/NumberInput';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { formatInput, formatQuantity } from '@/shared/format/format';
import {
  bothUnits, conflictText, draftBalance, factorOf, lineWriteOf, quantityOf, toPlanUnits, toRowUnits, type PartDraft,
} from './distributionView';

/**
 * Окно строки (макет П2, вариант 1): части строки парами «раздел — количество», количество — двумя связанными
 * полями, в единице строки и в единице плана (ответ листа 0). Остаток — «стройка в целом», текстом под парами.
 *
 * Отказ 409 — banner окна с «Перечитать», и набранное остаётся: человек перечитывает распределение и повторяет,
 * не набирая заново. Отказ баланса — текстом сервера, он называет обе единицы.
 */
export function LineDialog({ distribution, line, onClose }: {
  distribution: Distribution;
  line: DistributionLine;
  onClose: () => void;
}) {
  const write = useWriteLines(distribution.revisionId);
  const reread = useRereadDistribution(distribution.revisionId);
  const toast = useSnackbar();
  const factor = factorOf(line);
  const [unit, setUnit] = useState<'row' | 'plan'>('row');
  const [drafts, setDrafts] = useState<PartDraft[]>(() => line.parts.length > 0
    ? line.parts.map(p => ({ sectionId: p.sectionId, row: formatInput(p.quantity.row), plan: formatInput(p.quantity.plan) }))
    : [{ sectionId: '', row: '', plan: '' }]);
  const [refusal, setRefusal] = useState<DistributionRefusal | null>(null);
  const [rereading, setRereading] = useState(false);
  const sections = distribution.targets.sections.filter(s => !s.deleted);
  const balance = draftBalance(drafts, line, unit);
  const volume = line.volume;

  const change = (i: number, patch: Partial<PartDraft>) => setDrafts(prev => prev.map((d, j) => (j === i ? { ...d, ...patch } : d)));
  const typeRow = (i: number, text: string) => {
    setUnit('row');
    const q = quantityOf(text);
    change(i, { row: text, plan: factor !== null && q !== null && !Number.isNaN(q) ? formatInput(toPlanUnits(q, factor)) : '' });
  };
  const typePlan = (i: number, text: string) => {
    setUnit('plan');
    const q = quantityOf(text);
    change(i, { plan: text, row: factor !== null && q !== null && !Number.isNaN(q) ? formatInput(toRowUnits(q, factor)) : '' });
  };

  const send = (writes: ReturnType<typeof lineWriteOf>, done: string) => {
    setRefusal(null);
    write.mutate({ body: [writes], version: distribution.version }, {
      onSuccess: () => { toast.success(done); onClose(); },
      onError: e => setRefusal(distributionRefusal(e)),
    });
  };
  const submit = () => {
    const w = lineWriteOf(line.rowId, drafts, unit);
    send(w, w.parts === null ? `Строка ${line.number} — по правилу раздела` : `Строка ${line.number} распределена`);
  };
  const toRule = () => send({ rowId: line.rowId, unit: 'row', parts: null }, `Строка ${line.number} — по правилу раздела`);

  const unitName = unit === 'plan' ? line.units.plan : line.units.row;
  const volumeIn = unit === 'plan' ? volume?.plan : volume?.row;
  const used = new Set(drafts.map(d => d.sectionId));
  const twice = drafts.some((d, i) => d.sectionId !== '' && drafts.findIndex(x => x.sectionId === d.sectionId) !== i);
  const ready = !balance.over && !balance.invalid && !twice && drafts.some(d => d.sectionId !== '');

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title={`Строка ${line.number}: распределение`} busy={write.isPending}>
      <div className="space-y-4">
        {refusal?.kind === 'conflict' && (
          <ModalBanner tone="warning" actions={[{
            label: 'Перечитать', loading: rereading,
            onClick: () => { setRereading(true); void reread().finally(() => { setRereading(false); setRefusal(null); }); },
          }]}>
            {conflictText(refusal)}
          </ModalBanner>
        )}
        {refusal && refusal.kind !== 'conflict' && <ModalBanner tone="error">{refusal.text}</ModalBanner>}

        <div>
          <p className="text-body-medium text-on-surface">{line.name}</p>
          {volume && <p className="text-body-small text-on-surface-variant tabular-nums">Объём: {bothUnits(volume, line.units)}</p>}
        </div>

        <div className="space-y-2">
          {drafts.map((d, i) => (
            <div key={i} className="flex items-center gap-2">
              <Select density="dense" className="flex-1 min-w-0" value={d.sectionId} aria-label={`Раздел стройки, часть ${i + 1}`}
                placeholder="Раздел стройки" onValueChange={v => change(i, { sectionId: v })}>
                {sections.map(s => (
                  <SelectItem key={s.sectionId} value={s.sectionId} disabled={s.sectionId !== d.sectionId && used.has(s.sectionId)}>
                    {s.name}
                  </SelectItem>
                ))}
              </Select>
              <NumberInput density="dense" className="w-28" label="В ед. строки" unit={line.units.row}
                value={d.row} onChange={t => typeRow(i, t)} />
              {factor !== null && (
                <NumberInput density="dense" className="w-28" label="В ед. плана"
                  unit={line.units.plan} value={d.plan} onChange={t => typePlan(i, t)} />
              )}
              <IconButton icon={Close} label={`Убрать часть ${i + 1}`} disabled={drafts.length === 1}
                onClick={() => setDrafts(prev => prev.filter((_, j) => j !== i))} />
            </div>
          ))}
          {drafts.length < sections.length && (
            <Button variant="text" icon={<Icon icon={Add} size={18} aria-hidden />}
              onClick={() => setDrafts(prev => [...prev, { sectionId: '', row: '', plan: '' }])}>
              Добавить раздел
            </Button>
          )}
        </div>

        {volumeIn != null && (
          <p className={`text-body-medium tabular-nums ${balance.over ? 'text-error' : 'text-on-surface-variant'}`} role={balance.over ? 'alert' : undefined}>
            {balance.over
              ? `${overText(balance.sum, unit, line, factor)} — больше объёма строки`
              : `${formatQuantity(balance.sum)} из ${formatQuantity(volumeIn)}${unitName ? ` · ${unitName}` : ''} · стройка в целом: ${formatQuantity(balance.rest)}`}
          </p>
        )}
        {balance.invalid && <p role="alert" className="text-body-medium text-error">Количество — число больше нуля.</p>}
        {twice && <p role="alert" className="text-body-medium text-error">Раздел назван дважды — у строки одна часть на раздел.</p>}

        <ModalButtons>
          {line.source === 'parts' && (
            <ModalAction lead variant="text" disabled={write.isPending} onClick={toRule}>Вернуть к правилу раздела</ModalAction>
          )}
          <ModalExit />
          <ModalPrimary disabled={!ready} loading={write.isPending} onClick={submit}>Записать</ModalPrimary>
        </ModalButtons>
      </div>
    </Modal>
  );
}

/** Превышение в обеих единицах: «13 · 100 м = 1 300 м при 1 200 м» (§77 F.1). */
function overText(sum: number, unit: 'row' | 'plan', line: DistributionLine, factor: number | null): string {
  const v = line.volume!;
  const asRow = unit === 'row' ? sum : factor ? toRowUnits(sum, factor) : sum;
  const asPlan = unit === 'plan' ? sum : factor ? toPlanUnits(sum, factor) : null;
  const rowText = `${formatQuantity(asRow)}${line.units.row ? ` · ${line.units.row}` : ''}`;
  if (asPlan === null || v.plan === null || !line.units.plan) return `${rowText} при ${formatQuantity(v.row)}`;
  return `${rowText} = ${formatQuantity(asPlan)} ${line.units.plan} при ${formatQuantity(v.plan)} ${line.units.plan}`;
}
