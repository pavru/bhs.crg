import { RadioGroup } from '@/shared/ui/RadioGroup';
import { SegmentedButton } from '@/shared/ui/SegmentedButton';
import { StatusText } from '@/shared/ui/StatusText';
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
 *
 * Клавиатура у обоих видов — радиогруппы ([UI-44]): одна остановка Tab, стрелки, Home и End. `confirm` —
 * выбор сразу ведёт дальше (окно закрывается): тогда стрелки только двигают фокус, а выбирает Enter или
 * пробел, иначе первая же стрелка закрыла бы окно.
 */
export function VariantPicker({ options, active, onSelect, label, layout = 'auto', confirm = false }: {
  options: { key: string; label: string; filled: boolean }[];
  active: string; onSelect: (key: string) => void;
  /** Имя группы для диктора: что выбирают. */
  label: string;
  layout?: 'segmented' | 'list' | 'auto';
  confirm?: boolean;
}) {
  const useList = layout === 'list'
    || (layout === 'auto' && (options.length > 3 || options.some(o => o.label.length > 18)));

  if (useList) {
    // Строки радиокнопок — RadioGroup ([UI-69]): ЛЕВО — круг выбора, ПРАВО — пометка «задан».
    return (
      <RadioGroup aria-label={label} value={active} onChange={onSelect} confirm={confirm}
        items={options.map(o => ({
          value: o.key, label: o.label,
          trailing: o.filled && <StatusText tone="primary" title="Значение задано" className="ml-2">задан</StatusText>,
        }))} />
    );
  }

  // Сегмент — `SegmentedButton` ([UI-68]): радиогруппа со стрелками, как у списка, а не свой ряд.
  return (
    <SegmentedButton aria-label={label} value={active} onChange={onSelect}
      items={options.map(o => ({
        value: o.key, label: o.label,
        // Точка «задан» — не badge MD3: у того один цвет, красный, и он сказал бы «непрочитанное» ([UI-74]).
        // Подписи «задан» в сегменте места нет; в списке она есть — там пометка `StatusText`.
        badge: o.filled && <span aria-hidden className="w-1.5 h-1.5 rounded-full shrink-0 bg-primary" />,
      }))} />
  );
}
