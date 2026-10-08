import { useState } from 'react';
import { Link } from 'react-router';
import * as Popover from '@radix-ui/react-popover';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { TextField } from '@/shared/ui/TextField';
import { ArchivedMark } from '@/shared/ui/ArchivedMark';
import { useToast } from '@/shared/ui/Toast';
import { useCan } from '@/shared/api/access';
import { apiError } from '@/shared/utils/apiError';
import {
  useCreatePartyOrganization, type InvoiceParty, type InvoicePartySide,
} from '@/shared/api/invoiceRecognition';
import { inheritsFrom, nameOf, partyLine, partyWhy, pickOrder, scanParty } from './recognition';

const NAME_LIMIT = 512;

/**
 * Сторона счёта из скана — под полем «Поставщик» / «Плательщик» (issue #1077).
 *
 * <p>Две строчки: что справочник говорит об организации из скана (и действие), и серым — что именно
 * прочитано. У самого поля, а не отдельным блоком: предложение стоит там, куда оно относится.</p>
 *
 * <p>⚠️ «Завести» есть ТОЛЬКО у `absent`. `unknown` значит «часть справочника не прочитана», и
 * предложить по нему заведение — значит предложить дубль; неизвестное клиенту состояние — тоже не
 * «нет».</p>
 *
 * @param chosen организация, стоящая в поле сейчас (с несохранённой правкой).
 * @param offered под полем уже стоит предложение «В скане: … · Взять» — второе такое же не нужно.
 * @param onChoose положить организацию в поле. В счёт не пишет: сохраняет человек.
 */
export function InvoicePartyNote({ invoiceId, side, party, chosen, offered, onChoose, onRetry }: {
  invoiceId: string; side: InvoicePartySide; party: InvoiceParty;
  chosen: string | null; offered: boolean;
  onChoose: (entryId: string) => void;
  /** Перечитать состояние распознавания — стороны сопоставляются при каждом чтении. */
  onRetry: () => void;
}) {
  const canCreate = useCan().permission('costs.organization.create');
  const [creating, setCreating] = useState(false);
  const [created, setCreated] = useState<string | null>(null);
  const line = partyLine(party, chosen, canCreate);
  const read = scanParty(party);
  const shown = line && !(line.action === 'take' && offered) ? line : null;

  return (
    <div className="mt-0.5 text-xs min-w-0">
      {created && created === chosen && (
        <p className="text-fg2">
          Заведена и поставлена в поле — сохраните счёт.{' '}
          <Link to="/common-data" target="_blank" className="text-brand hover:underline">Дополнить в каталоге ↗</Link>
        </p>
      )}
      {shown && (
        <p className={`flex items-baseline gap-1 min-w-0 ${shown.tone === 'warning' ? 'text-warning' : 'text-fg3'}`}>
          <span className="truncate" title={shown.text}>{shown.text}</span>
          {shown.action === 'take' && party.match && (
            <Act onClick={() => onChoose(party.match!)}>Взять</Act>
          )}
          {shown.action === 'create' && <Act onClick={() => setCreating(true)}>Завести</Act>}
          {(shown.action === 'pick' || shown.action === 'details') && (
            <Details party={party} pick={shown.action === 'pick'} canCreate={canCreate}
              onChoose={onChoose} onRetry={onRetry} />
          )}
        </p>
      )}
      {read && <p className="text-fg4 truncate" title={`В скане: ${read}`}>в скане: {read}</p>}
      {creating && (
        <CreateDialog invoiceId={invoiceId} side={side} party={party} onClose={() => setCreating(false)}
          onDone={id => { setCreated(id); onChoose(id); setCreating(false); }} />
      )}
    </div>
  );
}

function Act({ onClick, children }: { onClick: () => void; children: React.ReactNode }) {
  return (
    <button type="button" className="shrink-0 text-brand hover:underline" onClick={onClick}>{children}</button>
  );
}

