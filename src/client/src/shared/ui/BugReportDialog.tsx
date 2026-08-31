import { useEffect, useRef, useState } from 'react';
import { AlertTriangle, ChevronDown, ChevronRight, Loader2, Monitor, Paperclip, X } from 'lucide-react';
import { Modal } from './Modal';
import { Button } from './Button';
import { TextAreaField } from './TextAreaField';
import { useToast } from './Toast';
import { useAppVersion } from '@/shared/api/version';
import { uploadAttachment } from '@/shared/api/attachments';
import { collectBugReportTech, submitBugReport } from '@/shared/api/bugReports';
import { apiError } from '@/shared/utils/apiError';
import { canCaptureScreen, captureScreenshotFile } from './screenCapture';

/** С чем форму открыли: контекстная дверь приносит то, чего пользователь не перепечатает. */
export interface BugReportPrefill {
  /** «Что получили» — текст ошибки с идентификатором запроса. */
  received?: string;
  /** Стек сбоя интерфейса (из ErrorBoundary): после перезагрузки страницы его уже не собрать. */
  stack?: string;
  /** Откуда открыли: 'rail' | 'boundary' | 'toast' — попадает в техблок. */
  origin?: string;
}

/**
 * Форма «Сообщить об ошибке» (issue #834).
 *
 * Модалка, а не маршрут: сообщают, не уходя с экрана и не теряя несохранённое — а уходить с экрана
 * пришлось бы ровно в тот момент, когда на нём видно то, о чём сообщают.
 *
 * Обязательное поле здесь одно. Три обязательных поля — это анкета, и с неё уйдёт тот, кто хотел
 * сказать «кнопка не нажимается»; остальное подставляет техблок, который человеку и не собрать.
 */
export function BugReportDialog({ open, prefill, onClose }: {
  open: boolean;
  prefill: BugReportPrefill;
  onClose: () => void;
}) {
  const toast = useToast();
  const { data: version } = useAppVersion();
  // Начальное значение — из предзаполнения двери, через которую пришли. Синхронизировать
  // эффектом нечего: провайдер пересоздаёт форму при каждом открытии (ключ = номер открытия).
  const [message, setMessage] = useState(() => initialMessage(prefill));
  const [shot, setShot] = useState<{ file: File; url: string } | null>(null);
  const [capturing, setCapturing] = useState(false);
  const [sending, setSending] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [contextOpen, setContextOpen] = useState(false);
  const fileRef = useRef<HTMLInputElement>(null);

  useEffect(() => () => { if (shot) URL.revokeObjectURL(shot.url); }, [shot]);

  function attach(file: File) {
    if (shot) URL.revokeObjectURL(shot.url);
    setShot({ file, url: URL.createObjectURL(file) });
  }

  function removeShot() {
    if (shot) URL.revokeObjectURL(shot.url);
    setShot(null);
  }

  /**
   * Снимок средствами браузера. Форму на время съёмки прячем (`capturing`): иначе на снимке
   * оказалась бы она сама, закрывая собой то, о чём сообщают. Компонент при этом остаётся
   * смонтированным — набранный текст переживает съёмку.
   */
  async function capture() {
    setError(null);
    setCapturing(true);
    try {
      attach(await captureScreenshotFile());
    } catch (e) {
      // Отказ в выборе окна — не ошибка: человек передумал. Молчим, а не показываем красное.
      if (!(e instanceof DOMException && (e.name === 'NotAllowedError' || e.name === 'AbortError')))
        setError('Не удалось снять экран. Приложите файл — кнопка рядом.');
    } finally {
      setCapturing(false);
    }
  }

  async function send() {
    const text = message.trim();
    if (!text) { setError('Опишите, что произошло.'); return; }

    setSending(true);
    setError(null);
    try {
      const uploaded = shot ? await uploadAttachment(shot.file) : null;
      await submitBugReport({
        message: text,
        tech: {
          version: version?.version,
          commit: version?.commit || undefined,
          ...collectBugReportTech({ stack: prefill.stack, origin: prefill.origin }),
        },
        screenshotBlobPath: uploaded?.blobPath ?? null,
      });
      setMessage('');
      removeShot();
      onClose();
      // Результат не виден на экране, с которого сообщали, — ровно случай для тоста.
      toast.success('Передано администратору.');
    } catch (e) {
      setError(apiError(e, 'Не удалось отправить сообщение.'));
    } finally {
      setSending(false);
    }
  }

  const canCapture = canCaptureScreen();

  return (
    <Modal
      open={open && !capturing}
      onOpenChange={o => { if (!o) onClose(); }}
      title="Сообщить об ошибке"
      wide
      isDirty={message.trim().length > 0 && !sending}
      footer={
        <div className="flex items-center justify-end gap-2">
          <Button variant="text" onClick={onClose} disabled={sending}>Отмена</Button>
          <Button variant="filled" onClick={() => void send()} disabled={sending}>
            {sending && <Loader2 size={15} className="animate-spin" />} Отправить
          </Button>
        </div>
      }
    >
      <div className="space-y-4">
        <TextAreaField
          label="Что произошло"
          required
          rows={6}
          autoFocus
          value={message}
          onChange={e => setMessage(e.target.value)}
          placeholder="что делали → что ожидали → что получили"
          hint="Сообщение уходит администратору. Он же решает, что из него передать разработчикам."
        />

        {/* ── Снимок экрана ─────────────────────────────────────────────── */}
        <div className="space-y-2">
          <div className="flex flex-wrap items-center gap-2">
            <Button variant="outlined" size="sm" onClick={() => fileRef.current?.click()} disabled={sending}>
              <Paperclip size={14} /> Загрузить файл
            </Button>
            {/* Кнопки съёмки нет там, где API недоступно (не secure context — например http://
                внутри сети). Показать её сломанной значило бы обещать несуществующее. */}
            {canCapture && (
              <Button variant="outlined" size="sm" onClick={() => void capture()} disabled={sending}>
                <Monitor size={14} /> Снять экран
              </Button>
            )}
            <input
              ref={fileRef} type="file" accept="image/png,image/jpeg,image/webp,image/gif"
              className="hidden"
              onChange={e => { const f = e.target.files?.[0]; if (f) attach(f); e.target.value = ''; }}
            />
          </div>

          {shot && (
            <div className="rounded-xl border border-stroke p-2 space-y-2">
              {/* Честность не предупреждением, а показом: человек видит ровно то, что уйдёт. */}
              <img src={shot.url} alt="Снимок экрана" className="max-h-56 w-auto rounded-lg border border-stroke" />
              <div className="flex items-center justify-between gap-2">
                <p className="text-xs text-fg3">
                  Уйдёт администратору вместе с сообщением. Проверьте, нет ли лишнего.
                  {' '}В GitHub снимок не передаётся.
                </p>
                <Button variant="text" size="sm" onClick={removeShot}>
                  <X size={14} /> Убрать
                </Button>
              </div>
            </div>
          )}
        </div>

        {/* ── Что уйдёт вместе с сообщением ─────────────────────────────── */}
        <div className="rounded-xl border border-stroke">
          <button
            type="button"
            onClick={() => setContextOpen(o => !o)}
            className="w-full flex items-center gap-1.5 px-3 py-2 text-left text-xs text-fg3 hover:text-fg2 transition-colors"
          >
            {contextOpen ? <ChevronDown size={13} /> : <ChevronRight size={13} />}
            Вместе с сообщением уйдёт: версия · экран · браузер · последние ошибки API
          </button>
          {contextOpen && <ContextFacts prefill={prefill} version={version?.version} commit={version?.commit} />}
        </div>

        {error && (
          <p className="flex items-start gap-1.5 text-sm text-danger">
            <AlertTriangle size={14} className="shrink-0 mt-0.5" /> {error}
          </p>
        )}
      </div>
    </Modal>
  );
}

