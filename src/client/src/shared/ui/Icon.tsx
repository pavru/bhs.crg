import type { SVGProps } from 'react';
import type { FilledIconDef, IconDef, IconSize } from './icons/types';

type SvgProps = Omit<SVGProps<SVGSVGElement>, 'children' | 'fill' | 'viewBox' | 'width' | 'height'>;

/** `filled` — только у значка с вариантом заливки: у прочих его нет в наборе, и это ошибка типа. */
type Variant = { icon: FilledIconDef; filled?: boolean } | { icon: IconDef; filled?: false };

export type IconProps = SvgProps & Variant & {
  /** Ступень шкалы (`ICON_SIZES`); по умолчанию 24, как у MD3. */
  size?: IconSize;
};

/**
 * Значок ([UI-60]): Material Symbols Outlined встроенным SVG из набора `shared/ui/icons` —
 * `<Icon icon={Delete} size={18} />`. Цвет — цвет текста (`currentColor`), поэтому тема и состояние
 * кнопки красят его сами. Для читалки скрыт: смысл несёт подпись рядом или имя кнопки. Значок со
 * смыслом без подписи получает `aria-label` — тогда он `role="img"` с этим именем.
 *
 * `filled` — выбранный пункт навигации (рейл, нижняя навигация); прочие значки — контуром
 * (решение владельца 28.10.2026). Вращать значок нельзя: ход операции — `CircularProgress`.
 */
export function Icon({ icon, size = 24, filled = false, ...rest }: IconProps) {
  const named = rest['aria-label'] !== undefined || rest['aria-labelledby'] !== undefined;
  return (
    <svg viewBox="0 -960 960 960" width={size} height={size} fill="currentColor" focusable="false"
      {...(named ? { role: 'img' } : { 'aria-hidden': true })} {...rest}>
      <path d={filled ? icon.fill ?? icon.d : icon.d} />
    </svg>
  );
}
