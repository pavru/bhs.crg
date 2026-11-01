import { useMemo, useRef, useState } from 'react';
import { Icon } from '@/shared/ui/Icon';
import { Add, Print } from '@/shared/ui/icons';
import { Button } from '@/shared/ui/Button';
import { Modal, ModalExit } from '@/shared/ui/Modal';
import { TextField } from '@/shared/ui/TextField';
import { Tooltip } from '@/shared/ui/Tooltip';
import { useSnackbar } from '@/shared/ui/Snackbar';
import { ListDetailShell } from '@/shared/ui/ListDetailShell';
import { LeaveGuardDialog } from '@/shared/ui/LeaveGuardDialog';
import { useLeaveGuard } from '@/shared/ui/NavigationGuard';
import { useDirtyGuard } from '@/shared/ui/useDirtyGuard';
import { useListDetailView } from '@/shared/ui/listDetailView';
import { useRememberedSelection } from '@/shared/hooks/useRememberedSelection';
import { refusalIssues } from '@/shared/api/refusals';
import { useCreatePrintProfile, usePrintProfiles, type PrintProfile } from '@/shared/api/printProfiles';
import { NAME_MAX, isModified, nameIssue, profileSummary } from './printProfileRules';
import { PrintProfileDetail, type EditState } from './PrintProfileDetail';
import { SheetSketch } from './SheetSketch';

/**
 * «Профили печати» (issue #1455): как раскладывать на страницы книги Excel без своей раскладки — для
 * вида для чтения у счетов. Профиль по умолчанию первым, остальные — по названию.
 *
 * <p>Строка списка несёт набросок листа: бумага, ориентация и поля видны, не открывая профиль. Поиска
 * нет: профилей единицы.</p>
 *
 * <p>Несохранённую правку карточки страница не теряет молча — ни переходом к другому профилю, ни
 * уходом с экрана: оба спрашивают, как у типов полей.</p>
 */

const SELECTION_KEYS = ['profile'] as const;
const LAST_KEY = 'print-profiles-last';
const EMPTY: PrintProfile[] = [];
const CLEAN: EditState = { dirty: false, saving: false };

export function PrintProfilesPage() {
  const { data: loaded, isLoading, isError } = usePrintProfiles();
  const profiles = useMemo(() => [...(loaded ?? EMPTY)].sort((a, b) =>
    Number(b.isSystem) - Number(a.isSystem) || a.name.localeCompare(b.name, 'ru')), [loaded]);
  const { values, remember } = useRememberedSelection(LAST_KEY, SELECTION_KEYS);
  const setSelectedId = (id: string | null) => remember({ profile: id ?? '' });
  const ld = useListDetailView();
  const [createOpen, setCreateOpen] = useState(false);
  // Удалённый или чужой id — молча первый профиль: он есть всегда (по умолчанию не удаляется).
  const selected = profiles.find(p => p.id === values.profile) ?? profiles[0];
  const base = profiles.find(p => p.isSystem);

  // Карточка сообщает, есть ли у неё несохранённое, и даёт своё сохранение — для «Сохранить и перейти».
  const [edit, setEdit] = useState<EditState>(CLEAN);
  const saveRef = useRef<() => Promise<void>>(() => Promise.resolve());
  const saveAll = () => saveRef.current();
  const { request, dialogProps } = useDirtyGuard<string>({
    isDirty: edit.dirty, saving: edit.saving, saveAll, onCommit: setSelectedId,
  });
  const open = (id: string) => { ld.showDetail(); if (id !== selected?.id) request(id); };

  const [routeLeave, setRouteLeave] = useState<(() => void) | null>(null);
  useLeaveGuard(edit.dirty, proceed => setRouteLeave(() => proceed));

  return (
    <>
      <ListDetailShell view={ld}
        title="Профили печати"
        subtitle="Как раскладывать на страницы файлы Excel, в которых автор не задал раскладку, — для вида для чтения у счетов"
        icon={<Icon icon={Print} size={20} className="text-on-surface-variant" />}
        actions={
          <Button variant="filled" icon={<Icon icon={Add} size={18} />} onClick={() => setCreateOpen(true)} disabled={!loaded}>
            Добавить профиль
          </Button>
        }
        overlay={isLoading ? <div className="flex-1 flex items-center justify-center text-on-surface-variant text-body-medium">Загрузка…</div> : undefined}
        nav={
          <div className="flex-1 min-h-0 overflow-y-auto p-2 space-y-0.5" data-testid="print-profiles-list">
            {profiles.map(p => (
              <ProfileRow key={p.id} profile={p} active={selected?.id === p.id} onClick={() => open(p.id)} />
            ))}
            {isError && <p className="text-body-medium text-on-surface-variant px-3 py-2">Не удалось получить список профилей</p>}
          </div>
        }
        detail={selected
          ? <PrintProfileDetail key={selected.id} profile={selected}
              otherNames={profiles.filter(p => p.id !== selected.id).map(p => p.name)}
              onDeleted={() => setSelectedId(null)} onEditState={setEdit} saveRef={saveRef} />
          : <div className="flex-1 flex items-center justify-center text-on-surface-variant text-body-medium">Выберите профиль</div>} />

      <LeaveGuardDialog {...dialogProps} />
      <LeaveGuardDialog
        open={routeLeave !== null} saving={edit.saving}
        onCancel={() => setRouteLeave(null)}
        onDiscard={() => { const p = routeLeave; setRouteLeave(null); p?.(); }}
        onSave={async () => {
          try { await saveAll(); const p = routeLeave; setRouteLeave(null); p?.(); }
          catch { setRouteLeave(null); /* ошибка показана в карточке */ }
        }} />

      <Modal open={createOpen} onOpenChange={setCreateOpen} title="Новый профиль печати">
        <CreateForm names={profiles.map(p => p.name)} base={base}
          // Созданный открывается через тот же вопрос: правка в карточке не пропадает и отсюда.
          onCreated={id => { setCreateOpen(false); open(id); }} />
      </Modal>
    </>
  );
}

