/**
 * MD3 switch (issue #197): бинарный переключатель на наших токенах. Вкл — трек `brand`, крупный
 * белый knob справа; выкл — контурный трек `stroke-strong`, мелкий knob слева. Доступен (role=switch).
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
    <button
      type="button" role="switch" aria-checked={checked} aria-label={label} title={title} disabled={disabled}
      onClick={() => onChange(!checked)}
      className={`relative inline-flex shrink-0 items-center rounded-full transition-colors focus-visible:outline-none focus-visible:ring-2 focus-visible:ring-brand disabled:opacity-40 disabled:cursor-not-allowed ${track} ${
        checked ? 'bg-brand' : 'bg-surface border-2 border-stroke-strong'
      }`}
    >
      <span
        aria-hidden="true"
        className={`absolute rounded-full transition-all ${
          checked ? `bg-white ${knobOn}` : `bg-stroke-strong ${knobOff}`
        }`}
      />
    </button>
  );
}
