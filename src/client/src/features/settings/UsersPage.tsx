import { useState } from 'react';
import { Plus, KeyRound, Trash2, ShieldCheck, User as UserIcon, Mail } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button, IconButton } from '@/shared/ui/Button';
import { TextField } from '@/shared/ui/TextField';
import { PASSWORD_MIN_LENGTH, PASSWORD_HINT } from '@/shared/auth/passwordPolicy';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { SendMessageDialog } from '@/shared/ui/SendMessageDialog';
import { useAuth } from '@/shared/hooks/useAuth';
import { useRoles } from '@/shared/api/roles';
import { RolePicker } from './RolePicker';
import {
  useListUsers, useCreateUser, useChangeUserRoles, useResetUserPassword, useDeleteUser,
  type AppUser,
} from '@/shared/api/users';

function apiError(e: unknown): string {
  const err = e as { response?: { data?: { error?: string } }; message?: string };
  return err?.response?.data?.error || err?.message || 'Ошибка';
}

export function UsersPage() {
  const { user: me } = useAuth();
  const { data: users = [], isLoading } = useListUsers();
  const { data: roles = [] } = useRoles();
  const changeRoles = useChangeUserRoles();
  const del = useDeleteUser();
  const [createOpen, setCreateOpen] = useState(false);
  const [resetFor, setResetFor] = useState<AppUser | null>(null);
  const [rowError, setRowError] = useState<{ id: string; msg: string } | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<AppUser | null>(null);
  const [sendOpen, setSendOpen] = useState(false);

  async function onRolesChange(u: AppUser, roles: string[]) {
    setRowError(null);
    try { await changeRoles.mutateAsync({ id: u.id, roles }); }
    catch (e) { setRowError({ id: u.id, msg: apiError(e) }); }
  }


  return (
    <div className="px-6 py-4 max-w-4xl">
      <div className="flex items-center justify-between mb-4">
        <h1 className="text-xl font-semibold text-fg1">Пользователи</h1>
        <div className="flex items-center gap-2">
          <Button variant="tonal" icon={<Mail size={15} />} onClick={() => setSendOpen(true)}>
            Отправить сообщение
          </Button>
          <Button variant="filled" icon={<Plus size={16} />} onClick={() => setCreateOpen(true)}>
            Добавить пользователя
          </Button>
        </div>
      </div>

      <SendMessageDialog open={sendOpen} onClose={() => setSendOpen(false)}
        candidates={users.map(u => ({ id: u.id, displayName: u.displayName, email: u.email }))} />

      {isLoading ? (
        <div className="text-center text-fg4 text-sm py-10">Загрузка...</div>
      ) : (
        <div className="border border-stroke rounded-lg overflow-hidden bg-surface">
          <table className="w-full text-sm">
            <thead className="bg-base border-b border-stroke">
              <tr>
                <th className="text-left px-4 py-2.5 font-medium text-fg2">Пользователь</th>
                <th className="text-left px-4 py-2.5 font-medium text-fg2 w-56">Роли</th>
                <th className="px-4 py-2.5 w-24" />
              </tr>
            </thead>
            <tbody className="divide-y divide-muted">
              {users.map(u => {
                const isSelf = u.id === me?.sub;
                return (
                  <tr key={u.id} className="group hover:bg-base align-top">
                    <td className="px-4 py-2.5">
                      <div className="text-fg1 font-medium flex items-center gap-2">
                        {/* Щит — у роли «все права», а не у роли с именем «Admin»: признак
                            приходит с сервера, а имя ничего не значит (issues #989/#990).
                            Ролей может быть несколько, и щит ставится, если ТАКАЯ есть хотя бы
                            одна: «все права» плюс что-то ещё — всё равно все права. */}
                        {u.roles.some(ur => roles.find(r => r.name === ur.name)?.allPermissions)
                          ? <ShieldCheck size={14} className="text-brand shrink-0" />
                          : <UserIcon size={14} className="text-fg4 shrink-0" />}
                        {u.displayName || u.email}
                        {isSelf && <span className="text-[11px] text-fg4 font-normal">(вы)</span>}
                      </div>
                      {u.displayName && <div className="text-xs text-fg4 mt-0.5">{u.email}</div>}
                      {rowError?.id === u.id && <div className="text-xs text-danger mt-1">{rowError.msg}</div>}
                    </td>
                    <td className="px-4 py-2.5">
                      <RolePicker value={u.roles.map(r => r.name)}
                        onChange={next => onRolesChange(u, next)}
                        disabled={changeRoles.isPending}
                        ariaLabel={`Роли — ${u.displayName || u.email}`} className="w-52" />
                    </td>
                    <td className="px-4 py-2.5">
                      <div className="flex items-center justify-end gap-1 opacity-0 group-hover:opacity-100 group-focus-within:opacity-100 transition-opacity">
                        <IconButton label="Сбросить пароль" size="sm" onClick={() => setResetFor(u)}>
                          <KeyRound size={14} />
                        </IconButton>
                        <IconButton label="Удалить" size="sm" danger onClick={() => setDeleteTarget(u)}
                          disabled={isSelf || del.isPending}
                          title={isSelf ? 'Нельзя удалить себя' : 'Удалить'}>
                          <Trash2 size={14} />
                        </IconButton>
                      </div>
                    </td>
                  </tr>
                );
              })}
            </tbody>
          </table>
        </div>
      )}

      <p className="text-xs text-fg4 mt-3">
        Ролей у человека может быть несколько: действующие права — объединение прав всех его ролей.
        Что именно даёт каждая и кому её выдавать — на экране «Роли и права»: там же состав
        правится, и правка действует немедленно у всех носителей. Снятые все роли означают
        отозванный доступ: человек войдёт, но не увидит ничего.
      </p>

      <CreateUserModal open={createOpen} onClose={() => setCreateOpen(false)} />
      <ResetPasswordModal user={resetFor} onClose={() => setResetFor(null)} />
      <ConfirmDialog
        open={!!deleteTarget}
        onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить пользователя «${deleteTarget?.email ?? ''}»?`}
        description={<p>Действие необратимо.</p>}
        confirmLabel="Удалить пользователя"
        onConfirm={() => { if (deleteTarget) return del.mutateAsync(deleteTarget.id); }}
      />
    </div>
  );
}

function CreateUserModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const create = useCreateUser();
  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  // Умолчания у роли НЕТ, и это главное в этом диалоге.
  //
  // ⚠️ Здесь стояло `role || roles[0]?.name`. Выглядело безобидно — «первая роль списка вместо
  // имени, зашитого в код», — а означало: какая роль окажется первой в ответе сервера, такую и
  // получит человек, у которого администратор роль не выбрал. Сервер сортировал по техническому
  // имени, первой шла Accountant, и новый сотрудник заводился «Бухгалтером» — с правом отмечать
  // оплату и закрывать период (ревью #983). Порядок на сервере починен, но умолчание опасно самим
  // своим существованием: выдача прав — не то место, где подставляют «что-нибудь».
  //
  // Ролей может быть несколько (issue #984), но пустой список при ЗАВЕДЕНИИ отвергает и сервер:
  // завести человека без единой роли — почти всегда промах, он войдёт и не увидит ничего.
  // Снять же все роли у работающего можно, и это другое действие — осознанный отзыв доступа.
  const [chosen, setChosen] = useState<string[]>([]);
  const [error, setError] = useState('');

  function reset() { setEmail(''); setDisplayName(''); setPassword(''); setChosen([]); setError(''); }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    if (chosen.length === 0) { setError('Выберите роль: она и есть набор прав, который получит человек.'); return; }
    try {
      await create.mutateAsync({ email: email.trim(), displayName: displayName.trim(), password, roles: chosen });
      reset(); onClose();
    } catch (err) { setError(apiError(err)); }
  }

  return (
    <Modal open={open} onOpenChange={o => { if (!o) { reset(); onClose(); } }} title="Новый пользователь">
      <form onSubmit={submit} className="space-y-3">
        <TextField label="Email" type="email" value={email} onChange={e => setEmail(e.target.value)} required autoFocus />
        <TextField label="Отображаемое имя" value={displayName} onChange={e => setDisplayName(e.target.value)} />
        <TextField label="Начальный пароль" type="text" value={password} onChange={e => setPassword(e.target.value)}
          required minLength={PASSWORD_MIN_LENGTH} className="font-mono"
          hint={`${PASSWORD_HINT} Пользователь сможет сменить его сам.`} />
        <div>
          <div className="text-xs text-fg4 mb-1">Роли</div>
          <RolePicker value={chosen} onChange={setChosen} ariaLabel="Роли нового пользователя" />
          <p className="text-xs text-fg4 mt-1">
            Можно выбрать несколько: права складываются. Что даёт каждая — на экране «Роли и права».
          </p>
        </div>
        {error && <p className="text-sm text-danger">{error}</p>}
        <div className="flex justify-end gap-2 pt-1">
          <Button type="button" variant="text" onClick={() => { reset(); onClose(); }}>Отмена</Button>
          <Button type="submit" variant="filled" loading={create.isPending} disabled={chosen.length === 0}>
            {create.isPending ? 'Создание…' : 'Создать'}
          </Button>
        </div>
      </form>
    </Modal>
  );
}

function ResetPasswordModal({ user, onClose }: { user: AppUser | null; onClose: () => void }) {
  const reset = useResetUserPassword();
  const [password, setPassword] = useState('');
  const [error, setError] = useState('');
  const [done, setDone] = useState(false);

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    if (!user) return;
    setError('');
    try { await reset.mutateAsync({ id: user.id, newPassword: password }); setDone(true); setTimeout(() => { setDone(false); setPassword(''); onClose(); }, 1200); }
    catch (err) { setError(apiError(err)); }
  }

  return (
    <Modal open={!!user} onOpenChange={o => { if (!o) { setPassword(''); setError(''); setDone(false); onClose(); } }}
      title={`Сброс пароля — ${user?.email ?? ''}`}>
      <form onSubmit={submit} className="space-y-3">
        <TextField label="Новый пароль" type="text" value={password} onChange={e => setPassword(e.target.value)}
          required minLength={PASSWORD_MIN_LENGTH} autoFocus className="font-mono"
          hint={`${PASSWORD_HINT} Сообщите новый пароль пользователю.`} />
        {error && <p className="text-sm text-danger">{error}</p>}
        {done && <p className="text-sm text-success">Пароль изменён</p>}
        <div className="flex justify-end gap-2 pt-1">
          <Button type="button" variant="text" onClick={onClose}>Отмена</Button>
          <Button type="submit" variant="filled" loading={reset.isPending}>
            {reset.isPending ? 'Сохранение…' : 'Сбросить пароль'}
          </Button>
        </div>
      </form>
    </Modal>
  );
}
