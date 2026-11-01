/**
 * Линейный индикатор хода MD3 ([UI-74]) — ход операции на месте: пакет сканов, разбор файла. С долей
 * (`value` от 0 до 1), когда она известна, и бегущей полосой без доли, когда нет. Самодельных полос нет:
 * у них не было ни роли, ни доли для диктора — полоса стояла `aria-hidden`.
 *
 * <p>Вид — классический (выбор 01.11.2026): полоса 4 px, индикатор `primary` на дорожке
 * `secondary-container`, без зазора и точки-ограничителя M3 2024. Бег без доли — класс `md-linear` в
 * index.css; при «меньше движения» он втрое медленнее, но не стоит: стоящая полоса выглядела бы зависанием.</p>
 *
 * <p>Элемент — `progressbar` с именем (`label`); `aria-valuenow` — только при известной доле, в процентах:
 * без доли диктор говорит «занято», а не «0 %».</p>
 */
export function LinearProgress({ value, label, className = '' }: {
  /** Доля от 0 до 1; нет — доля неизвестна, полоса бежит. */
  value?: number;
  label: string;
  className?: string;
}) {
  const known = value !== undefined;
  const percent = known ? Math.round(Math.min(1, Math.max(0, value)) * 100) : undefined;
  return (
    <div role="progressbar" aria-label={label}
      {...(known && { 'aria-valuenow': percent, 'aria-valuemin': 0, 'aria-valuemax': 100 })}
      className={`relative h-1 rounded-full bg-secondary-container overflow-hidden ${className}`}>
      {known
        ? <div className="h-full rounded-full bg-primary transition-[width] duration-300 ease-standard" style={{ width: `${percent}%` }} />
        : <div className="md-linear absolute inset-y-0 w-2/5 rounded-full bg-primary" />}
    </div>
  );
}
