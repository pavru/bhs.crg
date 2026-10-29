import { Icon } from '@/shared/ui/Icon';
import { Add, Remove } from '@/shared/ui/icons';
import { tagLabelOf, tagOrder, type TagDefinition } from '@/shared/api/tags';
import { Tooltip } from '@/shared/ui/Tooltip';

/**
 * Функциональный тэг поля как ОДИН объект (issue #630).
 *
 * Номер параметра («Идентификатор 3», issue #583) жил рядом с чипом отдельным полем ввода в
 * собственной рамке — среди соседних чипов строки «Функц. тэги» тройка читалась как самостоятельный
 * элемент, а не как номер именно этого тэга. Здесь номер — второй сегмент того же чипа, отделённый
 * линией.
 *
 * Стрелки −/+ появляются по наведению, но место под них зарезервировано ВСЕГДА: строка тэгов
 * переносится по ширине, и чип, растущий под курсором, толкал бы соседей на другую строку.
 * `invisible` (а не `opacity-0`) заодно снимает с них клики — невидимая кнопка, меняющая номер, была
 * бы ловушкой, а номер здесь дорогой: его правка меняет ключи всех объектов разом.
 */
export function FunctionalTagChip({ tag, entry, onToggle, onOrderChange }: {
  tag: TagDefinition;
  /** Запись тэга как она лежит в схеме («identity:3»); undefined — тэг не проставлен. */
  entry: string | undefined;
  onToggle: () => void;
  /** Сырой ввод номера — фильтрацию цифр и «пусто = без номера» делает вызывающий. */
  onOrderChange: (raw: string) => void;
}) {
  const on = entry !== undefined;
  const order = entry !== undefined ? tagOrder(entry) : null;
  const showParameter = on && !!tag.parameter;

  // Стрелки ходят в пределах 1…99: ноль и отрицательные сервер всё равно считает «без номера»
  // (см. tagOrder), а два знака — предел поля ввода.
  const step = (delta: number) => onOrderChange(String(Math.min(99, Math.max(1, (order ?? 0) + delta))));

  const arrowClass = 'invisible group-hover:visible group-focus-within:visible '
    + 'text-tertiary hover:text-on-tertiary-container disabled:opacity-40 shrink-0';

  return (
    <span
      className={`group inline-flex items-stretch overflow-hidden rounded-full border text-xs transition-colors ${
        on
          ? 'bg-tertiary-container border-tertiary/50 text-on-tertiary-container focus-within:ring-1 focus-within:ring-tertiary/50'
          : 'border-outline-variant text-on-surface-variant hover:border-outline hover:text-on-surface-variant'
      }`}
    >
      <Tooltip content={tag.description}>
        <button type="button" onClick={onToggle} className="px-2 py-0.5">
          {tag.label}
        </button>
      </Tooltip>
      {/* Номер — только у проставленного тэга, который его принимает: у неотмеченного поля
          номер задавать не над чем. */}
      {showParameter && (
        <Tooltip content={tag.parameter!.description}>
          <span
            className="flex items-center gap-0.5 border-l border-tertiary/50 pl-1 pr-1"
          >
            <button type="button" className={arrowClass} onClick={() => step(-1)}
              disabled={(order ?? 0) <= 1} aria-label={`Уменьшить порядок: ${tag.label}`}>
              <Icon icon={Remove} size={18} />
            </button>
            <input
              type="text"
              inputMode="numeric"
              maxLength={2}
              value={order ?? ''}
              placeholder={tag.parameter!.label}
              aria-label={`Порядок компонента: ${tag.label}`}
              onChange={e => onOrderChange(e.target.value)}
              className="w-5 bg-transparent text-center tabular-nums text-on-tertiary-container outline-none
                         placeholder:text-tertiary/60"
            />
            <button type="button" className={arrowClass} onClick={() => step(1)}
              disabled={(order ?? 0) >= 99} aria-label={`Увеличить порядок: ${tag.label}`}>
              <Icon icon={Add} size={18} />
            </button>
          </span>
        </Tooltip>
      )}
    </span>
  );
}

/**
 * Тот же тэг в свёрнутой шапке поля — только показ, без правки. Сегменты те же: подпись и номер,
 * потому что бейдж «Идентификатор» без номера у поля, которое номер несёт, скрывал бы ровно то,
 * ради чего в шапку и смотрят.
 */
export function FunctionalTagBadge({ registry, entry }: {
  registry: TagDefinition[] | undefined;
  entry: string;
}) {
  const order = tagOrder(entry);
  return (
    <span className="hidden md:inline-flex items-center shrink-0 overflow-hidden rounded
                     bg-primary-container text-caption text-on-primary-container">
      <span className="px-1.5 py-0.5">{tagLabelOf(registry, entry)}</span>
      {order !== null && (
        <span className="border-l border-primary/40 px-1.5 py-0.5 tabular-nums">{order}</span>
      )}
    </span>
  );
}
