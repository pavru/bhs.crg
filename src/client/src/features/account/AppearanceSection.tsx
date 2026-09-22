import { useState } from 'react';
import {
  useLocale, LOCALE_OPTIONS, SYSTEM_LOCALE, resolveLocale, formatDate, formatNumber,
} from '@/shared/hooks/useLocale';
import { ThemeToggle } from '@/shared/ui/ThemeToggle';

const PREVIEW_DATE = new Date(2025, 11, 31, 14, 5, 0); // 31 дек 2025 14:05
const PREVIEW_NUMBER = 1234567.89;

/**
 * Оформление и язык — ЛИЧНЫЕ настройки (issue #954, ТЗ AUTH-16.6).
 *
 * ⚠️ Язык переехал сюда из «Настройки системы» (issue #954). Там он лежал за правом
 * `core.system.manage`, то есть сменить формат дат и чисел мог только администратор экземпляра —
 * личная настройка была заперта административным правом, и выглядело это как «такой настройки
 * нет». Тема стояла только в боковой панели: человек, искавший её в профиле, не находил.
 *
 * Обе настройки хранит сервер (issue #953) и обе приезжают на любой компьютер, где человек войдёт.
 */
export function AppearanceSection() {
  const [locale, setLocale] = useLocale();
  const [localeSaved, setLocaleSaved] = useState(false);

  const resolved = resolveLocale(locale);

  function handleSelect(value: string) {
    setLocale(value);
    setLocaleSaved(true);
    setTimeout(() => setLocaleSaved(false), 2000);
  }

  return (
    <div className="space-y-4">
      <div>
        <div className="text-xs text-fg4 mb-1">Тема оформления</div>
        <ThemeToggle />
      </div>

      <div className="text-xs text-fg4">Язык и формат дат</div>
      <p className="text-xs text-fg3">
        Определяет формат дат и чисел в интерфейсе. Тема и язык хранятся в учётной записи и
        приедут на любой компьютер, где вы войдёте.
      </p>

      <div className="space-y-1">
        {LOCALE_OPTIONS.map(opt => {
          const isSelected = locale === opt.value;
          return (
            <button
              key={opt.value}
              type="button"
              onClick={() => handleSelect(opt.value)}
              className={`w-full flex items-center gap-3 px-3 py-2 rounded-md text-sm text-left transition-colors ${
                isSelected
                  ? 'bg-brand-subtle border border-brand-subtle text-brand-pressed'
                  : 'border border-transparent text-fg2 hover:bg-base'
              }`}
            >
              <span className={`w-3.5 h-3.5 rounded-full border-2 shrink-0 flex items-center justify-center ${
                isSelected ? 'border-brand' : 'border-stroke-strong'
              }`}>
                {isSelected && <span className="w-2 h-2 rounded-full bg-brand block" />}
              </span>
              <span className="flex-1">{opt.label}</span>
              {opt.value === SYSTEM_LOCALE && (
                <span className="text-xs text-fg4 font-mono">{navigator.language}</span>
              )}
            </button>
          );
        })}
      </div>

      {/* Preview */}
      <div className="rounded-lg bg-base border border-stroke p-3 space-y-1.5">
        <p className="text-xs font-medium text-fg3 mb-2">Предпросмотр ({resolved})</p>
        <div className="flex gap-3 text-sm">
          <span className="text-fg3 w-20 shrink-0">Дата:</span>
          <span className="text-fg1 font-mono">
            {formatDate(PREVIEW_DATE, locale)}
          </span>
        </div>
        <div className="flex gap-3 text-sm">
          <span className="text-fg3 w-20 shrink-0">Дата и время:</span>
          <span className="text-fg1 font-mono">
            {formatDate(PREVIEW_DATE, locale, {
              day: '2-digit', month: '2-digit', year: 'numeric',
              hour: '2-digit', minute: '2-digit',
            })}
          </span>
        </div>
        <div className="flex gap-3 text-sm">
          <span className="text-fg3 w-20 shrink-0">Число:</span>
          <span className="text-fg1 font-mono">
            {formatNumber(PREVIEW_NUMBER, locale)}
          </span>
        </div>
        <div className="flex gap-3 text-sm">
          <span className="text-fg3 w-20 shrink-0">Валюта:</span>
          <span className="text-fg1 font-mono">
            {formatNumber(PREVIEW_NUMBER, locale, { style: 'currency', currency: 'RUB' })}
          </span>
        </div>
      </div>

      {localeSaved && (
        <p className="text-sm text-success">Сохранено</p>
      )}
    </div>
  );
}
