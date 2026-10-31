import { useId, type ReactNode } from 'react';
import { useRadioGroup } from './useRadioGroup';

export interface RadioOption<T extends string> {
  value: T;
  /** Подпись-фраза. Варианты в 1–2 слова — `SegmentedButton`. */
  label: ReactNode;
  /** Пояснение под подписью. */
  description?: ReactNode;
  /** Хвост строки после подписи — пометка «задан», пример значения. */
  trailing?: ReactNode;
  disabled?: boolean;
}

/** Имя группы: видимое — `label` над вариантами или `aria-labelledby` на своё; `aria-label` — когда имя видно рядом иначе. */
type GroupName =
  | { label: ReactNode; 'aria-labelledby'?: never; 'aria-label'?: never }
  | { label?: never; 'aria-labelledby': string; 'aria-label'?: never }
  | { label?: never; 'aria-labelledby'?: never; 'aria-label': string };

/**
 * Радиокнопки — radio button MD3 ([UI-69]): выбор одного из 2–5 вариантов с подписью-фразой. Пункт — кнопка
 * `role="radio"` во всю строку с подписью: круг 20 px, выбранный — `primary` с точкой, слой состояния 40 px
 * по кругу загорается от всей строки. Поля −10 px слева оставляют кругу место у края, как у флажка.
 * Клавиатура — `useRadioGroup` (APG): одна остановка Tab, стрелки выбирают соседний, Home и End — крайние.
 *
 * Нативный `<input type="radio">` не взят: стрелка у него выбирает всегда, и выбор с подтверждением
 * (`confirm`) открывал бы окно на каждую стрелку. `onChange` зовётся только при смене значения — месту
 * не нужно сверять его самому; исключение — `confirm`: там нажатие на выбранный вариант тоже ведёт дальше.
 *
 * С хвостом (`trailing`) хоть у одного варианта все строки — во всю ширину, хвост — у правого края: пометки
 * стоят колонкой, а не вразброс за подписями разной длины.
 */
export function RadioGroup<T extends string>({ items, value, onChange, orientation = 'vertical', confirm = false,
  disabled = false, className = '', label, ...aria }: {
  items: RadioOption<T>[];
  value: T | undefined;
  onChange: (next: T) => void;
  /** Столбиком (по умолчанию) или в строку с переносом. */
  orientation?: 'vertical' | 'horizontal';
  /** Выбор ведёт дальше: стрелки только двигают фокус, выбирает Enter, пробел или нажатие. */
  confirm?: boolean;
  /** Отключена вся группа. */
  disabled?: boolean;
  className?: string;
} & GroupName) {
  const nameId = useId();
  const off = (i: number) => disabled || !!items[i].disabled;
  const pick = (next: T) => { if (confirm || next !== value) onChange(next); };
  const radios = useRadioGroup(items.map(it => it.value), value, pick, { follow: !confirm, disabled: (_, i) => off(i) });
  const wide = items.some(it => it.trailing);

  return (
    <div role="radiogroup" aria-labelledby={label != null ? nameId : aria['aria-labelledby']} aria-label={aria['aria-label']}
      onKeyDown={radios.onKeyDown} className={className}>
      {label != null && <div id={nameId} className="text-body-small text-on-surface-variant">{label}</div>}
      <div className={orientation === 'horizontal' ? 'flex flex-wrap items-center gap-x-6' : 'flex flex-col'}>
        {items.map((it, i) => {
          const on = it.value === value;
          return (
            <button key={it.value} type="button" role="radio" aria-checked={on} {...radios.item(i)} disabled={off(i)}
              onClick={() => pick(it.value)}
              className={`group flex ${wide ? 'w-full' : 'w-fit max-w-full'} min-h-10 items-center gap-1 text-left text-body-medium focus-visible:outline-none ${
                off(i) ? 'text-on-surface/38 cursor-default' : 'text-on-surface cursor-pointer'}`}>
              <RadioMark selected={on} disabled={off(i)} />
              {/* Поля 10 px — первая строка подписи по центру круга, длинная подпись переносится под ней. */}
              <span className={`min-w-0 py-2.5 ${wide ? 'flex-1' : ''}`}>
                {it.label}
                {it.description && <span className={`block text-body-small ${off(i) ? '' : 'text-on-surface-variant'}`}>{it.description}</span>}
              </span>
              {it.trailing}
            </button>
          );
        })}
      </div>
    </div>
  );
}

/** Круг радиокнопки 20 px в слое 40 px. Слой и кольцо фокуса берут состояние у строки (`.group`). */
function RadioMark({ selected, disabled }: { selected: boolean; disabled: boolean }) {
  const ring = disabled ? 'border-on-surface/38' : selected ? 'border-primary' : 'border-on-surface-variant';
  const layer = disabled ? ''
    : `state-layer state-layer-group ${selected ? '[--_state-color:var(--color-primary)]' : '[--_state-color:var(--color-on-surface)]'}`;
  return (
    <span aria-hidden="true"
      className={`-ml-2.5 grid size-10 shrink-0 place-items-center self-start rounded-full group-focus-visible:focus-ring-mark ${layer}`}>
      <span className={`grid size-5 place-items-center rounded-full border-2 ${ring}`}>
        {selected && <span className={`size-2.5 rounded-full ${disabled ? 'bg-on-surface/38' : 'bg-primary'}`} />}
      </span>
    </span>
  );
}
