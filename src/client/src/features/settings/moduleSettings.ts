import type { ModuleSetting } from '@/shared/api/moduleSettings';

/**
 * Логика формы настроек модуля (issue #1070) — отдельно от экрана, чтобы её можно было проверить.
 *
 * Черновик хранит то, что человек НАБРАЛ, по ключам. Ключа нет — поле не трогали; `null` — нажали
 * «вернуть умолчание».
 */
export type SettingsDraft = Record<string, string | null>;

/** Число для человека: с запятой. На сервере и в хранении — с точкой. */
export function shown(value: string): string {
  return value.replace('.', ',');
}

/** Что стоит в поле: набранное, а если не трогали — действующее значение. */
export function fieldText(setting: ModuleSetting, draft: SettingsDraft): string {
  const typed = draft[setting.key];
  if (typed === undefined) return shown(setting.value);
  return typed === null ? shown(setting.default) : typed;
}

/**
 * Набранное — в вид сервера: запятая становится точкой, пробелы по краям уходят. Больше ничего
 * не чиним: «1 000» или «1,5,5» сервер отвергнет со своей причиной, а угаданное число было бы
 * значением, которого человек не вводил.
 */
export function toServer(typed: string): string {
  return typed.trim().replace(',', '.');
}

/** Одно ли это число: «1», «1,0» и «1.00» — одно значение, и менять на него нечего. */
function sameNumber(a: string, b: string): boolean {
  const [x, y] = [Number(a), Number(b)];
  return a.trim() !== '' && b.trim() !== '' && Number.isFinite(x) && Number.isFinite(y) && x === y;
}

/**
 * Что уйдёт на сервер — только изменённое. Пустой ответ — сохранять нечего.
 *
 * Сброс (`null`) уходит, только если настройка сохранена: у несохранённой умолчание и так действует.
 */
export function changedValues(settings: ModuleSetting[], draft: SettingsDraft): Record<string, string | null> {
  const values: Record<string, string | null> = {};
  for (const setting of settings) {
    const typed = draft[setting.key];
    if (typed === undefined) continue;
    if (typed === null) {
      if (setting.stored !== null || setting.stale !== null) values[setting.key] = null;
      continue;
    }
    const next = toServer(typed);
    // Негодное сохранённое дают перезаписать и тем же числом, что действует: действует умолчание,
    // а в базе лежит другое — без записи оно там и останется.
    if (setting.stale !== null || !sameNumber(next, setting.value)) values[setting.key] = next;
  }
  return values;
}

/**
 * Что видно без сервера: не число или вне границ. Проверяется ДО вопроса о следствиях — иначе
 * человек сначала подтверждал бы «изменить допуск на 500 ₽», а потом узнавал бы, что так нельзя.
 *
 * Знаки после запятой проверяет сервер, и его отказ остаётся
 * последним словом.
 */
export function localRefusals(
  settings: ModuleSetting[], values: Record<string, string | null>,
): Record<string, string> {
  const refusals: Record<string, string> = {};
  for (const setting of settings) {
    const next = values[setting.key];
    if (next === undefined || next === null || setting.kind !== 'number') continue;
    const number = Number(next);
    if (next === '' || !Number.isFinite(number) || !/^-?\d+(\.\d+)?$/.test(next)) {
      refusals[setting.key] = 'нужно число';
    } else if (setting.min !== null && setting.max !== null && (number < setting.min || number > setting.max)) {
      refusals[setting.key] =
        `допустимо от ${shown(String(setting.min))} до ${withUnit(shown(String(setting.max)), setting.unit)}`;
    }
  }
  return refusals;
}

/**
 * Предупреждения настроек, которые сейчас меняются, — их показывают до сохранения. Только там, где
 * меняется ДЕЙСТВУЮЩЕЕ значение: замена негодного сохранённого тем же числом настройку не меняет,
 * и спрашивать «1,00 ₽ → 1,00 ₽» не о чем.
 */
export function changeWarnings(settings: ModuleSetting[], values: Record<string, string | null>) {
  return settings
    .filter(s => s.key in values && s.changeWarning && !sameNumber(values[s.key] ?? s.default, s.value))
    .map(s => ({
      key: s.key,
      title: s.title,
      warning: s.changeWarning!,
      from: withUnit(shown(s.value), s.unit),
      to: withUnit(shown(values[s.key] ?? s.default), s.unit),
    }));
}

export function withUnit(text: string, unit: string | null): string {
  return unit ? `${text} ${unit}` : text;
}

/** Подсказка под полем: умолчание и границы — чтобы отказ сервера не был первым, кто их назовёт. */
export function boundsHint(setting: ModuleSetting): string {
  const parts = [`По умолчанию: ${withUnit(shown(setting.default), setting.unit)}`];
  if (setting.min !== null && setting.max !== null)
    parts.push(`допустимо от ${shown(String(setting.min))} до ${shown(String(setting.max))}`);
  return parts.join(' · ');
}

/**
 * Сохранено одно, действует другое: в базе лежит значение, которое эта версия не принимает
 * (границы сменились, базу правили руками). Экран обязан сказать это, а не показать умолчание
 * как будто так и задано.
 */
export function staleStored(setting: ModuleSetting): string | null {
  if (setting.stale === null) return null;
  return `Сохранено «${setting.stale}», но это значение не подходит — действует ` +
    `${withUnit(shown(setting.value), setting.unit)}. Сохраните годное значение или верните умолчание.`;
}

/** Причины отказа по полям из ответа сервера; пусто — отказ не про поле. */
export function fieldErrors(e: unknown): Record<string, string> {
  const fields = (e as { response?: { data?: { fields?: unknown } } })?.response?.data?.fields;
  if (!fields || typeof fields !== 'object') return {};
  return Object.fromEntries(
    Object.entries(fields as Record<string, unknown>).filter(([, v]) => typeof v === 'string'),
  ) as Record<string, string>;
}

/** Первая буква — заглавная: причина сервера начинается со строчной («допустимо от…»). */
export function sentence(text: string): string {
  return text ? text[0].toUpperCase() + text.slice(1) : text;
}
