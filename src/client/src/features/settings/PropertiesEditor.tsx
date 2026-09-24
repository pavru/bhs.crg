/**
 * Свойства типа: имя, код, родитель, признаки — часть страницы типов документов.
 *
 * Выделено из `DocumentTypesPage.tsx` (issue #1031). `getDescendantIds` оставлен приватным нарочно:
 * он нужен только выбору родителя — чтобы нельзя было замкнуть иерархию на себя.
 */
import { useState } from 'react';
import { apiError } from '@/shared/utils/apiError';
import { Switch } from '@/shared/ui/Switch';
import { TypePickerField } from '@/shared/ui/TypePickerField';
import { TextField } from '@/shared/ui/TextField';
import { Select, SelectItem } from '@/shared/ui/Select';
import { NO_ACCESS, useAccess } from '@/shared/api/access';
import { CORE_OWNER, keepingCurrent, offeredTypes, ownerOptions, ownerTitle } from '@/shared/api/typeOwners';
import { ConfirmDialog } from '@/shared/ui/ConfirmDialog';
import { countTemplatesUsingTypeCode } from '@/shared/api/typstUserLib';
import { useUpdateDocumentType, useSetDocumentTypeAbstract, useSetDocumentTypeAllowsProxy, useSetDocumentTypeOwner } from '@/shared/api/documentTypes';
import type { DocumentType } from '@/shared/api/types';
import { nextAutoKey } from './schemaConstants';
import { useRegisterEditor } from './typeEditorRegistry';
import { toParentPickTypes } from './documentTypeHelpers';

// ─── Properties editor ─────────────────────────────────────────────────────────

function getDescendantIds(id: string, allDocTypes: DocumentType[]): Set<string> {
  const result = new Set<string>();
  const stack = [id];
  while (stack.length > 0) {
    const curr = stack.pop()!;
    for (const dt of allDocTypes) {
      if (dt.parentId === curr && !result.has(dt.id)) {
        result.add(dt.id);
        stack.push(dt.id);
      }
    }
  }
  return result;
}

