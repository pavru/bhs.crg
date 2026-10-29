import { useState, useMemo } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { CheckCircle, Warning } from '@/shared/ui/icons';
import { toggleInSet } from '@/shared/utils/toggleInSet';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { Button } from '@/shared/ui/Button';
import { useSetEmailRecipients } from '@/shared/api/documentSets';
import { apiError } from '@/shared/utils/apiError';

const EMAIL_RE = /^[^@\s]+@[^@\s]+\.[^@\s]+$/;

/** Разбирает произвольные адреса (через запятую/точку с запятой/перенос строки). */
function parseAddresses(text: string): string[] {
  return text.split(/[,;\n]+/).map(s => s.trim()).filter(Boolean);
}

/**
 * Отправка сгенерированных PDF: подписчикам (с учётом наследования) и/или на произвольные адреса
 * контрагентов. Итоговые получатели = отмеченные подписчики + введённые внешние адреса. Универсально
 * для комплекта и отдельного документа (готовность и отправка задаются пропсами). Фоновая задача.
 */
export function EmailSendDialog({ open, onClose, setId, itemName, defaultSubjectHint, defaultBodyHint, ready, notReadyHint, onSend }: {
  open: boolean; onClose: () => void;
  setId: string;                       // комплект — для резолва подписчиков (документ передаёт свой documentSetId)
  itemName: string;
  defaultSubjectHint: string; defaultBodyHint: string;
  ready: boolean; notReadyHint: string;
  onSend: (to: string[], subject?: string, body?: string) => Promise<unknown>;
}) {
  // Список получателей приходит под тем же правом, что и отправка (issue #989). Ошибку ПОКАЗЫВАЕМ:
  // молча пустой список читается как «подписчиков нет», то есть отказ переодевается в результат.
  const { data: recipients = [], error: recipientsError } = useSetEmailRecipients(setId, open);
  const [unchecked, setUnchecked] = useState<Set<string>>(new Set()); // снятые подписчики (по умолчанию все отмечены)
  const [external, setExternal] = useState('');
  const [subject, setSubject] = useState('');
  const [body, setBody] = useState('');
  const [sending, setSending] = useState(false);
  const [queued, setQueued] = useState(false);
  const [error, setError] = useState('');

  const validSubs = recipients.filter(r => r.validEmail);
  const externalList = useMemo(() => parseAddresses(external), [external]);
  const externalValid = externalList.filter(a => EMAIL_RE.test(a));
  const externalBad = externalList.filter(a => !EMAIL_RE.test(a));

  // Итоговые адреса: отмеченные подписчики + валидные внешние (без дублей).
  const to = useMemo(() => {
    const subs = validSubs.filter(r => !unchecked.has(r.userId)).map(r => r.email!);
    return [...new Set([...subs, ...externalValid].map(e => e.toLowerCase()))];
  }, [validSubs, unchecked, externalValid]);

  function toggleSub(userId: string) {
    setUnchecked(prev => toggleInSet(prev, userId));
  }

  async function handleSend() {
    setError('');
    if (externalBad.length > 0) { setError(`Неверные адреса: ${externalBad.join(', ')}`); return; }
    if (to.length === 0) { setError('Не выбран ни один получатель.'); return; }
    setSending(true);
    try { await onSend(to, subject.trim() || undefined, body.trim() || undefined); setQueued(true); }
    catch (e) { setError(apiError(e, 'Не удалось запустить отправку.')); }
    finally { setSending(false); }
  }

  // Закрытие стирает набранное. Компонент смонтирован постоянно, и без сброса текст доживал до
  // следующего открытия: вопрос «изменения будут потеряны» лгал, а свежее окно — в том числе после
  // удачной отправки — сразу считалось окном с правками (ревью PR #1311).
  function leave() {
    onClose();
    setQueued(false); setError('');
    setUnchecked(new Set()); setExternal(''); setSubject(''); setBody('');
  }

  const field = "w-full border border-outline rounded-sm px-3 py-2 text-sm focus:outline-none focus-visible:ring-2 focus-visible:ring-primary bg-surface";

  return (
    <Modal open={open} onOpenChange={o => { if (!o) leave(); }} title={`Отправить: ${itemName}`}
      // Письмо ушло в очередь — терять уже нечего. Подписчиков снимают галками (их окно видит),
      // но признак явный: компонент смонтирован постоянно, и сам он знает точнее.
      isDirty={!queued && (unchecked.size > 0 || !!external.trim() || !!subject.trim() || !!body.trim())}
      busy={sending}
      footer={
        <div className="flex justify-end gap-3">
          <ModalExit>Закрыть</ModalExit>
          <Button variant="filled" onClick={handleSend} loading={sending}
            disabled={queued || !ready || to.length === 0}>
            Отправить ({to.length})
          </Button>
        </div>
      }>
      <div className="space-y-4">
        {!ready && (
          <p className="flex items-start gap-2 text-sm text-on-warning-container bg-warning-container rounded-sm p-3">
            <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" /> {notReadyHint}
          </p>
        )}

        {recipientsError && (
          <p className="flex items-start gap-2 text-sm text-on-warning-container bg-warning-container rounded-sm p-3">
            <Icon icon={Warning} size={18} className="shrink-0 mt-0.5" />
            Список подписчиков не загрузился — отправляйте по адресам, введённым вручную.
            Пустой список здесь означал бы «подписчиков нет», а это не так.
          </p>
        )}

        {recipients.length > 0 && (
          <div>
            <p className="text-xs font-medium text-on-surface-variant mb-1">Подписчики (с учётом наследования)</p>
            <div className="rounded-sm border border-outline-variant divide-y divide-surface-container max-h-40 overflow-y-auto">
              {recipients.map(r => (
                <label key={r.userId} className={`flex items-center gap-2 px-2.5 py-1.5 text-sm ${r.validEmail ? 'cursor-pointer state-layer' : 'opacity-60'}`}>
                  <input type="checkbox" disabled={!r.validEmail} checked={r.validEmail && !unchecked.has(r.userId)} onChange={() => toggleSub(r.userId)} />
                  <span className="text-on-surface flex-1 min-w-0 truncate">{r.displayName}</span>
                  <span className="text-xs text-on-surface-variant min-w-0 truncate">{r.email || '—'}</span>
                  {!r.validEmail && <Icon icon={Warning} size={18} className="text-warning shrink-0" />}
                </label>
              ))}
            </div>
          </div>
        )}

        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">Внешние адреса (контрагенты)</label>
          <textarea className={field + ' min-h-16 resize-y'} value={external} onChange={e => { setExternal(e.target.value); setError(''); }}
            placeholder="Через запятую: client@zakazchik.ru, tn@stroynadzor.ru" />
          {externalBad.length > 0 && <p className="text-caption text-error mt-0.5">Неверные: {externalBad.join(', ')}</p>}
        </div>

        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">Тема (необязательно)</label>
          <input className={field} value={subject} onChange={e => setSubject(e.target.value)} placeholder={defaultSubjectHint} />
        </div>
        <div>
          <label className="block text-xs font-medium text-on-surface-variant mb-1">Текст (необязательно)</label>
          <textarea className={field + ' min-h-24 resize-y'} value={body} onChange={e => setBody(e.target.value)} placeholder={defaultBodyHint} />
        </div>

        {error && <p className="text-sm text-error">{error}</p>}
        {queued && (
          <p className="text-sm text-success flex items-center gap-1.5">
            <Icon icon={CheckCircle} size={18} /> Отправка запущена — ход виден на кнопке активности в шапке.
          </p>
        )}
      </div>
    </Modal>
  );
}
