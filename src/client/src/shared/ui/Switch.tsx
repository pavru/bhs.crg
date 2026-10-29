import { Tooltip } from './Tooltip';
/**
 * MD3 switch (issue #197): бинарный переключатель на наших токенах. Вкл — трек `brand`, крупный
 * knob цвета `on-brand` справа (не белый: в тёмной теме трек светлый, и белый knob на нём пропадал,
 * issue #1462); выкл — контурный трек `outline`, мелкий knob слева. Доступен (role=switch).
 */
export function Switch({ checked, onChange, disabled, label, title, size = 'md' }: {
  checked: boolean;
  onChange: (checked: boolean) => void;
  disabled?: boolean;
  label?: string;
  title?: string;
  size?: 'sm' | 'md';
}) {
  const track = size === 'sm' ? 'w-9 h-5' : 'w-11 h-6';
  const knobOn = size === 'sm' ? 'w-3.5 h-3.5 right-0.5' : 'w-5 h-5 right-0.5';
  const knobOff = size === 'sm' ? 'w-2.5 h-2.5 left-[3px]' : 'w-3 h-3 left-1';
  return (
    <Tooltip content={title}>
      <button
        type="button" role="switch" aria-checked={checked} aria-label={label} disabled={disabled}
        onClick={() => onChange(!checked)}
        // Слой — цветом on-surface поверх дорожки любого положения; отключённое — по MD3: дорожка
        // on-surface 12 %, ползунок on-surface 38 % (у включённого — surface).
        className={`group relative inline-flex shrink-0 items-center rounded-full state-layer [--_state-color:var(--color-on-surface)] focus-ring disabled-container disabled:cursor-not-allowed ${track} ${
          checked ? 'bg-primary' : 'bg-surface border-2 border-outline'
        }`}
      >
        <span
          aria-hidden="true"
          className={`absolute rounded-full transition-[width,height] duration-150 ease-standard ${
            checked ? `bg-on-primary group-disabled:bg-surface ${knobOn}` : `bg-outline group-disabled:bg-on-surface/38 ${knobOff}`
          }`}
        />
      </button>
    </Tooltip>
  );
}
