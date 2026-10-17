import { useState } from 'react';
import { Pencil, Plus, Trash2 } from 'lucide-react';
import { NO_ACCESS, useAccess } from '@/shared/api/access';
import { Button } from '@/shared/ui/Button';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { Modal } from '@/shared/ui/Modal';
import { TextField } from '@/shared/ui/TextField';
import { useToast } from '@/shared/ui/Toast';
import { workNav } from '@/shared/ui/navConfig';
import { visibleNav } from '@/shared/ui/navAccess';
import { useWorkspace } from '@/shared/ui/workspaceContext';
import {
  DEFAULT_KEY, NAME_MAX, availableModules, compositionLabel, coreSectionsWith, membership, newWorkspaceId,
  ownProblem, ownToWorkspace, type OwnWorkspace,
} from '@/shared/ui/workspaces';
import { Tooltip } from '@/shared/ui/Tooltip';

interface Draft {
  /** null — новое пространство. */
  id: string | null;
  name: string;
  modules: string[];
  opensAtLogin: boolean;
}

/**
 * «Настроить пространства…» (ТЗ AUTH-16.5; issue #1001) — свои пространства: имя, модули и «Открывать
 * при входе». Открывается из переключателя: нужда в своём пространстве осознаётся при переключении,
 * а не в профиле.
 *
 * <p>Под галками — строка «Вместе с ними: …»: разделы ядра, которые придут с отмеченными модулями.
 * Без неё человек выберет «Счета» и решит, что механизм сломан, увидев стройки. Ниже — предпросмотр
 * состава ТОЙ ЖЕ строкой, что потом стоит в шапке.</p>
 */
