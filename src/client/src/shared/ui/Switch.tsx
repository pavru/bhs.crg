import { Tooltip } from './Tooltip';
/**
 * Переключатель — switch MD3 ([UI-69], issue #197). Трек 52×32, форма full. Выключенный — трек
 * `surface-container-highest` с обводкой 2 px `outline`, ручка 16 px `outline` слева; включённый — трек
 * `primary`, ручка 24 px `on-primary` справа (не белая: в тёмной теме трек светлый, и белая ручка на нём
 * пропадала, issue #1462). При нажатии ручка — 28 px. Знака на ручке нет (решение владельца 31.10.2026),
 * размер один. Слой состояния — круг 40 px вокруг ручки, загорается от всего трека. В строке с подписью
 * переключатель стоит в конце строки.
 *
 * Обводка — `inset-ring`, а не `border`: рамка сдвинула бы ручку на свою толщину. Отключённое — по MD3:
 * трек on-surface 12 % (`disabled-container`), ручка on-surface 38 %, у включённого — `surface`. Состояние
 * берётся из пропсов, а не `group-disabled:`: под подсказкой кнопка становится `aria-disabled`.
 */
export function Switch({ checked, onChange, disabled, label, title }: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  label?: string;
  title?: string;
}) {
  const handle = disabled
    ? (checked ? 'size-6 bg-surface' : 'size-4 bg-on-surface/38')
    : (checked ? 'size-6 bg-on-primary group-active:size-7' : 'size-4 bg-outline group-active:size-7');
  const layer = disabled ? ''
    : `state-layer state-layer-group ${checked ? '[--_state-color:var(--color-primary)]' : '[--_state-color:var(--color-on-surface)]'}`;
  return (
    <Tooltip content={title}>
      <button
        type="button" role="switch" aria-checked={checked} aria-label={label} disabled={disabled}
        onClick={() => onChange(!checked)}
        className={`group relative inline-flex h-8 w-13 shrink-0 rounded-full focus-ring disabled-container disabled:cursor-not-allowed ${
          checked ? 'bg-primary' : 'bg-surface-container-highest inset-ring-2 inset-ring-outline'
        }`}
      >
        {/* Центр ручки — 16 px от края трека: круг слоя 40 px стоит на −4 px и на 16 px. */}
        <span aria-hidden="true"
          className={`absolute top-1/2 grid size-10 -translate-y-1/2 place-items-center rounded-full [--_also-transition:left] ${
            checked ? 'left-4' : '-left-1'} ${layer}`}>
          <span className={`rounded-full transition-[width,height,background-color] duration-150 ease-standard ${handle}`} />
        </span>
      </button>
    </Tooltip>
  );
}