export function PropertiesEditor({ docType, allDocTypes }: { docType: DocumentType; allDocTypes: DocumentType[] }) {
  const [name, setName] = useState(docType.name);
  const [code, setCode] = useState(docType.code);
  const [parentId, setParentId] = useState(docType.parentId ?? '');
  const [error, setError] = useState('');
  const mutation = useUpdateDocumentType();
  const abstractMutation = useSetDocumentTypeAbstract();
  const proxyMutation = useSetDocumentTypeAllowsProxy();
  const ownerMutation = useSetDocumentTypeOwner();
  const { data: access } = useAccess();
  const [ownerError, setOwnerError] = useState('');

  const descendantIds = getDescendantIds(docType.id, allDocTypes);
  // В родители ПРЕДЛАГАЮТСЯ типы включённых модулей, но уже записанный родитель остаётся в
  // списке, даже если его модуль выключен: этим же списком пикер разрешает своё значение, и без
  // записанного родителя поле показало бы «— без родителя —» — наследование выглядело бы
  // потерянным.
  const candidates = allDocTypes.filter(
    dt => dt.kind === docType.kind && dt.id !== docType.id && !descendantIds.has(dt.id),
  );
  const eligibleParents = keepingCurrent(
    offeredTypes(candidates, access), candidates, parentId || docType.parentId);

  const dirty = name !== docType.name || code !== docType.code || parentId !== (docType.parentId ?? '');

  // Код типа стабилен после создания (issue #355): у существующего типа переименование НЕ меняет код
  // (иначе ломаются ссылки/резолв, #59). Авто-код — только у нового (без id).
  function handleNameChange(v: string) {
    setCode(nextAutoKey(code, name, v, !docType.id));
    setName(v);
  }

  // Смена кода типа ломает вызовы «Код.имя» в шаблонах (issue #773): спрашиваем ДО сохранения,
  // сколько шаблонов затронуто. Typst сообщил бы об этом лишь при генерации документа — когда связь
  // с причиной уже не видна.
  const [codeWarning, setCodeWarning] = useState<{ count: number; onConfirm: () => void } | null>(null);

  // Сохранение параметров: бросает при ошибке — чтобы общий «Сохранить»/гард прервались (issue #197).
  async function save() {
    if (!name.trim() || !code.trim()) { setError('Наименование и код обязательны'); throw new Error('validation'); }
    setError('');

    if (docType.id && code.trim() !== docType.code) {
      const used = await countTemplatesUsingTypeCode(docType.code).catch(() => 0);
      if (used > 0) {
        const confirmed = await new Promise<boolean>(resolve =>
          setCodeWarning({ count: used, onConfirm: () => resolve(true) }));
        setCodeWarning(null);
        if (!confirmed) throw new Error('rename-cancelled');
      }
    }

    try {
      await mutation.mutateAsync({ id: docType.id, name: name.trim(), code: code.trim(), parentId: parentId || null });
    } catch (err: unknown) {
      // Текст сервера, а не «Request failed with status code 409»: отказ называет КОНКРЕТНЫЙ тип
      // («опора типа ядра принадлежит модулю „id“»), и без него человеку нечего чинить.
      setError(apiError(err, 'Ошибка сохранения'));
      throw err;
    }
  }
  useRegisterEditor('props', dirty, save,
    () => { setName(docType.name); setCode(docType.code); setParentId(docType.parentId ?? ''); setError(''); });

  return (
    <form onSubmit={e => { e.preventDefault(); save().catch(() => { /* ошибка показана в форме */ }); }}
      className="space-y-3 pb-4 border-b border-stroke mb-4">
      <ConfirmDialog
        open={codeWarning !== null}
        onOpenChange={o => { if (!o) setCodeWarning(null); }}
        title="Сменить код типа?"
        description={
          <>
            Шаблонов, которые обращаются к блокам этого типа по коду{' '}
            <code className="font-mono">{docType.code}</code>: <b>{codeWarning?.count ?? 0}</b>.
            После смены кода эти вызовы перестанут разрешаться — префикс в них придётся заменить
            вручную. Typst сообщит об этом только при генерации документа.
          </>
        }
        confirmLabel="Сменить код"
        confirmDanger={false}
        onConfirm={() => codeWarning?.onConfirm()}
      />
      <p className="text-xs font-medium text-fg3 uppercase tracking-wide">Параметры типа</p>
      <div className="grid grid-cols-2 gap-3">
        <TextField label="Наименование" value={name} onChange={e => handleNameChange(e.target.value)} required />
        <TextField label="Код" value={code} onChange={e => setCode(e.target.value)}
          required spellCheck={false} className="font-mono" />
      </div>
      <TypePickerField className="w-full" label="Родительский тип" title="Родительский тип"
        placeholder="— без родителя —" clearable={{ label: 'Без родителя' }}
        types={toParentPickTypes(eligibleParents)} value={parentId || undefined}
        onChange={id => setParentId(id ?? '')} />
      {/* Владелец — мгновенная мутация, как прокси и абстрактность: это не часть формы параметров.
          Отказ сервера («опора ядра — ядро») показывается здесь же: ответ называет конкретный тип,
          и пересказывать его своими словами значило бы потерять это имя. */}
      <div>
        <Select label="Владелец" value={docType.module} disabled={ownerMutation.isPending}
          onValueChange={v => {
            setOwnerError('');
            ownerMutation.mutate({ id: docType.id, module: v },
              { onError: (e: unknown) => setOwnerError(apiError(e, 'Не удалось сменить владельца')) });
          }}>
          {ownerOptions(access ?? NO_ACCESS).map(o => <SelectItem key={o.code} value={o.code}>{o.title}</SelectItem>)}
        </Select>
        <p className="mt-1 text-xs text-fg4">
          {docType.module === CORE_OWNER
            ? 'Тип ядра: остаётся в редакторе при любом наборе модулей.'
            : `Тип модуля «${ownerTitle(docType.module, access ?? NO_ACCESS)}»: при выключенном модуле не предлагается.`}
        </p>
        {ownerError && <p className="mt-1 text-xs text-danger">{ownerError}</p>}
      </div>
      {/* Прокси/абстрактность — отдельные мгновенные переключатели (не часть формы «Сохранить
          параметры»): каждый — своя мутация, применяется сразу по щелчку (issue #197 Фаза C). */}
      <div className="flex flex-col gap-2 pt-1">
        <label className="flex items-center gap-2.5 select-none">
          <Switch checked={docType.allowsProxy} size="sm" label="Роль/прокси"
            disabled={proxyMutation.isPending}
            onChange={v => proxyMutation.mutate({ id: docType.id, allowsProxy: v })} />
          <span className="text-sm text-fg2">Роль/прокси</span>
          <span className="text-xs text-fg4">— тип может подменять другой при генерации</span>
        </label>
        {docType.kind === 'Document' && (
          <label className="flex items-center gap-2.5 select-none">
            <Switch checked={docType.isAbstract} size="sm" label="Абстрактный"
              disabled={abstractMutation.isPending}
              onChange={v => abstractMutation.mutate({ id: docType.id, isAbstract: v })} />
            <span className="text-sm text-fg2">Абстрактный</span>
            <span className="text-xs text-fg4">— нельзя добавить в комплект напрямую</span>
          </label>
        )}
      </div>
      {error && <p className="text-xs text-danger">{error}</p>}
    </form>
  );
}
