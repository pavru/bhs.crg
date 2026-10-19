import type { CardLine, NomenclatureCard } from '@/shared/api/nomenclatureCard';
import { formatDay, formatNumber } from '@/shared/format/format';
import { withArchiveWord } from '@/shared/ui/archive';
import { ruPlural } from '@/shared/utils/pluralize';

/**
 * Карточка позиции в окне выбора (issue #1517) — что и каким тоном написать. Отдельно от разметки: здесь
 * различаются «не заполнено», «не прочитано» и «заполнено, но не показывается», а спутать их — значит
 * сказать человеку неправду о записи.
 */

export type CardValueTone = 'value' | 'empty' | 'unreadable' | 'quiet';

export function lineValue(line: CardLine): { text: string; tone: CardValueTone } {
  if (line.kind === 'hidden') return { text: 'заполнено, не показывается', tone: 'quiet' };
  if (line.unreadable) return { text: 'не прочитано', tone: 'unreadable' };
  if (line.text === null) return { text: 'не заполнено', tone: 'empty' };
  return { text: withArchiveWord(shown(line), line.archived), tone: 'value' };
}

/** Дата и число — письмом языка пользователя; сервер отдаёт их как хранит. */
function shown(line: CardLine): string {
  const text = line.text ?? '';
  if (line.kind === 'date') return formatDay(text);
  if (line.kind === 'number') {
    const value = Number(text);
    return text.trim() !== '' && Number.isFinite(value) ? formatNumber(value, 6) : text;
  }
  return text;
}

/** «Не заполнено полей: 2 · Не показаны: Изображение, Сертификаты (2)»; нечего сказать — пусто. */
export function cardMeta(card: Pick<NomenclatureCard, 'emptyCount' | 'notShown'>): string {
  return [
    card.emptyCount > 0 && `Не заполнено полей: ${card.emptyCount}`,
    card.notShown.length > 0 && `Не показаны: ${card.notShown.join(', ')}`,
  ].filter(Boolean).join(' · ');
}

/**
 * Сколько раз позицию выбирали — по документам, открытым читающему, поэтому без «в счетах и
 * накладных»: читателю одних накладных про счета не сказано ничего.
 */
export function chosenText({ times, lastOn }: NomenclatureCard['chosen']): string {
  if (times === 0) return 'Ещё не выбиралась';
  const count = `${times} ${ruPlural(times, 'раз', 'раза', 'раз')}`;
  return lastOn ? `Выбиралась: ${count}, последний — ${formatDay(lastOn)}` : `Выбиралась: ${count}`;
}

/**
 * Кроме названия, у позиции не заполнено ничего — сверять не с чем, и это надо сказать: пустая карточка
 * без слов читается как «не догрузилась».
 */
export function onlyName(card: Pick<NomenclatureCard, 'lines' | 'notShown' | 'aliases'>): boolean {
  const filled = card.lines.filter(l => l.text !== null || l.kind === 'hidden').length;
  return filled <= 1 && card.notShown.length === 0 && card.aliases.length === 0;
}

/** Строка сравнения «набрано ↔ в справочнике»: `ok` — совпало, `differs` — нет, `plain` — сравнивать не с чем. */
export interface CompareRow {
  label: string;
  text: string;
  tone: 'ok' | 'differs' | 'plain';
}