export function WorkspacesDialog({ onClose }: { onClose: () => void }) {
  const ws = useWorkspace();
  const { data: access = NO_ACCESS } = useAccess();
  const toast = useToast();
  const [draft, setDraft] = useState<Draft | null>(() => (ws.own.length === 0 ? blank() : null));
  const [saving, setSaving] = useState(false);
  const [removing, setRemoving] = useState<OwnWorkspace | null>(null);

  const offered = availableModules(access);
  const visible = visibleNav(workNav, access);

  function edit(own: OwnWorkspace) {
    setDraft({ id: own.id, name: own.name, modules: own.modules, opensAtLogin: ws.defaultChoice === `own:${own.id}` });
  }

  const reserved = ws.list.filter(w => w.kind !== 'own').map(w => w.name);
  const problem = draft ? ownProblem(draft.name, draft.modules, ws.own, draft.id, reserved) : null;
  // Недоступный модуль в правке помечен, а не исчез (AUTH-16.3): снять его — решение человека.
  // Список — из ИСХОДНОГО состава, а не из черновика (ревью PR #1330): снятая галка иначе убрала бы
  // строку, и вернуть модуль стало бы негде — после «Сохранить» он пропал бы насовсем.
  const original = draft?.id ? ws.own.find(w => w.id === draft.id) : undefined;
  const closed = original ? membership(ownToWorkspace(original), access).closed : [];
  const preview = draft ? ownToWorkspace({ id: draft.id ?? 'new', name: draft.name, modules: draft.modules }) : null;
  const together = draft ? coreSectionsWith(draft.modules, visible, access) : [];
  // Правки — то, что отличается от исходного: открытая пустая форма закрывается без вопроса.
  const dirty = draft !== null && isChanged(draft, ws.own, ws.defaultChoice);

  async function save() {
    if (!draft || problem || saving) return;
    const id = draft.id ?? newWorkspaceId();
    const next: OwnWorkspace = { id, name: draft.name.trim(), modules: draft.modules };
    const key = `own:${id}`;
    setSaving(true);
    try {
      // Список — СВЕЖИЙ, с сервера (ревью PR #1330): правка по кэшу стёрла бы пространство, заведённое
      // тем временем на другом компьютере. Проверки имени — заново, по нему же.
      await ws.updateOwn(fresh => {
        if (draft.id && !fresh.some(w => w.id === draft.id)) return 'Это пространство тем временем удалили';
        const late = ownProblem(next.name, next.modules, fresh, draft.id, reserved);
        if (late) return late;
        return draft.id ? fresh.map(w => (w.id === id ? next : w)) : [...fresh, next];
      }, (fresh): Record<string, string | null> => {
        // Умолчание — той же правкой: отдельная запись после списка могла бы не дойти, и галка
        // «Открывать при входе» соврала бы.
        if (draft.opensAtLogin) return { [DEFAULT_KEY]: key };
        return fresh[DEFAULT_KEY] === key ? { [DEFAULT_KEY]: null } : {};
      });
      setDraft(null);
      if (draft.id) {
        toast.success('Пространство изменено');
      } else {
        // Новое заводят, чтобы в нём работать: в него переходим и окно закрываем.
        ws.choose(key);
        toast.success(`Пространство «${next.name}» создано и открыто`);
        onClose();
      }
    } catch (e) {
      toast.apiError(e, 'Пространство не сохранено');
    } finally {
      setSaving(false);
    }
  }

  function toggle(code: string) {
    if (!draft) return;
    setDraft({ ...draft, modules: draft.modules.includes(code) ? draft.modules.filter(c => c !== code) : [...draft.modules, code] });
  }

  return (
    <Modal open onOpenChange={o => { if (!o) onClose(); }} title="Рабочие пространства" wide isDirty={dirty && !saving}>
      <p className="text-xs text-fg3 mb-3">
        Пространство сужает меню до выбранных модулей и разделов, которыми они пользуются. Доступ оно не
        меняет: разделы других модулей открываются по ссылке и находятся через Ctrl+K.
      </p>

      {ws.own.length > 0 && (
        <ul className="mb-3 divide-y divide-stroke rounded-lg border border-stroke" data-testid="own-workspaces">
          {ws.own.map(own => (
            <li key={own.id} className="flex items-center gap-2 px-3 py-2">
              <span className="min-w-0 flex-1">
                <span className="block text-sm text-fg1 truncate">
                  {own.name}
                  {ws.defaultChoice === `own:${own.id}` && <span className="ml-2 text-xs text-fg3">открывается при входе</span>}
                </span>
                <span className="block text-xs text-fg3 truncate">{compositionLabel(ownToWorkspace(own), access)}</span>
              </span>
              {/* Пока черновик изменён, правку другого не начать — карандаш скрыт (ревью PR #1330):
                  иначе набранное заменилось бы молча, хотя закрытие окна те же правки бережёт. */}
              {!dirty && (
                <Tooltip content="Изменить" name>
                  <button type="button" onClick={() => edit(own)} className="p-1 text-fg3 hover:text-fg1">
                    <Pencil size={14} />
                  </button>
                </Tooltip>
              )}
              <Tooltip content="Удалить" name>
                <button type="button" onClick={() => setRemoving(own)} className="p-1 text-fg3 hover:text-danger">
                  <Trash2 size={14} />
                </button>
              </Tooltip>
            </li>
          ))}
        </ul>
      )}

      {!draft ? (
        <Button variant="outlined" icon={<Plus size={16} />} onClick={() => setDraft(blank())}>Новое пространство</Button>
      ) : (
        <div className="space-y-3 rounded-lg border border-stroke p-3" data-testid="workspace-form">
          <TextField label="Имя" value={draft.name} maxLength={NAME_MAX} autoFocus
            error={draft.name.trim() && problem?.startsWith('Пространство с таким') ? problem : undefined}
            onChange={e => setDraft({ ...draft, name: e.target.value })} />

          <fieldset>
            <legend className="text-xs text-fg3 mb-1">Модули</legend>
            <div className="flex flex-wrap gap-x-4 gap-y-1">
              {offered.map(m => (
                <label key={m.code} className="flex items-center gap-2 text-sm text-fg1">
                  <input type="checkbox" checked={draft.modules.includes(m.code)} onChange={() => toggle(m.code)} />
                  {m.title}
                </label>
              ))}
              {closed.map(m => (
                <label key={m.code} className="flex items-center gap-2 text-sm text-fg3">
                  <input type="checkbox" checked={draft.modules.includes(m.code)} onChange={() => toggle(m.code)} />
                  {m.title} <span className="text-xs">— недоступен</span>
                </label>
              ))}
            </div>
            <p className="mt-1 text-xs text-fg3" data-testid="workspace-together">
              {together.length ? `Вместе с ними: ${together.join(', ')}` : 'Разделов ядра вместе с ними нет'}
            </p>
          </fieldset>

          {preview && draft.modules.length > 0 && (
            <p className="text-xs text-fg2" data-testid="workspace-preview">
              В шапке: <span className="font-medium">{draft.name.trim() || 'Без имени'}</span> · {compositionLabel(preview, access)}
            </p>
          )}

          <label className="flex items-center gap-2 text-sm text-fg1">
            <input type="checkbox" checked={draft.opensAtLogin} onChange={e => setDraft({ ...draft, opensAtLogin: e.target.checked })} />
            Открывать при входе
          </label>

          <div className="flex items-center gap-2">
            <Button variant="filled" loading={saving} disabled={problem !== null} onClick={() => void save()}>
              {draft.id ? 'Сохранить' : 'Создать'}
            </Button>
            <Button variant="text" disabled={saving} onClick={() => setDraft(null)}>Отмена</Button>
          </div>
          {/* Заперта — с причиной под кнопкой, а не отказом после нажатия (AUTH-16.5). */}
          {problem && <p className="text-xs text-fg3" data-testid="workspace-problem">{problem}</p>}
        </div>
      )}

      <ConfirmDialog open={removing !== null} onOpenChange={o => { if (!o) setRemoving(null); }}
        title="Удалить пространство?"
        description={removing ? `Пространство «${removing.name}» исчезнет из переключателя. Доступ и данные это не меняет.` : undefined}
        confirmLabel="Удалить" errorTitle="Пространство не удалено"
        onConfirm={async () => {
          if (!removing) return;
          const key = `own:${removing.id}`;
          await ws.updateOwn(fresh => fresh.filter(w => w.id !== removing.id),
            (fresh): Record<string, string | null> => (fresh[DEFAULT_KEY] === key ? { [DEFAULT_KEY]: null } : {}));
          if (draft?.id === removing.id) setDraft(null);
        }} />
    </Modal>
  );
}

function blank(): Draft {
  return { id: null, name: '', modules: [], opensAtLogin: false };
}

function isChanged(draft: Draft, own: OwnWorkspace[], defaultChoice: string | null): boolean {
  const was = own.find(w => w.id === draft.id);
  if (!was) return draft.name.trim() !== '' || draft.modules.length > 0 || draft.opensAtLogin;
  return draft.name !== was.name
    || draft.modules.length !== was.modules.length || draft.modules.some(c => !was.modules.includes(c))
    || draft.opensAtLogin !== (defaultChoice === `own:${was.id}`);
}
