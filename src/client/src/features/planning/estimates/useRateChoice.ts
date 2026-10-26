import { useState } from 'react';
import { useUnitFactor, useWorkTypeChoices, type RateChoice, type WorkTypeChoice } from '@/shared/api/planningMatches';
import { apiError } from '@/shared/utils/apiError';
import { formatInput } from '@/shared/format/format';
import { parseFactor } from './matchRows';

/**
 * Выбор вида работы и пересчёт единицы (макет П1 1г): вид работы, обе единицы и коэффициент между ними, пример на
 * строке. Коэффициент предлагает сервер тем же правилом, что у сопоставления (D8 `plan-31`); набранный руками —
 * главнее. Не выводится — поле пустое, и причина сказана словами сервера: единица вместо неизвестного дала бы план
 * во сто раз меньше.
 */
export function useRateChoice(sourceUnit: string | null, initial?: { workTypeId: string; factor: number | null }) {
  const types = useWorkTypeChoices();
  const [workTypeId, setWorkTypeId] = useState(initial?.workTypeId ?? '');
  // Пустая строка — коэффициент не набран: берётся предложенный сервером.
  const [typed, setTyped] = useState(initial?.factor != null ? formatInput(initial.factor) : '');
  const chosen = types.data?.find(t => t.id === workTypeId) ?? null;
  const suggestion = useUnitFactor(sourceUnit, chosen?.unitId ?? null);
  const suggested = suggestion.data?.factor ?? null;
  const factor = typed.trim() ? parseFactor(typed) : suggested;

  // Почему сопоставить нельзя — словами; `null` — можно.
  const missing = !chosen ? 'Выберите вид работы.'
    : !chosen.unitId ? `У вида работы «${chosen.name}» нет единицы измерения — её задают в классификаторе.`
    : typed.trim() && factor === null ? 'Коэффициент — число больше нуля.'
    : factor === null && suggestion.isPending ? 'Считаем коэффициент…'
    : factor === null ? suggestion.data?.reason ?? (suggestion.isError ? apiError(suggestion.error, 'Коэффициент не посчитался') : 'Назовите коэффициент.')
    : null;
  const choice: RateChoice | null = missing === null && chosen
    ? { workTypeId: chosen.id, unitId: chosen.unitId, factor: typed.trim() ? factor : undefined }
    : null;

  return {
    types, chosen, workTypeId, typed, suggested, factor, missing, choice, sourceUnit,
    pick: (id: string) => { setWorkTypeId(id); setTyped(''); },
    type: setTyped,
    /** Новый вид работы заведён из окна — он и выбран. */
    created: (made: WorkTypeChoice) => { setWorkTypeId(made.id); setTyped(''); },
  };
}

export type RateChoiceState = ReturnType<typeof useRateChoice>;
