import { useMemo } from 'react';
import { SegmentProblemMark } from './SegmentProblemMark';
import { timeSegmentModel } from './timeSegments';
import { useSegmentField } from './useSegmentField';
import { resolveLocale, useLocale } from '@/shared/hooks/useLocale';

/**
 * Время суток ЧЧ:ММ сегментами (issue #1468, макет — вариант А): четыре цифры с клавиатуры, 24 часа
 * при любом языке. Вместо нативного `<input type="time">`: тот показывал «04:44 AM» при английской
 * Windows и не слушал настройку языка. Движок — общий с датой (`useSegmentField`): набор, стрелки,
 * вставка «4:44 PM», Esc, Ctrl+A.
 *
 * <p>Хвоста у поля нет — ни крестика, ни значка часов (решение владельца, вариант А): значок выглядел
 * бы кнопкой без действия. Очищают его Ctrl+A → Delete. Выбор мышью (список или сетки) можно
 * добавить позже на место хвоста, раскладка не изменится.</p>
 */
export function TimeInput({ label, value, onChange, onProblem, onIncomplete, onCommit, disabled = false, readOnly = false,
  required = false, invalid = false, hintId, className = '' }: {
  /** Имя группы сегментов для чтения с экрана — текст видимой подписи. */
  label: string;
  /** Хранимое значение: «ЧЧ:ММ» или ''. */
  value: string;
  /** «ЧЧ:ММ», когда набрано время, '' — когда стёрто; недописанное и 25:00 наружу не уходят. */
  onChange: (value: string) => void;
  /** Ошибка набранного текстом — для подписи под полем (`TimeField`); не задан — значок у поля. */
  onProblem?: (message: string | null) => void;
  /** Набранное — не время и наружу не ушло: сразу, а не после ухода (см. `DateInput.onIncomplete`). */
  onIncomplete?: (message: string | null) => void;
  onCommit?: (value: string) => void;
  disabled?: boolean;
  readOnly?: boolean;
  required?: boolean;
  invalid?: boolean;
  /** id подписи под рамкой — сегменты описываются ею (`useSegmentField`). */
  hintId?: string;
  className?: string;
}) {
  const [storedLocale] = useLocale();
  const model = useMemo(() => timeSegmentModel(resolveLocale(storedLocale)), [storedLocale]);
  const { foreign, problem, problemId, describedBy, groupProps, segments, selectedAnnouncement, startOver } = useSegmentField(model, {
    label, value, onChange, onProblem, onIncomplete, onCommit, disabled, readOnly, required, invalid, hintId,
  });
  const problemMark = <SegmentProblemMark id={problemId} problem={problem} showIcon={!onProblem} />;

  if (foreign) {
    return (
      <div role="group" aria-label={label} className={`flex items-center min-w-0 ${className}`}>
        {readOnly || disabled
          ? <span className="truncate text-on-surface-variant">{value}</span>
          // Как у даты: ввод начинается щелчком или Enter, а не фокусом — проход Tab'ом не стирает показанное.
          : <button type="button" onClick={startOver} aria-describedby={describedBy}
              aria-label={`${value} — не время, набрать время заново`}
              className="truncate text-left text-on-surface-variant rounded-sm focus:outline-none focus-visible:bg-primary-container">
              {value}
            </button>}
        {problemMark}
      </div>
    );
  }
  return (
    <div {...groupProps} className={`flex items-center ${className}`}>
      {segments}
      {problemMark}
      {selectedAnnouncement}
    </div>
  );
}
