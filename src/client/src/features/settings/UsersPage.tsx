import { useState } from 'react';
import { Plus, KeyRound, Trash2, ShieldCheck, User as UserIcon, Mail } from 'lucide-react';
import { Modal } from '@/shared/ui/Modal';
import { Button, IconButton } from '@/shared/ui/Button';
import { Select, SelectItem } from '@/shared/ui/Select';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { SendMessageDialog } from '@/shared/ui/SendMessageDialog';
import { useAuth, type UserRole } from '@/shared/hooks/useAuth';
import {
  useListUsers, useCreateUser, useChangeUserRole, useResetUserPassword, useDeleteUser,
  type AppUser,
} from '@/shared/api/users';

function apiError(e: unknown): string {
  const err = e as { response?: { data?: { error?: string } }; message?: string };
  return err?.response?.data?.error || err?.message || 'Ошибка';
}

export function UsersPage() {
  const { user: me } = useAuth();
  const { data: users = [], isLoading } = useListUsers();
  const changeRole = useChangeUserRole();
  const del = useDeleteUser();
  const [createOpen, setCreateOpen] = useState(false);
  const [resetFor, setResetFor] = useState<AppUser | null>(null);
  const [rowError, setRowError] = useState<{ id: string; msg: string } | null>(null);
  const [deleteTarget, setDeleteTarget] = useState<AppUser | null>(null);
  const [sendOpen, setSendOpen] = useState(false);

  async function onRoleChange(u: AppUser, role: UserRole) {
    setRowError(null);
    try { await changeRole.mutateAsync({ id: u.id, role }); }
    catch (e) { setRowError({ id: u.id, msg: apiError(e) }); }
  }

  async function onDelete(u: AppUser) {
    setRowError(null);
    try { await del.mutateAsync(u.id); }
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
                <th className="text-left px-4 py-2.5 font-medium text-fg2 w-48">Роль</th>
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
                        {u.role === 'Admin'
                          ? <ShieldCheck size={14} className="text-brand shrink-0" />
                          : <UserIcon size={14} className="text-fg4 shrink-0" />}
                        {u.displayName || u.email}
                        {isSelf && <span className="text-[11px] text-fg4 font-normal">(вы)</span>}
                      </div>
                      {u.displayName && <div className="text-xs text-fg4 mt-0.5">{u.email}</div>}
                      {rowError?.id === u.id && <div className="text-xs text-danger mt-1">{rowError.msg}</div>}
                    </td>
                    <td className="px-4 py-2.5">
                      <Select value={u.role} onValueChange={v => onRoleChange(u, v as UserRole)}
                        disabled={changeRole.isPending} aria-label="Роль" className="w-44">
                        <SelectItem value="Admin">Администратор</SelectItem>
                        <SelectItem value="User">Пользователь</SelectItem>
                      </Select>
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
        Администратор — полный доступ. Пользователь — только работа с документами и данными (без настройки системы).
      </p>

      <CreateUserModal open={createOpen} onClose={() => setCreateOpen(false)} />
      <ResetPasswordModal user={resetFor} onClose={() => setResetFor(null)} />
      <ConfirmDialog
        open={!!deleteTarget}
        onOpenChange={o => { if (!o) setDeleteTarget(null); }}
        title={`Удалить пользователя «${deleteTarget?.email ?? ''}»?`}
        description={<p>Действие необратимо.</p>}
        confirmLabel="Удалить пользователя"
        onConfirm={() => { if (deleteTarget) onDelete(deleteTarget); }}
      />
    </div>
  );
}

function CreateUserModal({ open, onClose }: { open: boolean; onClose: () => void }) {
  const create = useCreateUser();
  const [email, setEmail] = useState('');
  const [displayName, setDisplayName] = useState('');
  const [password, setPassword] = useState('');
  const [role, setRole] = useState<UserRole>('User');
  const [error, setError] = useState('');

  function reset() { setEmail(''); setDisplayName(''); setPassword(''); setRole('User'); setError(''); }

  async function submit(e: React.FormEvent) {
    e.preventDefault();
    setError('');
    try {
      await create.mutateAsync({ email: email.trim(), displayName: displayName.trim(), password, role });
      reset(); onClose();
    } catch (err) { setError(apiError(err)); }
  }

  return (
    <Modal open={open} onOpenChange={o => { if (!o) { reset(); onClose(); } }} title="Новый пользователь">
      <form onSubmit={submit} className="space-y-3">
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Email</label>
          <input type="email" value={email} onChange={e => setEmail(e.target.value)} required autoFocus
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
        </div>
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Отображаемое имя</label>
          <input value={displayName} onChange={e => setDisplayName(e.target.value)}
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
        </div>
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Начальный пароль</label>
          <input type="text" value={password} onChange={e => setPassword(e.target.value)} required minLength={6}
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm font-mono bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
          <p className="text-xs text-fg4 mt-1">Минимум 6 символов. Пользователь сможет сменить его сам.</p>
        </div>
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Роль</label>
          <Select value={role} onValueChange={v => setRole(v as UserRole)} aria-label="Роль">
            <SelectItem value="User">Пользователь — только документы и данные</SelectItem>
            <SelectItem value="Admin">Администратор — полный доступ</SelectItem>
          </Select>
        </div>
        {error && <p className="text-sm text-danger">{error}</p>}
        <div className="flex justify-end gap-2 pt-1">
          <Button type="button" variant="text" onClick={() => { reset(); onClose(); }}>Отмена</Button>
          <Button type="submit" variant="filled" loading={create.isPending}>
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
        <div>
          <label className="block text-sm font-medium text-fg2 mb-1">Новый пароль</label>
          <input type="text" value={password} onChange={e => setPassword(e.target.value)} required minLength={6} autoFocus
            className="w-full border border-stroke-strong rounded-md px-3 py-2 text-sm font-mono bg-surface focus:outline-none focus-visible:ring-2 focus-visible:ring-brand" />
          <p className="text-xs text-fg4 mt-1">Сообщите новый пароль пользователю.</p>
        </div>
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
