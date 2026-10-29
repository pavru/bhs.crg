import { useState } from 'react';
import { useCreateWorkType, useUnitChoices, type WorkTypeChoice } from '@/shared/api/planningMatches';
import { Button } from '@/shared/ui/Button';
import { ModalExit } from '@/shared/ui/Modal';
import { Select, SelectItem } from '@/shared/ui/Select';
import { TextField } from '@/shared/ui/TextField';
import { useToast } from '@/shared/ui/Toast';
import { WindowStep } from '@/shared/ui/WindowStep';
import { apiError } from '@/shared/utils/apiError';
import { unitWord } from './matchRows';

/**
 * Шаг «Новый вид работы» окна сопоставления (ТЗ PLAN-6.1, CORE-8; дверь классификатора WORK-59). Виден только при
 * `core.worktypes.edit` — его проверяет тот, кто шаг открывает: невыполнимое действие скрывается.
 *
 * Спрашивает обязательное по схеме вида работы — код, наименование, единицу; признаки «требует места» и прочие
 * дополняют в классификаторе. Единица предлагается по единице расценки («100 шт» → «шт»), если такая есть.
 * Отказ сервера — в этом же шаге, а не тостом: набранное цело, причина рядом с ним.
 */
export function NewWorkTypeStep({ sourceUnit, onBack, onCreated }: {
  sourceUnit: string | null;
  onBack: () => void;
  onCreated: (made: WorkTypeChoice) => void;
}) {
  const units = useUnitChoices(true);
  const create = useCreateWorkType();
  const toast = useToast();
  const [code, setCode] = useState('');
  const [name, setName] = useState('');
  const [picked, setPicked] = useState<string | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);

  const guessed = units.data?.find(u => u.name === unitWord(sourceUnit))?.id ?? '';
  const unitId = picked ?? guessed;
  const unit = units.data?.find(u => u.id === unitId) ?? null;
  const missing = !code.trim() ? 'Назовите код вида работы.' : !name.trim() ? 'Назовите наименование.'
    : !unit ? 'Выберите единицу измерения.' : null;

  const submit = () => {
    if (missing || !unit) return;
    setRefusal(null);
    create.mutate({ code: code.trim(), name: name.trim(), unit }, {
      onSuccess: made => {
        toast.success(`Вид работы заведён: ${made.name}. Остальные признаки дополните в классификаторе.`);
        onCreated(made);
      },
      onError: e => setRefusal(apiError(e, 'Вид работы не заведён')),
    });
  };

  return (
    <WindowStep title="Новый вид работы" onBack={onBack} busy={create.isPending}
      footer={(
        <div className="flex flex-wrap items-center justify-end gap-2">
          {missing && <span className="mr-auto text-xs text-on-surface-variant">{missing}</span>}
          <ModalExit>Назад</ModalExit>
          <Button variant="filled" disabled={missing !== null} loading={create.isPending} onClick={submit}>Завести и выбрать</Button>
        </div>
      )}>
      <div className="space-y-3">
        <p className="text-sm text-on-surface-variant">Вид работы появится в классификаторе для всех смет и учёта работ.</p>
        <div className="grid gap-3 sm:grid-cols-[minmax(0,1fr)_minmax(0,2fr)]">
          <TextField label="Код" required value={code} onChange={e => setCode(e.target.value)} />
          <TextField label="Наименование" required value={name} onChange={e => setName(e.target.value)} />
        </div>
        {units.isError ? (
          <p role="alert" className="text-sm text-error">{apiError(units.error, 'Единицы измерения не прочитались')}</p>
        ) : (
          <Select label="Единица измерения" required value={unitId} onValueChange={setPicked} disabled={!units.data}
            hint={sourceUnit ? `Единица расценки — «${sourceUnit}»; коэффициент пересчёта предложит окно.` : undefined}
            placeholder={units.data ? (units.data.length ? 'Выберите единицу' : 'Справочник единиц пуст') : 'Читаем единицы…'}>
            {units.data?.map(u => <SelectItem key={u.id} value={u.id}>{u.name}</SelectItem>)}
          </Select>
        )}
        {refusal && <p role="alert" className="text-sm text-error">{refusal}</p>}
      </div>
    </WindowStep>
  );
}
