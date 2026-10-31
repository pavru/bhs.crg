import { Link } from 'react-router';
import { Icon } from '@/shared/ui/Icon';
import { Close, EditNote, LockIcon, Logout, Save, Warning } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { TextAreaField } from '@/shared/ui/TextAreaField';
import { useCan } from '@/shared/api/access';
import { REASON_LENGTH } from './correction';

/**
 * Запертая накладная — почему её не правят и какой путь есть (issue #1340, D10). Путь исправления
 * называется тому, у кого кнопка «Исправить»; «отменить закрытие» — тому, у кого право закрывать период,
 * как у счёта (`InvoiceLockNote`): остальным ссылка вела бы в отказ.
 */
export function WaybillLockNote({ closed, correctable }: { closed: string; correctable: boolean }) {
  const can = useCan();
  return (
    <p role="status" className="flex items-start gap-2 text-body-small text-on-surface-variant" data-testid="waybill-locked">
      <Icon icon={LockIcon} size={18} className="shrink-0 mt-0.5 text-on-surface-variant" />
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
      <div role="alert" className="flex items-start gap-2 rounded-md border border-outline-variant bg-warning-container
        px-3 py-2 text-body-small text-on-warning-container">
        <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" />
        <p className="flex-1">
          Исправление больше не нужно: закрытие периода отменили, и накладная не заперта. Её правят обычным путём —
          верните в черновик, поправьте и проведите снова. Набранное здесь не сохранится: перенесите нужное себе.
        </p>
        <Button variant="outlined" icon={<Icon icon={Logout} size={18} />} onClick={onRelease}>
          Выйти из исправления
        </Button>
      </div>
    );

  return (
    <div className="space-y-2" data-testid="waybill-correction-bar">
      <div role="status" className="flex items-start gap-2 rounded-md border border-outline-variant bg-warning-container
        px-3 py-2 text-body-small text-on-warning-container">
        <Icon icon={EditNote} size={18} className="shrink-0 mt-0.5" />
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
        <Button variant="text" icon={<Icon icon={Close} size={18} />} disabled={writing} onClick={onCancel}>Отменить исправление</Button>
        <Button variant="filled" icon={<Icon icon={Save} size={18} />} disabled={!ready} loading={writing} onClick={onWrite}>
          Записать исправление
        </Button>
      </div>
    </div>
  );
}