function ProfileRow({ profile, active, onClick }: { profile: PrintProfile; active: boolean; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick} aria-current={active || undefined}
      className={`state-layer w-full flex items-center gap-3 text-left px-3 py-2 rounded-md ${
        active ? 'bg-primary-container text-on-primary-container' : 'text-on-surface-variant'}`}>
      <SheetSketch paper={profile.paper} orientation={profile.orientation} fit={profile.fit} margins={profile.margins} />
      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-1.5">
          <span className="text-body-medium truncate min-w-0 flex-1" title={profile.name}>{profile.name}</span>
          {isModified(profile) && (
            <Tooltip content="Изменён"><span className="w-1.5 h-1.5 rounded-full bg-primary shrink-0" /></Tooltip>
          )}
        </span>
        <span className="block text-body-small text-on-surface-variant truncate">{profileSummary(profile)}</span>
      </span>
    </button>
  );
}

/**
 * Новый профиль — одно название: параметры берутся из профиля по умолчанию и правятся в карточке.
 * Так новый профиль с первой секунды годен, и форма создания не повторяет карточку.
 *
 * <p>Профиля по умолчанию в списке нет (сервер его не отдал) — брать параметры неоткуда: «Создать»
 * недоступна, и причина написана, а не молчаливое нажатие без ответа.</p>
 */
function CreateForm({ names, base, onCreated }: {
  names: string[]; base: PrintProfile | undefined; onCreated: (id: string) => void;
}) {
  const toast = useSnackbar();
  const create = useCreatePrintProfile();
  const [name, setName] = useState('');
  const [error, setError] = useState<string | undefined>();

  async function save() {
    if (!base) return;
    const issue = nameIssue(name, names);
    if (issue) { setError(issue); return; }
    try {
      const created = await create.mutateAsync({
        name: name.trim(), paper: base.paper, orientation: base.orientation, fit: base.fit, margins: base.margins,
      });
      toast.success('Профиль сохранён');
      onCreated(created.id);
    } catch (e) {
      const byPath = refusalIssues(e);
      if (byPath.name) setError(byPath.name);
      else toast.apiError(e, 'Профиль не создан');
    }
  }

  return (
    <form className="space-y-4" onSubmit={e => { e.preventDefault(); void save(); }}>
      <TextField label="Название" autoFocus value={name} maxLength={NAME_MAX} error={error}
        onChange={e => { setName(e.target.value); setError(undefined); }} />
      {base
        ? <p className="text-body-small text-on-surface-variant">Параметры возьмутся из профиля по умолчанию — поменяете в карточке.</p>
        : <p className="text-body-small text-error">Нет профиля по умолчанию — параметры взять неоткуда. Обновите страницу.</p>}
      <div className="flex justify-end gap-2">
        <ModalExit />
        <Button type="submit" variant="filled" loading={create.isPending} disabled={!base}>Создать</Button>
      </div>
    </form>
  );
}
