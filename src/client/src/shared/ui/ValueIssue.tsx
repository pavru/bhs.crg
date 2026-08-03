import { AlertTriangle } from 'lucide-react';

/**
 * Расхождение значения с объявленным типом — у поля формы (issue #644).
 *
 * Предупреждением, а не ошибкой, и намеренно НЕ блокирует сохранение: значение чаще всего пришло не
 * отсюда (распознавание, вставка, привязка набора), а черновик заполняют неделями. Жёсткий гейт
 * стоит на генерации, здесь — только видимость.
 */
export function ValueIssueHint({ messages, compact }: { messages: string[] | undefined; compact?: boolean }) {
  if (!messages?.length) return null;
  return (
    <p className={`${compact ? 'text-[11px] mt-0.5' : 'text-xs mt-1'} text-warning flex items-start gap-1`}>
      <AlertTriangle size={compact ? 11 : 12} className="shrink-0 mt-[3px]" />
      <span>{messages.join(' ')}</span>
    </p>
  );
}

/** Свод-бейдж расхождений внутри составного поля или раздела — по образцу счётчика битых ссылок. */
export function ValueIssueBadge({ count, className = '' }: { count: number; className?: string }) {
  if (count <= 0) return null;
  // Подложка токеном -subtle, а не заливкой цветом текста: в тёмной теме --color-warning сам светлый,
  // и белые цифры на нём не читались бы.
  return (
    <span title={`Значений не по типу: ${count}`}
      className={`inline-flex items-center justify-center min-w-[16px] h-4 px-1 rounded-full bg-warning-subtle text-warning border border-warning-border text-[10px] font-semibold leading-none ${className}`}>
      {count}
    </span>
  );
}