/** Раскрытие: причина словами сервера, кандидаты на выбор, записи с непрочитанным ИНН. */
function Details({ party, pick, canCreate, onChoose, onRetry }: {
  party: InvoiceParty; pick: boolean; canCreate: boolean;
  onChoose: (entryId: string) => void; onRetry: () => void;
}) {
  const [open, setOpen] = useState(false);
  const candidates = pickOrder(party.candidates);

  return (
    <Popover.Root open={open} onOpenChange={setOpen}>
      <Popover.Trigger asChild>
        <button type="button" className="shrink-0 text-brand hover:underline">{pick ? 'Выбрать' : 'подробнее'}</button>
      </Popover.Trigger>
      <Popover.Portal>
        <Popover.Content align="start" sideOffset={6}
          className="z-50 w-[360px] max-w-[90vw] rounded-lg bg-surface border border-stroke p-3 text-xs text-fg2 space-y-2 focus:outline-none"
          style={{ boxShadow: 'var(--f-shadow16)' }}>
          <p>{partyWhy(party, canCreate)}</p>
          {candidates.length > 0 && (
            <ul className="space-y-1">
              {candidates.map(c => {
                const role = inheritsFrom(c, party.candidates);
                const body = (
                  <>
                    <span className="flex items-center gap-1 text-fg1">
                      <span className="truncate">{c.name ?? 'запись без названия'}</span>
                      {c.archived && <ArchivedMark words={false} />}
                    </span>
                    {role && <span className="block text-fg3">{role}</span>}
                    {c.archived && <span className="block text-fg3">в архиве — в новый счёт не выбирается</span>}
                  </>
                );
                // Выбирается только действующая и только там, где выбор и есть действие: у `archived`
                // и `unknown` список — объяснение, а не меню.
                return (
                  <li key={c.id}>
                    {pick && !c.archived
                      ? (
                        <button type="button" className="w-full text-left rounded px-2 py-1 hover:bg-surface2"
                          onClick={() => { onChoose(c.id); setOpen(false); }}>{body}</button>
                      )
                      : <div className="px-2 py-1">{body}</div>}
                  </li>
                );
              })}
            </ul>
          )}
          {party.unreadable.length > 0 && (
            <div>
              <p className="text-fg3">ИНН не прочитан у записей:</p>
              <ul className="list-disc pl-4">
                {party.unreadable.map(u => <li key={u.id}>{u.name ?? 'запись без названия'}</li>)}
              </ul>
            </div>
          )}
          {party.state === 'unavailable' && (
            <Button size="sm" variant="outlined" onClick={() => { onRetry(); setOpen(false); }}>Повторить</Button>
          )}
        </Popover.Content>
      </Popover.Portal>
    </Popover.Root>
  );
}

/**
 * Диалог «Завести организацию»: название правится, ИНН — из скана и не правится.
 *
 * <p>Отказ сервера остаётся В ДИАЛОГЕ, под названием: тост ушёл бы, а причина нужна, пока человек
 * правит название. Набранное при отказе цело.</p>
 */
function CreateDialog({ invoiceId, side, party, onClose, onDone }: {
  invoiceId: string; side: InvoicePartySide; party: InvoiceParty;
  onClose: () => void; onDone: (entryId: string) => void;
}) {
  const [name, setName] = useState(party.name ?? '');
  const [refusal, setRefusal] = useState<string | null>(null);
  const create = useCreatePartyOrganization();
  const toast = useToast();
  const tooLong = name.trim().length > NAME_LIMIT;

  async function submit() {
    setRefusal(null);
    try {
      const result = await create.mutateAsync({ id: invoiceId, side, name: name.trim() || undefined });
      if (result.created) {
        toast.success('Организация заведена: название и ИНН');
        onDone(result.created);
      } else if (result.party.state === 'matched' && result.party.match) {
        // Её тем временем завёл кто-то ещё — заводить не пришлось, а в поле она ложится так же.
        toast.success(`Организацию тем временем уже завели — заводить не пришлось: ${nameOf(result.party, result.party.match)}`);
        onDone(result.party.match);
      } else {
        // Теперь записей несколько или она в архиве: поле покажет новое состояние само.
        onClose();
      }
    } catch (e) {
      setRefusal(apiError(e, 'сервер отказал без причины'));
    }
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Завести организацию"
      isDirty={name.trim() !== (party.name ?? '').trim()}>
      <div className="space-y-3">
        <TextField label="Название" value={name} onChange={e => setName(e.target.value)} autoFocus
          invalid={tooLong}
          error={tooLong ? `Длиннее ${NAME_LIMIT} знаков (${name.trim().length}) — сократите до названия организации` : undefined} />
        <p className="text-xs text-fg2">ИНН: <span className="text-fg1">{party.taxId}</span> — из скана, не правится.</p>
        <p className="text-xs text-fg3">
          В справочник попадут только название и ИНН. Остальные реквизиты дополните в каталоге.
        </p>
        {refusal && (
          <div role="alert" className="rounded-lg border border-danger-border bg-danger-subtle px-3 py-2 text-xs text-danger">
            <p>Организация не заведена: {refusal}</p>
            <Link to="/common-data" target="_blank" className="underline hover:no-underline">
              Открыть справочник организаций ↗
            </Link>
          </div>
        )}
        <div className="flex justify-end gap-2">
          <Button variant="text" onClick={onClose}>Отмена</Button>
          <Button variant="filled" loading={create.isPending} disabled={tooLong || !name.trim()} onClick={() => void submit()}>
            Завести
          </Button>
        </div>
      </div>
    </Modal>
  );
}
