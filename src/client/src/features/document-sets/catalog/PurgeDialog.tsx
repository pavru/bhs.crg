import * as Dialog from '@radix-ui/react-dialog';
import { DIALOG_SCRIM, DIALOG_SURFACE, DIALOG_TITLE } from '@/shared/ui/dialogSurface';
import { useState, type FormEvent, type ReactNode } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Warning } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { useSnackbar } from '@/shared/ui/Snackbar';
import type { PurgeHolder, PurgeOffer } from '@/shared/api/recordPurge';
import { DataGrid, type DataGridColumn } from '@/shared/ui/DataGrid';
import { useHistoryLayer } from '@/shared/ui/historyLayers';
import { countMatches, holderLabel, mismatchShown, type PurgeFailure } from './purgeRecord';
import { PurgeCountField } from './PurgeCountField';

const HOLDER_COLUMNS: DataGridColumn<PurgeHolder>[] = [
  { key: 'holder', label: 'Кто ссылается', wrap: true,
    cell: h => (
      <>
        {holderLabel(h)}
        {h.documents && <div className="text-on-surface-variant">{h.documents}</div>}
        {!h.traceable && <div className="text-warning">после удаления эти ссылки не покажет никто</div>}
      </>
    ) },
  { key: 'rows', label: 'Ссылок', align: 'right', width: 'w-20' },
];

/** Что удаляют и какими словами об этом говорить: запись (issue #1187) или уровень (issue #1243). */
export interface PurgeSubject<O extends PurgeOffer> {
  /** «Удалить «X», потеряв ссылки?» */
  title: string;
  /** Строка над разбивкой держателей. */
  intro: string;
  /** Блок над разбивкой — что уйдёт вместе (у уровня). */
  above?: (offer: O) => ReactNode;
  /** Последствия — строками списка. */
  lines: (offer: O) => string[];
  submit: string;
  run: (references: number) => Promise<{ references: number }>;
  /** Слова успеха. */
  done: (references: number) => string;
  failure: (e: unknown, shown: O) => PurgeFailure<O>;
}

/**
 * Удаление того, что держат только данные выключенного или снятого модуля, — с потерей этих
 * ссылок. Одно окно на запись и на уровень: различаются они словами и адресом, а ввод числа,
 * пересчёт после отказа и три исхода неудачи — одни.
 *
 * Открывается только из отказа обычного удаления. Подтверждение — вводом числа теряемых ссылок. Число стоит в строке «Всего ссылок» под
 * разбивкой и нарочно НЕ повторено ни в подписи поля, ни в подсказке внутри него: чтобы его
 * перепечатать, взгляд обязан пройти по тому, что будет потеряно.
 *
 * Свой диалог, а не `ConfirmDialog`: у того подтверждение — галка, а здесь поле, разбивка и
 * пересчёт после отказа.
 */
