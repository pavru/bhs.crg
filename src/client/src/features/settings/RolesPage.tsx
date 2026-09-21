import { useMemo, useState } from 'react';
import { Plus, ShieldCheck, Trash2, Users as UsersIcon, Pencil } from 'lucide-react';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { Modal } from '@/shared/ui/Modal';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { ListDetailShell, NavSection } from '@/shared/ui/ListDetailShell';
import { apiError } from '@/shared/utils/apiError';
import {
  useRoles, usePermissionCatalog, useCreateRole, useRenameRole, useSetRolePermissions, useDeleteRole,
  type AppRole, type PermissionInfo,
} from '@/shared/api/roles';

/**
 * Редактор матрицы ролей (issue #951, ТЗ AUTH-5).
 *
 * Условие, без которого редактор небезопасен, — объяснение у каждой галки: что право даёт и к каким
 * данным открывает доступ. Поэтому объяснение стоит ПОД названием права, а не в подсказке по
 * наведению: администратор раздаёт доступ глазами, а не курсором, и спрятанное объяснение — это
 * объяснение, которого нет.
 *
 * Права сгруппированы по модулям тем же порядком, каким их отдаёт сервер: ядро, модули, составные.
 */

function RoleBadge({ role }: { role: AppRole }) {
  if (!role.system) return null;
  return (
    <span className="shrink-0 inline-flex items-center h-5 px-2 rounded-full text-[11px] font-medium bg-brand-subtle text-brand">
      системная
    </span>
  );
}

