import { Tooltip } from '@/shared/ui/Tooltip';
/**
 * Выбор одного варианта union-поля — вынесен из ComplexFields (issue #747).
 *
 * Своим файлом, потому что тот же переключатель понадобился ПИКЕРУ: когда запись каталога
 * одинаково подходит двум вариантам, он спрашивает вариант вторым шагом. Оставь компонент в
 * ComplexFields — вышел бы цикл импорта (ComplexFields зовёт RefPickerModal, RefPickerModal
 * звал бы ComplexFields).
 */
/**
 * Выбор ОДНОГО варианта union (issue #320/#391). Представление зависит от контента (совет Дизайнера):
 * сегмент — для ≤3 коротких подписей; вертикальный список radio-строк — для многих/длинных
 * (не обрезаются, масштабируется 2-8). `auto` выбирает сам. В списке разведены два смысла:
 * ЛЕВО = radio-выбор, ПРАВО = бейдж «задан» (есть ли значение/маппинг) — в сегменте они слипались в точку.
 */
export function VariantPicker({ options, active, onSelect, layout = 'auto' }: {
  options: { key: string; label: string; filled: boolean }[];
  active: string; onSelect: (key: string) => void;
  layout?: 'segmented' | 'list' | 'auto';
}) {
  const useList = layout === 'list'
    || (layout === 'auto' && (options.length > 3 || options.some(o => o.label.length > 18)));

  if (useList) {
    return (
      <div role="radiogroup" className="flex flex-col gap-1 text-sm">
        {options.map(o => {
          const on = o.key === active;
          return (
            <button key={o.key} type="button" role="radio" aria-checked={on} onClick={() => onSelect(o.key)}
              className={`state-layer flex items-center gap-2.5 rounded-lg border px-3 py-2 text-left ${
                on ? 'border-primary bg-primary/10 text-on-surface font-medium' : 'border-outline-variant bg-surface text-on-surface-variant'}`}>
              <span className={`grid place-items-center w-4 h-4 rounded-full border shrink-0 ${on ? 'border-primary' : 'border-outline-variant'}`}>
                {on && <span className="w-2 h-2 rounded-full bg-primary" />}
              </span>
              <span className="flex-1 min-w-0 truncate">{o.label}</span>
              {o.filled && (
                <Tooltip content="Значение задано">
                  <span className="text-caption text-primary flex items-center gap-1 shrink-0">
                    <span className="w-1.5 h-1.5 rounded-full bg-primary" /> задан
                  </span>
                </Tooltip>
              )}
            </button>
          );
        })}
      </div>
    );
  }

  return (
    <div role="radiogroup" className="inline-flex rounded-lg border border-outline-variant overflow-hidden text-sm">
      {options.map((o, i) => {
        const on = o.key === active;
        return (
          <button key={o.key} type="button" role="radio" aria-checked={on} onClick={() => onSelect(o.key)}
            className={`flex items-center gap-1.5 px-3 py-1.5 ${i > 0 ? 'border-l border-outline-variant' : ''} ${
              on ? 'bg-primary text-on-primary font-medium' : 'bg-surface text-on-surface-variant state-layer'}`}>
            {o.filled && <span className={`w-1.5 h-1.5 rounded-full shrink-0 ${on ? 'bg-on-primary' : 'bg-primary'}`} />}
            <span className="truncate">{o.label}</span>
          </button>
        );
      })}
    </div>
  );
}
