import { useState } from 'react';
import { useAttachInvoiceScan } from '@/shared/api/invoices';
import { acceptOf, sizeLimitWords, useFileKinds, wordsOf } from '@/shared/api/fileKinds';
import { FileDropZone } from '@/shared/ui/FileDropZone';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { WindowStep } from '@/shared/ui/WindowStep';
import { attachKinds, precheck } from './scanPrecheck';

/**
 * Окно «Приложить файл…» и «Заменить файл…» (costs-invoice-screen-14, D6; лист «Ф», окно 8). Файл к счёту —
 * только явным действием: бросок на страницу заводит НОВЫЙ черновик, а приложить или заменить можно только
 * здесь, на площадке (`FileDropZone`, [UI-89]) или кнопкой выбора.
 *
 * <p>Ничего не пишется до «Приложить» / «Заменить». У замены второй шаг — `WindowStep` (шаг окна вместо окна
 * поверх окна, #1384) с вопросом «Прежний файл будет удалён без возврата»: замена удаляет первичку, и до
 * этого окна она шла без вопроса. Запись — `POST …/{id}/scan` с `If-Match`, как раньше.</p>
 *
 * <p>Площадка судит файл тем же `precheck`, что пакет и бросок на окно: одни слова отказа (#1265), — но с
 * охватом приложения ({@link attachKinds}): показываемое, хоть и не распознаваемое, приложить можно. Своего
 * захвата броска у неё нет — в модальном слое её обработчик отменяет событие раньше стража окна, а захват
 * страницы при открытом слое молчит: бросок мимо площадки, пока окно открыто, ничего не делает.</p>
 */
export function InvoiceFileDialog({ invoiceId, version, number, current, onClose }: {
  invoiceId: string;
  /** Версия счёта для `If-Match`. */
  version: string;
  number: string | null;
  /** Имя прежнего файла; `null` — файла нет, и окно приложения, а не замены. */
  current: string | null;
  onClose: () => void;
}) {
  const kinds = useFileKinds();
  const attach = useAttachInvoiceScan();
  const toast = useSnackbar();
  const [file, setFile] = useState<File | null>(null);
  const [asking, setAsking] = useState(false);
  const replace = current !== null;
  const of = number ? `счёта ${number}` : 'счёта без номера';
  const offered = attachKinds(kinds.data);
  const hint = [offered.length ? wordsOf(offered) : null, kinds.data ? `до ${sizeLimitWords(kinds.data)}` : null]
    .filter(Boolean).join(' · ');

  async function send() {
    if (!file) return;
    try {
      await attach.mutateAsync({ id: invoiceId, seen: version, file });
      onClose();
    } catch (e) {
      setAsking(false);
      toast.apiError(e, replace ? 'Файл не заменён' : 'Файл не приложен');
    }
  }

  const busy = attach.isPending;
  return (
    // Терять нечего: выбранный, но не отправленный файл выбирается заново одним действием; идущий запрос держит `busy`.
    <Modal open nothingToLose onOpenChange={open => { if (!open) onClose(); }} busy={busy}
      title={replace ? `Заменить файл ${of}` : `Приложить файл к ${of.replace(/^счёта/, 'счёту')}`}
      footer={<>
        <ModalExit />
        <ModalPrimary disabled={!file} loading={busy && !asking} onClick={() => (replace ? setAsking(true) : void send())}>
          {replace ? 'Заменить' : 'Приложить'}
        </ModalPrimary>
      </>}>
      <div className="space-y-3">
        {replace && <p className="text-body-medium text-on-surface-variant break-all">Сейчас: {current}</p>}
        <FileDropZone label="Файл счёта" onFile={setFile} check={f => precheck(f, kinds.data, offered)}
          accept={acceptOf(offered) || undefined} hint={hint || undefined} file={file}
          prompt="Перетащите файл сюда" pickLabel="Выбрать файл…"
          busy={busy && file ? `Загружаем «${file.name}»` : undefined} />
        <p className="text-body-medium text-on-surface-variant">
          Файл распознаем: распознанное ляжет только в пустые поля, набранное руками не изменится. Бросок мимо площадки,
          пока окно открыто, ничего не делает.
        </p>
      </div>
      {asking && file && current !== null && (
        <WindowStep title="Заменить файл?" onBack={() => setAsking(false)} busy={busy}
          footer={<>
            <ModalExit>Отмена</ModalExit>
            <ModalPrimary loading={busy} onClick={() => void send()}>Заменить файл</ModalPrimary>
          </>}>
          <p className="text-body-medium text-on-surface">
            <span className="font-medium">Прежний файл будет удалён без возврата</span> — «{current}». Новый, «{file.name}»,
            распознаем заново: распознанное ляжет только в пустые поля, набранное не изменится.
          </p>
        </WindowStep>
      )}
    </Modal>
  );
}
