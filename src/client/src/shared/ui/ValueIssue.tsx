import { Icon } from './Icon';
import { Warning } from './icons';
import { StatusText } from './StatusText';

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
    <p className={`${compact ? 'text-label-small mt-0.5' : 'text-body-small mt-1'} text-warning flex items-start gap-1`}>
      <Icon icon={Warning} size={18} className="shrink-0 mt-[3px]" />
      <span>{messages.join(' ')}</span>
    </p>
  );
}

/**
 * Свод расхождений внутри составного поля или раздела — у подписи поля: пометка `warning` с числом
 * ([UI-74]), а не своя пилюля. Пометка, а не `CountText`: число без знака и тона не сказало бы, что внутри
 * есть что поправить; не `Badge` — его красный здесь соврал бы, расхождение не ошибка.
 */
export function ValueIssueCount({ count, className = '' }: { count: number; className?: string }) {
  if (count <= 0) return null;
  return <StatusText tone="warning" title={`Значений не по типу: ${count}`} className={`tabular-nums ${className}`}>{count}</StatusText>;
}
