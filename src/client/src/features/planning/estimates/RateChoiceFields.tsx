import { Icon } from '@/shared/ui/Icon';
import { ArrowForward } from '@/shared/ui/icons';
import { NumberInput } from '@/shared/ui/NumberInput';
import { Select, SelectItem } from '@/shared/ui/Select';
import { apiError } from '@/shared/utils/apiError';
import { formatInput, formatQuantity } from '@/shared/format/format';
import type { RateChoiceState } from './useRateChoice';

/** Строка-образец для примера пересчёта под полем: «9.4: 2,16 × 100 = 216 шт». */
export interface FactorSample {
  label: string;
  volume: number | null;
}

/** Поля выбора: вид работы, обе единицы и коэффициент, пример на строке — состояние держит {@link useRateChoice}. */
export function RateChoiceFields({ state, sample }: { state: RateChoiceState; sample?: FactorSample | null }) {
  const { types, chosen, factor, suggested, sourceUnit } = state;
  const unit = chosen?.unitName ?? '…';
  const result = sample?.volume != null && factor !== null ? sample.volume * factor : null;
  return (
    <div className="grid gap-3 sm:grid-cols-2">
      {types.isError ? (
        <p role="alert" className="text-body-medium text-error">{apiError(types.error, 'Классификатор видов работ не прочитался')}</p>
      ) : (
        <Select label="Вид работы" value={state.workTypeId} onValueChange={state.pick} disabled={!types.data}
          placeholder={types.data ? (types.data.length ? 'Выберите вид работы' : 'Классификатор пуст') : 'Читаем классификатор…'}>
          {types.data?.map(t => <SelectItem key={t.id} value={t.id}>{t.code ? `${t.code} ${t.name}` : t.name}</SelectItem>)}
        </Select>
      )}
      <div className="space-y-1">
        <span className="text-body-small text-on-surface-variant">Пересчёт единицы</span>
        <div className="flex items-center gap-2 text-body-medium text-on-surface">
          <span className="tabular-nums">{sourceUnit ?? 'без единицы'}</span>
          <Icon icon={ArrowForward} size={18} aria-hidden className="text-on-surface-variant" />
          <span>{unit}</span>
          <span className="text-on-surface-variant">×</span>
          <NumberInput value={state.typed} onChange={state.type} label="Коэффициент пересчёта" className="w-24"
            placeholder={suggested !== null ? formatInput(suggested) : '?'} disabled={!chosen} />
        </div>
        {chosen && (
          <p className="text-body-small text-on-surface-variant">
            {result !== null && sample
              ? <>например, {sample.label}: {formatQuantity(sample.volume!)} × {formatQuantity(factor!)} = <b className="font-medium text-on-surface">{formatQuantity(result)} {unit}</b></>
              : factor !== null ? `сколько «${unit}» в одной «${sourceUnit ?? 'единице расценки'}»` : null}
            {factor !== null && !state.typed.trim() && ' · коэффициент предложен единицами'}
          </p>
        )}
      </div>
    </div>
  );
}
