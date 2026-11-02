/**
 * Разделитель MD3 ([UI-77], sharedui-md3-13): черта 1 px роли `outline-variant`, горизонтальная во всю ширину
 * или вертикальная во всю высоту ряда. `inset` — отступ 16: `start` — от ведущего края (между пунктами
 * списка под знаком), `both` — с двух сторон. Для читалки — `separator`; `decorative` прячет черту, которая
 * только украшает и границы смысла не несёт.
 */
export function Divider({ orientation = 'horizontal', inset, decorative = false, className = '' }: {
  orientation?: 'horizontal' | 'vertical';
  inset?: 'start' | 'both';
  decorative?: boolean;
  className?: string;
}) {
  const vertical = orientation === 'vertical';
  const indent = inset === 'both' ? (vertical ? 'my-4' : 'mx-4') : inset === 'start' ? (vertical ? 'mt-4' : 'ms-4') : '';
  const look = `shrink-0 border-0 bg-outline-variant ${vertical ? 'w-px self-stretch' : 'h-px'} ${indent} ${className}`;
  if (decorative) return <div aria-hidden className={look} />;
  // `<hr>` и есть `separator`; вертикальной черте ориентацию называет атрибут.
  return vertical ? <div role="separator" aria-orientation="vertical" className={look} /> : <hr className={look} />;
}