/** Список фактов — тем же составом, что уйдёт на сервер. Галочек «не отправлять X» нет намеренно:
 *  убирать частное — работа администратора, и делается она над текстом, а не над техблоком. */
function ContextFacts({ prefill, version, commit }: {
  prefill: BugReportPrefill; version?: string; commit?: string;
}) {
  const tech = collectBugReportTech({ stack: prefill.stack, origin: prefill.origin });
  return (
    <ul className="px-3 pb-3 space-y-1 text-xs text-fg3">
      <li>Версия: {version ?? '—'}{commit ? ` · сборка ${commit}` : ''}</li>
      <li>Экран: {tech.route}</li>
      <li>Окно: {tech.viewport}</li>
      <li className="break-all">Браузер: {tech.userAgent}</li>
      <li>
        Ошибки API: {tech.apiErrors?.length ?? 0}
        {(tech.apiErrors?.length ?? 0) > 0 && (
          <ul className="mt-1 ml-3 space-y-0.5 font-mono text-[11px]">
            {tech.apiErrors!.map((e, i) => (
              <li key={i} className="break-all">
                {e.at} {e.method} {e.url} → {e.status || 'нет ответа'}
                {e.traceId ? ` · ${e.traceId}` : ''}
              </li>
            ))}
          </ul>
        )}
      </li>
      {prefill.stack && <li>Стек сбоя интерфейса — приложен</li>}
      <li className="text-fg4">Тел ответов и содержимого форм здесь нет.</li>
    </ul>
  );
}

/** Заготовка сообщения: контекстная дверь заполняет «что получили», остальное — за человеком. */
function initialMessage(prefill: BugReportPrefill): string {
  if (!prefill.received) return '';
  return `Что делали: \nЧто ожидали: \nЧто получили: ${prefill.received}`;
}