export function RolesPage() {
  const { data: roles = [], isLoading } = useRoles();
  const { data: groups = [] } = usePermissionCatalog();
  const save = useSetRolePermissions();
  const del = useDeleteRole();

  const [picked, setPicked] = useState<string | null>(null);
  // Черновик помнит, ЧЬИ это галки. Без имени роли внутри его пришлось бы сбрасывать эффектом на
  // смену выбора, а эффект, досылающий состояние, — это лишний кадр с чужими галками и правило
  // линта, которое в этом проекте уже вычищали (issue #858).
  const [draft, setDraft] = useState<{ role: string; codes: string[] } | null>(null);
  const [error, setError] = useState('');
  const [createOpen, setCreateOpen] = useState(false);
  const [renameTarget, setRenameTarget] = useState<AppRole | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<AppRole | null>(null);

  // Выбор ВЫЧИСЛЯЕМ, а не досылаем эффектом: пока никого не выбрали, открыта первая роль.
  const selected = roles.find(r => r.name === picked) ?? roles[0] ?? null;

  // Черновик чужой роли не показывается: сравнение имени и есть тот сброс, который иначе делал бы
  // эффект. Галки предыдущей роли на следующую не переезжают, и «сохранить» не запишет чужой состав.
  //
  // ⚠️ Проверяем обе величины явно. Запись `draft?.role === selected?.name` выглядит короче и
  // ломается на первом же кадре: пока роли грузятся, обе части — undefined, условие истинно, а
  // draft ещё null. Типы этого не видят, экран падает целиком (поймано живым прогоном).
  const codes = draft !== null && selected !== null && draft.role === selected.name ? draft.codes : null;

  // Роль «все права»: её состав сервер править отказывается (409). Признак приходит С СЕРВЕРА, а не
  // угадывается по имени «Admin»: правило живёт там, где ему отказывают, и имя роли к делу не
  // относится — им распоряжается объявление, а не этот экран.
  const locked = selected?.allPermissions === true;

  const checked = useMemo(
    () => new Set(codes ?? selected?.permissions ?? []),
    [codes, selected]);

  const dirty = codes !== null && selected !== null
    && (codes.length !== selected.permissions.length
        || codes.some(c => !selected.permissions.includes(c)));

  function toggle(code: string) {
    if (!selected || locked) return;
    const next = new Set(codes ?? selected.permissions);
    if (next.has(code)) next.delete(code); else next.add(code);
    setDraft({ role: selected.name, codes: [...next] });
  }

  async function submit() {
    if (!selected || !codes) return;
    setError('');
    try {
      await save.mutateAsync({ name: selected.name, permissions: codes });
      setDraft(null);
    } catch (e) { setError(apiError(e)); }
  }

  function pick(name: string) {
    setPicked(name);
    setError('');
  }

  const row = (r: AppRole) => (
    <button key={r.name} onClick={() => pick(r.name)}
      className={`w-full text-left px-3 py-2 rounded-lg transition-colors ${
        r.name === selected?.name ? 'bg-brand-subtle' : 'hover:bg-base'}`}>
      <div className="flex items-center gap-2">
        <span className="text-sm text-fg1 truncate">{r.title}</span>
        <RoleBadge role={r} />
      </div>
      <div className="text-[11px] text-fg4 mt-0.5 flex items-center gap-2">
        <span className="inline-flex items-center gap-1"><UsersIcon size={11} />{r.users}</span>
        <span>прав: {r.permissions.length}</span>
      </div>
    </button>
  );

  return (
    <>
    <ListDetailShell
      title="Роли и права"
      subtitle="Роль — это набор прав. Состав правится здесь, выдаётся роль на экране «Пользователи»."
      headerAction={
        <Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>
          Новая роль
        </Button>
      }
      nav={
        <>
          <NavSection label="Роли" />
          {/* Своя прокрутка у каждого слота — правило этого shell: он раздаёт высоту, но не
              прокручивает. Без неё растут обе колонки сразу, и панель «Сохранить», приклеенная к
              низу detail, уезжает за край экрана вместе со списком ролей. */}
          <div className="flex-1 min-h-0 overflow-y-auto px-2 pb-3 space-y-0.5">
            {isLoading ? <div className="px-3 py-2 text-sm text-fg4">Загрузка…</div> : roles.map(row)}
          </div>
        </>
      }
      detail={selected && (
        <div className="flex-1 min-h-0 overflow-y-auto">
        <div className="px-6 py-4 pb-2 max-w-3xl">
          <div className="flex items-start justify-between gap-4 mb-1">
            <div>
              <h2 className="text-lg font-semibold text-fg1 flex items-center gap-2">
                <ShieldCheck size={18} className="text-fg3" />
                {selected.title}
              </h2>
              {selected.summary && <p className="text-sm text-fg3 mt-1">{selected.summary}</p>}
            </div>
            <div className="flex items-center gap-1 shrink-0">
              {!selected.system && (
                <IconButton label="Переименовать" size="sm" onClick={() => setRenameTarget(selected)}>
                  <Pencil size={14} />
                </IconButton>
              )}
              {!selected.system && (
                <IconButton label="Удалить роль" size="sm" danger onClick={() => setDeleteTarget(selected)}>
                  <Trash2 size={14} />
                </IconButton>
              )}
            </div>
          </div>

          <p className="text-[13px] text-fg4 mb-4">
            Роль носят {selected.users} чел.{' '}
            {locked
              ? 'Состав этой роли задан не здесь — см. ниже.'
              : 'Правка состава действует немедленно у всех них — перезаходить в систему никому не нужно.'}
          </p>

          {locked && (
            <p className="text-[13px] text-fg2 border border-stroke rounded-lg bg-muted px-4 py-3 mb-4">
              <span className="font-medium">Состав этой роли не правится.</span> Он не перечислен:
              он равен справочнику прав и пополняется вместе с ним — поэтому право следующего
              обновления достанется ей само. Снятое здесь вернулось бы при ближайшем запуске, а
              снятое управление пользователями оставило бы систему без администратора. Нужен
              администратор с ограничениями — заведите отдельную роль и выдайте ей нужные галки.
            </p>
          )}

          {groups.map(group => (
            <section key={group.module} className="mb-5">
              <h2 className="text-[11px] font-medium uppercase tracking-wide text-fg4 mb-2">{group.title}</h2>
              <div className="border border-stroke rounded-lg divide-y divide-muted bg-surface">
                {group.permissions.map((p: PermissionInfo) => (
                  <label key={p.code} className={`flex gap-3 px-4 py-2.5 hover:bg-base ${
                    locked ? 'cursor-default' : 'cursor-pointer'}`}>
                    <input type="checkbox" className="mt-1 shrink-0" checked={checked.has(p.code)}
                      disabled={locked} onChange={() => toggle(p.code)} />
                    <span className="min-w-0">
                      <span className="block text-sm text-fg1">{p.gives}</span>
                      <span className="block text-[12px] text-fg3">Открывает доступ: {p.opens}</span>
                      <span className="block text-[11px] text-fg4 font-mono mt-0.5">{p.code}</span>
                      {p.usuallyWith.length > 0 && (
                        <span className="block text-[11px] text-fg4">
                          обычно вместе с: {p.usuallyWith.join(', ')}
                        </span>
                      )}
                    </span>
                  </label>
                ))}
              </div>
            </section>
          ))}

          {error && <p className="text-sm text-danger mb-2">{error}</p>}

          {/* У роли «все права» сохранять нечего — кнопки нет вовсе, а не выключенная: выключенная
              кнопка обещает, что когда-нибудь включится. */}
          {!locked && (
            <div className="flex items-center gap-2 sticky bottom-0 bg-base/90 backdrop-blur py-3">
              <Button variant="filled" disabled={!dirty} loading={save.isPending} onClick={submit}>
                Сохранить состав прав
              </Button>
              {dirty && <Button variant="text" onClick={() => setDraft(null)}>Отменить</Button>}
            </div>
          )}
        </div>
        </div>
      )}
    />
      <CreateRoleModal open={createOpen} onClose={() => setCreateOpen(false)} onCreated={setPicked} />
      {/* key — чтобы диалог перемонтировался под другую роль: поля заполняются начальным
          значением useState, а не досылаются эффектом (тот же приём, что и в остальных диалогах). */}
      {renameTarget && (
        <RenameRoleModal key={renameTarget.name} role={renameTarget}
          onClose={() => setRenameTarget(null)} />
      )}
      <ConfirmDialog
        open={!!deleteTarget}
        onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить роль «${deleteTarget?.title ?? ''}»?`}
        description={<p>Действие необратимо. Роль, которую кто-то носит, удалить нельзя — сначала снимите её.</p>}
        confirmLabel="Удалить роль"
        onConfirm={async () => {
          if (!deleteTarget) return;
          await del.mutateAsync(deleteTarget.name);
          setPicked(null);
        }}
      />
    </>
  );
}

function CreateRoleModal(
  { open, onClose, onCreated }: { open: boolean; onClose: () => void; onCreated: (name: string) => void },
) {
  const create = useCreateRole();
  const [title, setTitle] = useState('');
  const [summary, setSummary] = useState('');
  const [error, setError] = useState('');

  function reset() { setTitle(''); setSummary(''); setError(''); }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    try {
      // Новая роль заводится ПУСТОЙ: права ставятся галками тут же, рядом с объяснениями. Набор в
      // диалоге создания означал бы выдачу доступа вслепую — в диалоге объяснениям места нет.
      const role = await create.mutateAsync({ title: title.trim(), summary: summary.trim(), permissions: [] });
      onCreated(role.name);
      reset(); onClose();
    } catch (err) { setError(apiError(err)); }
  }

  return (
    <Modal open={open} onOpenChange={o => { if (!o) { reset(); onClose(); } }} title="Новая роль">
      <form onSubmit={submit} className="space-y-3">
        <TextField label="Название" value={title} onChange={e => setTitle(e.target.value)} required autoFocus />
        <TextField label="Для чего роль" value={summary} onChange={e => setSummary(e.target.value)}
          hint="Показывается рядом с названием — чтобы через полгода было понятно, кому её выдавать." />
        {error && <p className="text-sm text-danger">{error}</p>}
        <div className="flex justify-end gap-2 pt-1">
          <Button type="button" variant="text" onClick={() => { reset(); onClose(); }}>Отмена</Button>
          <Button type="submit" variant="filled" loading={create.isPending}>Создать</Button>
        </div>
      </form>
    </Modal>
  );
}

function RenameRoleModal({ role, onClose }: { role: AppRole; onClose: () => void }) {
  const rename = useRenameRole();
  const [title, setTitle] = useState(role.title);
  const [summary, setSummary] = useState(role.summary ?? '');
  const [error, setError] = useState('');

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    try {
      await rename.mutateAsync({ name: role.name, title: title.trim(), summary: summary.trim() });
      onClose();
    } catch (err) { setError(apiError(err)); }
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Переименовать роль">
      <form onSubmit={submit} className="space-y-3">
        <TextField label="Название" value={title} onChange={e => setTitle(e.target.value)} required autoFocus />
        <TextField label="Для чего роль" value={summary} onChange={e => setSummary(e.target.value)} />
        {error && <p className="text-sm text-danger">{error}</p>}
        <div className="flex justify-end gap-2 pt-1">
          <Button type="button" variant="text" onClick={onClose}>Отмена</Button>
          <Button type="submit" variant="filled" loading={rename.isPending}>Сохранить</Button>
        </div>
      </form>
    </Modal>
  );
}
