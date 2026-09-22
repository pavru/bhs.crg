import { Lock } from 'lucide-react';
import type { DocumentType } from '@/shared/api/types';
import { ownerTitle } from '@/shared/api/typeOwners';
import { NO_ACCESS, useAccess } from '@/shared/api/access';

/**
 * Уровень правки схемы — СЛОВАМИ, до первой правки (issue #958, ТЗ CORE-19.1).
 *
 * Зачем полоса, а если коротко — зачем вообще. Отказы уровня приходят при сохранении, то есть
 * ПОСЛЕ того, как труд потрачен: администратор переименовал поле модуля, разложил его по группам и
 * только тогда узнал, что так нельзя. Одна полоса называет правило заранее и объясняет все отказы
 * разом; без неё каждый отказ читается как поломка.
 *
 * Показывается только у типа, где ограничение ЕСТЬ. У открытых (а сегодня это все типы) экран не
 * меняется вовсе: полоса «вам всё можно» — шум, который научатся не замечать, и вместе с ней
 * перестанут замечать настоящую.
 */
export function SchemaLevelBanner({ level, module }: { level: DocumentType['editLevel']; module: string }) {
  const { data: access } = useAccess();
  if (level === 'Open') return null;

  // Название модуля берём из прав: выключенный модуль в них не назван, и тогда остаётся его
  // код — это честнее, чем придумать название, которого система не знает.
  const owner = ownerTitle(module, access ?? NO_ACCESS);
  const what = level === 'Closed'
    ? 'Схему задаёт модуль. Вам остаётся подпись поля — и печатная форма, её вы рисуете без ограничений.'
    : 'Можно добавлять свои НЕОБЯЗАТЕЛЬНЫЕ поля и править их. Поля модуля заперты: их ведёт он сам.';

  return (
    <div className="flex items-start gap-2 rounded-md border border-info/50 bg-info/10 px-3 py-2">
      <Lock size={14} className="text-info shrink-0 mt-0.5" />
      <p className="text-xs text-fg2">
        <span className="font-medium">Тип модуля «{owner}»{level === 'Closed' ? ' — закрытый' : ' — расширяемый'}.</span>{' '}
        {what}
      </p>
    </div>
  );
}
