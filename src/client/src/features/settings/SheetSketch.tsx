import type { PrintFit, PrintOrientation, PrintPaper } from '@/shared/api/printProfiles';
import { pageSize } from './printProfileRules';

/**
 * Набросок листа по профилю печати (issue #1455): бумага в пропорции, поля пунктиром, строки таблицы.
 * Нарисован в миллиметрах (`viewBox` — размер страницы), поэтому пропорции честные и ширину ему
 * задавать не нужно: высоту даёт класс, ширина выходит из пропорции бумаги.
 *
 * <p>«По ширине» — строки во всю ширину между полями. «Не вписывать» — таблица шире места под
 * текст и обрезается краем листа: то, что в жизни уедет на следующую страницу.</p>
 */
export function SheetSketch({ paper, orientation, fit, margins, size = 'sm', invalid = false, label }: {
  paper: PrintPaper;
  orientation: PrintOrientation;
  fit: PrintFit;
  /** Поля, мм. Негодное поле (`null`) рисуется нулём — набросок не ломается от недописанного числа. */
  margins: { top: number | null; bottom: number | null; left: number | null; right: number | null };
  size?: 'sm' | 'lg';
  /** Места под текст не хватает — область текста подсвечена как ошибка. */
  invalid?: boolean;
  /** Что набросок показывает — имя картинки для скринридера; без него набросок декоративен. */
  label?: string;
}) {
  const { width, height } = pageSize(paper, orientation);
  const top = margins.top ?? 0, bottom = margins.bottom ?? 0, left = margins.left ?? 0, right = margins.right ?? 0;
  const textWidth = Math.max(width - left - right, 0);
  const textHeight = Math.max(height - top - bottom, 0);
  const rowWidth = fit === 'width' ? textWidth : textWidth * 1.35;
  const step = Math.max(height / 14, 8);
  const rows = Math.max(Math.floor((textHeight - step) / step), 0);

  return (
    <svg viewBox={`0 0 ${width} ${height}`} className={size === 'lg' ? 'h-48 shrink-0' : 'h-10 shrink-0'}
      {...(label ? { role: 'img', 'aria-label': label } : { 'aria-hidden': true })}>
      <rect x={0} y={0} width={width} height={height} className="fill-surface stroke-stroke-strong"
        strokeWidth={1} vectorEffect="non-scaling-stroke" />
      <rect x={left} y={top} width={textWidth} height={textHeight}
        className={invalid ? 'fill-danger-subtle stroke-danger' : 'fill-brand-subtle stroke-brand'}
        strokeWidth={1} strokeDasharray="3 2" vectorEffect="non-scaling-stroke" />
      {Array.from({ length: rows }, (_, i) => (
        <rect key={i} x={left + step * 0.3} y={top + step * (i + 0.7)} rx={1}
          width={Math.max((i === 0 ? rowWidth * 0.55 : rowWidth) - step * 0.6, 0)} height={step * 0.35}
          className="fill-stroke-strong" />
      ))}
    </svg>
  );
}
