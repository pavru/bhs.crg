import { useMemo, useState } from 'react';
import { Plus, Printer } from 'lucide-react';
import { Button } from '@/shared/ui/Button';
import { Modal } from '@/shared/ui/Modal';
import { TextField } from '@/shared/ui/TextField';
import { Tooltip } from '@/shared/ui/Tooltip';
import { useToast } from '@/shared/ui/Toast';
import { ListDetailShell } from '@/shared/ui/ListDetailShell';
import { useListDetailView } from '@/shared/ui/listDetailView';
import { useRememberedSelection } from '@/shared/hooks/useRememberedSelection';
import { refusalIssues } from '@/shared/api/refusals';
import { useCreatePrintProfile, usePrintProfiles, type PrintProfile } from '@/shared/api/printProfiles';
import { NAME_MAX, isModified, nameIssue, profileSummary } from './printProfileRules';
import { PrintProfileDetail } from './PrintProfileDetail';
import { SheetSketch } from './SheetSketch';

/**
 * «Профили печати» (issue #1455): как раскладывать на страницы книги Excel без своей раскладки — для
 * вида для чтения у счетов. Профиль по умолчанию первым, остальные — по названию.
 *
 * <p>Строка списка несёт набросок листа: бумага, ориентация и поля видны, не открывая профиль. Поиска
 * нет: профилей единицы.</p>
 */

const SELECTION_KEYS = ['profile'] as const;
const LAST_KEY = 'print-profiles-last';
const EMPTY: PrintProfile[] = [];

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

  return (
    <>
      <ListDetailShell view={ld}
        title="Профили печати"
        subtitle="Как раскладывать на страницы файлы Excel, в которых автор не задал раскладку, — для вида для чтения у счетов"
        icon={<Printer size={20} className="text-fg3" />}
        actions={
          <Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)} disabled={!loaded}>
            Добавить профиль
          </Button>
        }
        overlay={isLoading ? <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Загрузка…</div> : undefined}
        nav={
          <div className="flex-1 min-h-0 overflow-y-auto p-2 space-y-0.5" data-testid="print-profiles-list">
            {profiles.map(p => (
              <ProfileRow key={p.id} profile={p} active={selected?.id === p.id}
                onClick={() => { ld.showDetail(); setSelectedId(p.id); }} />
            ))}
            {isError && <p className="text-sm text-fg4 px-3 py-2">Не удалось получить список профилей</p>}
          </div>
        }
        detail={selected
          ? <PrintProfileDetail key={selected.id} profile={selected}
              otherNames={profiles.filter(p => p.id !== selected.id).map(p => p.name)}
              onDeleted={() => setSelectedId(null)} />
          : <div className="flex-1 flex items-center justify-center text-fg4 text-sm">Выберите профиль</div>} />

      <Modal open={createOpen} onOpenChange={setCreateOpen} title="Новый профиль печати">
        <CreateForm names={profiles.map(p => p.name)} base={profiles.find(p => p.isSystem)}
          onCreated={id => { setSelectedId(id); ld.showDetail(); setCreateOpen(false); }}
          onCancel={() => setCreateOpen(false)} />
      </Modal>
    </>
  );
}

function ProfileRow({ profile, active, onClick }: { profile: PrintProfile; active: boolean; onClick: () => void }) {
  return (
    <button type="button" onClick={onClick} aria-current={active || undefined}
      className={`w-full flex items-center gap-3 text-left px-3 py-2 rounded-lg transition-colors ${
        active ? 'bg-brand-subtle text-brand-pressed' : 'text-fg2 hover:bg-base'}`}>
      <SheetSketch paper={profile.paper} orientation={profile.orientation} fit={profile.fit} margins={profile.margins} />
      <span className="min-w-0 flex-1">
        <span className="flex items-center gap-1.5">
          <span className="text-sm truncate min-w-0 flex-1" title={profile.name}>{profile.name}</span>
          {isModified(profile) && (
            <Tooltip content="Изменён"><span className="w-1.5 h-1.5 rounded-full bg-brand shrink-0" /></Tooltip>
          )}
        </span>
        <span className="block text-xs text-fg4 truncate">{profileSummary(profile)}</span>
      </span>
    </button>
  );
}

/**
 * Новый профиль — одно название: параметры берутся из профиля по умолчанию и правятся в карточке.
 * Так новый профиль с первой секунды годен, и форма создания не повторяет карточку.
 */
function CreateForm({ names, base, onCreated, onCancel }: {
  names: string[]; base: PrintProfile | undefined; onCreated: (id: string) => void; onCancel: () => void;
}) {
  const toast = useToast();
  const create = useCreatePrintProfile();
  const [name, setName] = useState('');
  const [error, setError] = useState<string | undefined>();

  async function save() {
    const issue = nameIssue(name, names);
    if (issue) { setError(issue); return; }
    if (!base) return;
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
      <p className="text-xs text-fg3">Параметры возьмутся из профиля по умолчанию — поменяете в карточке.</p>
      <div className="flex justify-end gap-2">
        <Button type="button" variant="text" onClick={onCancel}>Отмена</Button>
        <Button type="submit" variant="filled" loading={create.isPending}>Создать</Button>
      </div>
    </form>
  );
}
