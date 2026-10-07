import * as Dialog from '@radix-ui/react-dialog';
import { useId, useState, type FormEvent } from 'react';
import { AlertTriangle } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { useToast } from '@/shared/ui/Toast';
import { apiError } from '@/shared/utils/apiError';
import { purgeOffered, usePurgeRecord, type PurgeOffer } from '@/shared/api/recordPurge';
import { changedNote, consequences, countMatches, holderLabel, mismatchShown, purgedToast } from './purgeRecord';

/**
 * Удаление записи, которую держат только данные выключенного или снятого модуля, — с потерей этих
 * ссылок (issue #1187).
 *
 * Открывается только из отказа обычного удаления и только вторым шагом: первым человеку предложен
 * архив. Подтверждение — вводом числа теряемых ссылок. Число стоит в строке «Всего ссылок» под
 * разбивкой и нарочно НЕ повторено ни в подписи поля, ни в подсказке внутри него: чтобы его
 * перепечатать, взгляд обязан пройти по тому, что будет потеряно.
 *
 * Свой диалог, а не `ConfirmDialog`: у того подтверждение — галка, а здесь поле, разбивка и
 * пересчёт после отказа.
 */
export function PurgeRecordDialog({ target, offer: offered, onClose }: {
  target: { id: string; displayName: string };
  /** Предложение из отказа удаления: разбивка держателей и число. */
  offer: PurgeOffer;
  onClose: () => void;
}) {
  const purge = usePurgeRecord();
  const toast = useToast();
  const [offer, setOffer] = useState(offered);
  const [input, setInput] = useState('');
  const [settled, setSettled] = useState(false);
  const [changed, setChanged] = useState<string | null>(null);
  // Отказ, после которого выхода больше нет: модуль включили, запись стало держать ядро.
  const [refused, setRefused] = useState<string | null>(null);
  const fieldId = useId(), hintId = useId(), errorId = useId();

  const matches = countMatches(input, offer);
  const mismatch = mismatchShown(input, offer, settled);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setSettled(true);
    if (!matches || purge.isPending) return;
    try {
      const done = await purge.mutateAsync({ id: target.id, references: offer.references });
      toast.success(purgedToast(target.displayName, done.references));
      onClose();
    } catch (err) {
      const fresh = purgeOffered(err);
      if (fresh?.allowed) {
        // Число изменилось: подтверждено не то, что удалялось бы. Показываем заново, поле пустое.
        if (fresh.references !== offer.references) setChanged(changedNote(offer.references, fresh.references));
        setOffer(fresh);
        setInput('');
        setSettled(false);
      } else {
        setRefused(apiError(err, 'Не удалось удалить запись.'));
      }
    }
  }

  return (
    <Dialog.Root open onOpenChange={o => { if (!o && !purge.isPending) onClose(); }}>
      <Dialog.Portal>
        <Dialog.Overlay className="fixed inset-0 z-40 bg-black/40" style={{ backdropFilter: 'blur(2px)' }} />
        {/* На узком экране — у верха: по центру клавиатура закрыла бы и поле, и кнопки. */}
        <Dialog.Content
          className="fixed left-1/2 top-4 sm:top-1/2 -translate-x-1/2 sm:-translate-y-1/2 z-50 rounded-[28px] p-6 w-[calc(100%-2rem)] max-w-md max-h-[calc(100dvh-2rem)] overflow-y-auto bg-surface border border-stroke focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow28)' }}>
          <Dialog.Title className="text-sm font-semibold mb-2 text-fg1">
            {refused ? 'Удаление невозможно' : `Удалить «${target.displayName}», потеряв ссылки?`}
          </Dialog.Title>

          {refused ? (
            <>
              <div className="mt-2 flex items-start gap-2.5 rounded-md bg-danger-subtle px-3 py-2.5 text-xs text-fg1" role="alert">
                <AlertTriangle size={16} className="shrink-0 mt-0.5 text-danger" aria-hidden />
                <div className="max-h-40 overflow-y-auto whitespace-pre-line min-w-0">{refused}</div>
              </div>
              <div className="flex justify-end mt-4">
                <Button variant="tonal" size="sm" onClick={onClose}>Понятно</Button>
              </div>
            </>
          ) : (
            <form onSubmit={submit} noValidate>
              <Dialog.Description className="text-xs text-fg2">
                На запись ссылаются данные выключенных модулей:
              </Dialog.Description>

              <table className="w-full mt-2 text-xs">
                <tbody>
                  {offer.holders.map((h, i) => (
                    <tr key={i} className="border-b border-stroke align-top">
                      <td className="py-1 pr-3 text-fg1">
                        {holderLabel(h)}
                        {h.documents && <div className="text-fg3">{h.documents}</div>}
                        {!h.traceable && <div className="text-warning">после удаления эти ссылки не покажет никто</div>}
                      </td>
                      <td className="py-1 text-right tabular-nums text-fg1">{h.rows}</td>
                    </tr>
                  ))}
                </tbody>
                <tfoot>
                  <tr className="font-semibold text-fg1">
                    <td className="py-1.5 pr-3">Всего ссылок</td>
                    <td className="py-1.5 text-right tabular-nums">{offer.references}</td>
                  </tr>
                </tfoot>
              </table>

              <ul className="mt-3 text-xs text-fg2 list-disc pl-4 space-y-1">
                {consequences(offer).map(line => <li key={line}>{line}</li>)}
              </ul>

              {changed && (
                <div className="mt-3 rounded-md bg-warning-subtle px-3 py-2 text-xs text-fg1" role="alert">{changed}</div>
              )}

              <div className="mt-4">
                <label htmlFor={fieldId} className="block text-xs font-medium text-fg2 mb-1">Число теряемых ссылок</label>
                {/* type=text, а не number: без стрелок и прокрутки колесом; на телефоне цифровую
                    клавиатуру даёт inputMode. Шрифт 16px — иначе iOS увеличит страницу при фокусе. */}
                <input id={fieldId} type="text" inputMode="numeric" autoComplete="off" autoFocus
                  value={input} disabled={purge.isPending}
                  onChange={e => { setInput(e.target.value); setSettled(false); }}
                  onBlur={() => setSettled(true)}
                  aria-invalid={mismatch || undefined} aria-describedby={mismatch ? errorId : hintId}
                  className={`w-full h-10 px-3 rounded-md bg-surface border text-base sm:text-sm text-fg1 tabular-nums outline-none focus-visible:ring-2 ${
                    mismatch ? 'border-danger focus-visible:ring-danger' : 'border-stroke-strong focus-visible:ring-brand'}`} />
                {mismatch
                  ? <p id={errorId} role="alert" className="mt-1 text-xs text-danger">Не совпадает с числом в строке «Всего ссылок».</p>
                  : <p id={hintId} className="mt-1 text-xs text-fg3">Введите число из строки «Всего ссылок».</p>}
              </div>

              <div className="flex gap-2 justify-end items-start mt-4">
                <Button type="button" variant="text" size="sm" className="shrink-0" disabled={purge.isPending} onClick={onClose}>Отмена</Button>
                <Button type="submit" variant="filled" danger size="sm" multiline className="min-w-0"
                  disabled={!matches} loading={purge.isPending}>
                  Удалить, потеряв ссылки
                </Button>
              </div>
            </form>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
