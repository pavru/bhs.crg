import { useState, useEffect } from 'react';
import { useAccount, useUpdateAccount } from '@/shared/api/account';
import { TextField } from '@/shared/ui/TextField';
import { Button } from '@/shared/ui/Button';
import { ChangePasswordModal } from '@/shared/ui/ChangePasswordModal';
import { apiError } from '@/shared/utils/apiError';
import { KeyRound, CheckCircle2, AlertCircle } from 'lucide-react';

/** Профиль текущего пользователя (issue #148): просмотр учётных данных,
 *  редактирование отображаемого имени, смена пароля. Доступно любой роли. */
export function ProfilePage() {
  const { data: account, isLoading } = useAccount();
  const update = useUpdateAccount();
  const [displayName, setDisplayName] = useState('');
  const [pwOpen, setPwOpen] = useState(false);
  const [saved, setSaved] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => { if (account) setDisplayName(account.displayName); }, [account]);

  async function save(e: React.FormEvent) {
    e.preventDefault();
    setError(''); setSaved(false);
    try {
      await update.mutateAsync({ displayName: displayName.trim() });
      setSaved(true);
    } catch (err) {
      setError(apiError(err, 'Не удалось сохранить профиль'));
    }
  }

  if (isLoading || !account) {
    return <div className="px-6 py-4 text-sm text-fg4">Загрузка…</div>;
  }

  const dirty = displayName.trim() !== account.displayName;

  return (
    <div className="px-6 py-4 max-w-lg">
      <h1 className="text-xl font-semibold text-fg1 mb-1">Профиль</h1>
      <p className="text-xs text-fg4 mb-5">Ваши учётные данные.</p>

      <form onSubmit={save} className="space-y-5">
        <div>
          <div className="text-xs text-fg4 mb-1">Email</div>
          <div className="flex items-center gap-2 text-sm text-fg1">
            {account.email}
            {account.emailConfirmed
              ? <span className="inline-flex items-center gap-1 text-xs text-success"><CheckCircle2 size={13} /> подтверждён</span>
              : <span className="inline-flex items-center gap-1 text-xs text-fg4"><AlertCircle size={13} /> не подтверждён</span>}
          </div>
        </div>

        <div>
          <div className="text-xs text-fg4 mb-1">Роль</div>
          <div className="text-sm text-fg1">{account.role === 'Admin' ? 'Администратор' : 'Пользователь'}</div>
        </div>

        <TextField label="Отображаемое имя" value={displayName}
          onChange={e => { setDisplayName(e.target.value); setSaved(false); }} />

        {error && <p className="text-sm text-danger">{error}</p>}
        {saved && <p className="text-sm text-success">Сохранено</p>}

        <div className="flex items-center gap-2">
          <Button type="submit" variant="filled" loading={update.isPending} disabled={!dirty}>
            Сохранить
          </Button>
          <Button type="button" variant="text" icon={<KeyRound size={14} />} onClick={() => setPwOpen(true)}>
            Сменить пароль
          </Button>
        </div>
      </form>

      <ChangePasswordModal open={pwOpen} onClose={() => setPwOpen(false)} />
    </div>
  );
}
