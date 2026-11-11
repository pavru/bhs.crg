import { useState } from 'react';
import { Link } from 'react-router';
import { Banner } from '@/shared/ui/Banner';
import { Modal, ModalExit, ModalPrimary } from '@/shared/ui/Modal';
import { TextField } from '@/shared/ui/TextField';
import { ListOption } from '@/shared/ui/ListOption';
import { useKeyScroll } from '@/shared/ui/useKeyScroll';
import { useCan } from '@/shared/api/access';
import { apiError } from '@/shared/utils/apiError';
import { useAccountCandidates, type AccountCandidate } from '@/shared/api/employees';
import { candidateBlocked, surnameOf } from './employeeAccount';

/**
 * Окно выбора учётной записи для сотрудника (B18): поиск сразу заполнен фамилией, занятая учётная запись
 * не выбирается и говорит, с кем связана. Одно окно на оба места — карточку («Связать») и клетку режима
 * «Сопоставить»: два поиска кандидатов с разными правилами разошлись бы.
 *
 * <p>`onConfirm` отказом — слова сервера в окне (занятость с именем, «карточку тем временем изменили»),
 * окно остаётся: выбрать можно снова.</p>
 */
export function AccountPickerDialog({ employee, confirmLabel, onConfirm, onClose }: {
  employee: { id: string; name: string };
  confirmLabel: string;
  onConfirm: (c: AccountCandidate) => Promise<void> | void;
  onClose: () => void;
}) {
  const [q, setQ] = useState(() => surnameOf(employee.name));
  const [active, setActive] = useState(0);
  const [chosen, setChosen] = useState<AccountCandidate | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const { data, isFetching } = useAccountCandidates(q.trim(), true);
  const items = data?.items ?? [];
  const can = useCan();
  const byKeys = useKeyScroll(items.length ? `acc-opt-${active}` : undefined);

  const selectable = (c: AccountCandidate) => !candidateBlocked(c, employee.id);
  function move(step: number) {
    for (let i = active + step; i >= 0 && i < items.length; i += step)
      if (selectable(items[i])) { byKeys(); setActive(i); return; }
  }
  function onKey(e: React.KeyboardEvent) {
    if (e.key === 'ArrowDown') { e.preventDefault(); move(1); }
    else if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); }
    else if (e.key === 'Enter') { e.preventDefault(); const c = items[active]; if (c && selectable(c)) setChosen(c); }
  }

  async function confirm() {
    if (!chosen) return;
    setError(null);
    setBusy(true);
    try { await onConfirm(chosen); onClose(); }
    catch (e) { setError(apiError(e, 'Не удалось связать.')); }
    finally { setBusy(false); }
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title={`Связать: ${employee.name}`} busy={busy} nothingToLose
      footer={<><ModalExit /><ModalPrimary onClick={() => void confirm()} disabled={!chosen} loading={busy}>{confirmLabel}</ModalPrimary></>}>
      <div className="space-y-4">
        <TextField label="Поиск учётной записи" value={q} autoFocus onKeyDown={onKey}
          onChange={e => { setQ(e.target.value); setActive(0); }}
          role="combobox" aria-expanded aria-controls={items.length ? 'acc-list' : undefined}
          aria-activedescendant={items.length ? `acc-opt-${active}` : undefined} />
        {error && <Banner tone="error" className="rounded-md border-0">{error}</Banner>}
        {items.length === 0 ? (
          <p className="text-body-medium text-on-surface-variant py-2">
            {isFetching ? 'Ищем…' : 'Нет учётной записи с таким именем — заведите её в «Пользователях».'}
            {!isFetching && can.permission('core.users.manage') && (
              <> <Link className="text-primary underline" to="/users">Открыть «Пользователей»</Link></>
            )}
          </p>
        ) : (
          <div id="acc-list" role="listbox" aria-label="Учётные записи" className="-mx-4 max-h-72 overflow-y-auto">
            {items.map((c, i) => {
              const blocked = candidateBlocked(c, employee.id);
              return (
                <ListOption key={c.userId} id={`acc-opt-${i}`} active={i === active} current={chosen?.userId === c.userId}
                  disabled={!!blocked} disabledNote="занята" headline={c.name}
                  supporting={blocked ? `${c.login} · ${blocked}` : c.login}
                  data-testid="account-candidate"
                  onHover={() => setActive(i)} onChoose={() => setChosen(c)} />
              );
            })}
          </div>
        )}
        {data?.more && <p className="text-body-small text-on-surface-variant">Показаны первые — уточните поиск.</p>}
      </div>
    </Modal>
  );
}
