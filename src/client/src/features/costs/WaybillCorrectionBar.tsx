import { Link } from 'react-router';
import { Lock, LogOut, PencilLine, Save, TriangleAlert, X } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { useCan } from '@/shared/api/access';
import { REASON_LENGTH } from './waybillCorrection';

/**
 * Запертая накладная — почему её не правят и какой путь есть (issue #1340, D10). Путь исправления
 * называется тому, у кого кнопка «Исправить»; «отменить закрытие» — тому, у кого право закрывать период,
 * как у счёта (`InvoiceLockNote`): остальным ссылка вела бы в отказ.
 */
export function WaybillLockNote({ closed, correctable }: { closed: string; correctable: boolean }) {
  const can = useCan();
  return (
    <p role="status" className="flex items-start gap-2 text-xs text-fg2" data-testid="waybill-locked">
      <Lock size={13} className="shrink-0 mt-0.5 text-fg3" />
      <span>
        Заперта: {closed}. Вернуть в черновик и править обычным путём нельзя — её материалы вошли в закрытый
        период.
        {correctable && <> Изменить её можно исправлением с причиной — кнопкой «Исправить»: прежнее содержимое останется в истории.</>}
        {can.permission('core.period.close') && (
          <> Закрытие отменяют на странице <Link to="/periods" className="underline">«Учётный период»</Link>.</>
        )}
      </span>
    </p>
  );
}

/**
 * Полоса режима «Исправление» накладной (вариант 1a, D10): то же, что у счёта (`CorrectionBar`), но без
 * «Проверить…» — у накладной нет книги, и предпросмотр сторожить нечего (D2). Запись одна: причина, шапка и
 * строки одним телом.
 *
 * <p>Причину пишут здесь же, и кнопка без неё не нажимается, а не получает отказ сервера.</p>
 */
export function WaybillCorrectionBar({
  reason, onReason, ready, writing, siteMoved, unneeded, onCancel, onWrite, onRelease,
}: {
  reason: string;
  onReason: (reason: string) => void;
  /** Есть причина и есть что записать. */
  ready: boolean;
  writing: boolean;
  /** Стройка сменена: учётная дата пересчитается (D4) — сказать до записи. */
  siteMoved: boolean;
  /** Закрытие отменили, пока шла правка: исправлять больше нечего. */
  unneeded: boolean;
  onCancel: () => void;
  onWrite: () => void;
  /** Выйти из исправления: накладная больше не заперта. */
  onRelease: () => void;
}) {
  if (unneeded)
    return (
      <div role="alert" className="flex items-start gap-2 rounded-lg border border-warning-border bg-warning-subtle
        px-3 py-2 text-xs text-warning">
        <TriangleAlert size={13} className="shrink-0 mt-0.5" />
        <p className="flex-1">
          Исправление больше не нужно: закрытие периода отменили, и накладная не заперта. Её правят обычным путём —
          верните в черновик, поправьте и проведите снова. Набранное здесь не сохранится: перенесите нужное себе.
        </p>
        <Button size="sm" variant="outlined" icon={<LogOut size={12} />} onClick={onRelease}>
          Выйти из исправления
        </Button>
      </div>
    );

  return (
    <div className="space-y-2" data-testid="waybill-correction-bar">
      <div role="status" className="flex items-start gap-2 rounded-lg border border-warning-border bg-warning-subtle
        px-3 py-2 text-xs text-warning">
        <PencilLine size={13} className="shrink-0 mt-0.5" />
        <p className="flex-1">
          Исправление закрытого периода: накладная останется проведённой, прежнее содержимое ляжет в историю.
          Правьте шапку и строки — запишется всё одним исправлением.
          {siteMoved && ' Стройка сменена: учётная дата пересчитается — накладная ляжет первым открытым днём новой стройки, не раньше даты отпуска.'}
        </p>
      </div>
      <div className="flex items-end gap-2 flex-wrap">
        <TextAreaField label="Причина исправления" required rows={2} maxLength={REASON_LENGTH}
          containerClassName="flex-1 min-w-64" value={reason}
          hint="Ляжет в историю накладной и в журнал: через год исправление объяснит только она"
          onChange={e => onReason(e.target.value)} />
        <Button variant="text" icon={<X size={13} />} disabled={writing} onClick={onCancel}>Отменить исправление</Button>
        <Button variant="filled" icon={<Save size={13} />} disabled={!ready} loading={writing} onClick={onWrite}>
          Записать исправление
        </Button>
      </div>
    </div>
  );
}
