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
              className={`flex items-center gap-2.5 rounded-lg border px-3 py-2 text-left transition-colors ${
                on ? 'border-brand bg-brand/10 text-fg1 font-medium' : 'border-stroke bg-surface text-fg2 hover:bg-base'}`}>
              <span className={`grid place-items-center w-4 h-4 rounded-full border shrink-0 ${on ? 'border-brand' : 'border-stroke'}`}>
                {on && <span className="w-2 h-2 rounded-full bg-brand" />}
              </span>
              <span className="flex-1 min-w-0 truncate">{o.label}</span>
              {o.filled && (
                <span className="text-[11px] text-brand flex items-center gap-1 shrink-0" title="Значение задано">
                  <span className="w-1.5 h-1.5 rounded-full bg-brand" /> задан
                </span>
              )}
            </button>
          );
        })}
      </div>
    );
  }

  return (
    <div role="radiogroup" className="inline-flex rounded-lg border border-stroke overflow-hidden text-sm">
      {options.map((o, i) => {
        const on = o.key === active;
        return (
          <button key={o.key} type="button" role="radio" aria-checked={on} onClick={() => onSelect(o.key)}
            className={`flex items-center gap-1.5 px-3 py-1.5 transition-colors ${i > 0 ? 'border-l border-stroke' : ''} ${
              on ? 'bg-brand text-white font-medium' : 'bg-surface text-fg2 hover:bg-base'}`}>
            {o.filled && <span className={`w-1.5 h-1.5 rounded-full shrink-0 ${on ? 'bg-white' : 'bg-brand'}`} />}
            <span className="truncate">{o.label}</span>
          </button>
        );
      })}
    </div>
  );
}
