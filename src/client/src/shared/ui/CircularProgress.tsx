import type { CSSProperties } from 'react';
import type { IconSize } from './icons/types';

interface CircularProgressProps {
  /** Ступень шкалы значков: 18 — в кнопке и строке, 24 — в панели, 40 и 48 — во весь пустой экран. */
  size?: IconSize;
  /**
   * Имя для читалки, когда индикатор стоит один («Загрузка…»): тогда он `role="progressbar"`. Рядом с
   * текстом хода («Сохраняется…») и внутри кнопки имени не нужно — индикатор скрыт, место подписано.
   */
  label?: string;
  /** Цвет (`text-primary`) и место в раскладке; по умолчанию — цвет текста. */
  className?: string;
}

/**
 * Круговой индикатор хода MD3 без известной доли ([UI-61]) — вместо вращающегося значка. Вращение и
 * «дыхание» дуги — классы `md-progress` в index.css; при `prefers-reduced-motion` дуга стоит, а
 * вращение втрое медленнее: индикатор не пропадает, иначе ожидание выглядело бы зависанием.
 */
export function CircularProgress({ size = 24, label, className = '' }: CircularProgressProps) {
  const stroke = size >= 40 ? 4 : size >= 24 ? 3 : 2;
  const r = (size - stroke) / 2;
  const arc = { '--md-progress-c': `${2 * Math.PI * r}px` } as CSSProperties;
  return (
    <svg width={size} height={size} viewBox={`0 0 ${size} ${size}`} focusable="false"
      className={`md-progress shrink-0 ${className}`}
      {...(label ? { role: 'progressbar', 'aria-label': label } : { 'aria-hidden': true })}>
      <circle className="md-progress-arc" style={arc} cx={size / 2} cy={size / 2} r={r}
        fill="none" stroke="currentColor" strokeWidth={stroke} strokeLinecap="round" />
    </svg>
  );
}