export function PurgeDialog<O extends PurgeOffer>({ subject, offer: offered, pending, onClose, onDone }: {
  subject: PurgeSubject<O>;
  /** Предложение из отказа удаления: разбивка держателей и число. */
  offer: O;
  /** Идёт ли удаление: состояние держит мутация вызывающего. */
  pending: boolean;
  onClose: () => void;
  /** После успеха, вместо `onClose`: уровень удалён — со страницы надо уйти. */
  onDone?: () => void;
}) {
  const toast = useSnackbar();
  // «Назад» на телефоне (issue #1418) — «Отмена»; пока идёт удаление, окно не закрывается ничем.
  useHistoryLayer(true, () => {
    if (pending) return false;
    onClose();
    return true;
  });
  const [offer, setOffer] = useState(offered);
  const [input, setInput] = useState('');
  const [settled, setSettled] = useState(false);
  const [changed, setChanged] = useState<string | null>(null);
  // Отказ, после которого выхода больше нет: модуль включили, запись стало держать ядро.
  const [refused, setRefused] = useState<string | null>(null);
  // Ответа по существу нет (сеть, сбой сервера): диалог остаётся, попытку можно повторить.
  const [failed, setFailed] = useState<string | null>(null);

  const matches = countMatches(input, offer);
  const mismatch = mismatchShown(input, offer, settled);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setSettled(true);
    if (!matches || pending) return;
    setFailed(null);
    try {
      const done = await subject.run(offer.references);
      toast.success(subject.done(done.references));
      (onDone ?? onClose)();
    } catch (err) {
      const failure = subject.failure(err, offer);
      if (failure.kind === 'recount') {
        // Подтверждено не то, что удалялось бы: показываем заново, поле пустое — и говорим почему.
        setChanged(failure.note);
        setOffer(failure.offer);
        setInput('');
        setSettled(false);
      } else if (failure.kind === 'final') {
        setRefused(failure.text);
      } else {
        setFailed(failure.text);
      }
    }
  }

  return (
    <Dialog.Root open onOpenChange={o => { if (!o && !pending) onClose(); }}>
      <Dialog.Portal>
        <Dialog.Overlay className={DIALOG_SCRIM} />
        {/* На узком экране — у верха: по центру клавиатура закрыла бы и поле, и кнопки. */}
        <Dialog.Content
          // Идёт удаление, показан его исход или набрано подтверждение — окно держит человека: поверх
          // него обвязка не открывает палитру и не уводит со страницы (см. `DialogHold`).
          data-modal-state={pending ? 'busy' : refused ?? failed ?? changed ? 'result' : input ? 'touched' : 'free'}
          className={`fixed left-1/2 top-4 sm:top-1/2 -translate-x-1/2 sm:-translate-y-1/2 z-50 p-6 w-[calc(100%-2rem)] max-w-md max-h-[calc(100dvh-2rem)] overflow-y-auto ${DIALOG_SURFACE} focus:outline-none`}>
          <Dialog.Title className={`${DIALOG_TITLE} mb-4`}>
            {refused ? 'Удаление невозможно' : subject.title}
          </Dialog.Title>

          {refused ? (
            <>
              <div className="mt-2 flex items-start gap-2.5 rounded-sm bg-error-container px-3 py-2.5 text-body-small text-on-surface" role="alert">
                <Icon icon={Warning} size={18} className="shrink-0 mt-0.5 text-error" aria-hidden />
                <div className="max-h-40 overflow-y-auto whitespace-pre-line min-w-0">{refused}</div>
              </div>
              <div className="flex justify-end mt-4">
                <Button variant="tonal" onClick={onClose}>Понятно</Button>
              </div>
            </>
          ) : (
            <form onSubmit={submit} noValidate>
              {subject.above?.(offer)}
              <Dialog.Description className="text-body-small text-on-surface-variant">{subject.intro}</Dialog.Description>

              <DataGrid density="compact" className="mt-2 max-h-56" columns={HOLDER_COLUMNS} rows={offer.holders}
                footer={c => c.key === 'rows' ? offer.references : 'Всего ссылок'} />

              <ul className="mt-3 text-body-small text-on-surface-variant list-disc pl-4 space-y-1">
                {subject.lines(offer).map(line => <li key={line}>{line}</li>)}
              </ul>

              {changed && (
                <div className="mt-3 rounded-sm bg-warning-container px-3 py-2 text-body-small text-on-surface" role="alert">{changed}</div>
              )}
              {failed && (
                <div className="mt-3 rounded-sm bg-error-container px-3 py-2 text-body-small text-on-surface" role="alert">{failed}</div>
              )}

              <PurgeCountField value={input} disabled={pending} mismatch={mismatch}
                onChange={v => { setInput(v); setSettled(false); }} onSettle={() => setSettled(true)} />

              <div className="flex gap-2 justify-end items-start mt-4">
                <Button type="button" variant="text" className="shrink-0" disabled={pending} data-modal-exit="" onClick={onClose}>Отмена</Button>
                <Button type="submit" variant="filled" danger multiline className="min-w-0"
                  disabled={!matches} loading={pending}>
                  {subject.submit}
                </Button>
              </div>
            </form>
          )}
        </Dialog.Content>
      </Dialog.Portal>
    </Dialog.Root>
  );
}
