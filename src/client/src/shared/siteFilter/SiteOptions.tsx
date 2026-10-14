import { useMemo, useState, type ReactNode } from 'react';
import { SiteRevealContext, REVEAL_CHOICE } from './siteReveal';
import { revealText, type SiteChoices } from './siteChoices';
import type { SiteEntry } from './siteFilterContext';

/**
 * Пункты нативного `<select>` выбора стройки (issue #1219): группы из `useSiteChoices`. Выход —
 * отдельным `RevealOption`, его носитель ставит ПОСЛЕДНИМ пунктом, после своих групп («Вне строек»).
 */
export function SiteOptions<T extends SiteEntry>({ choices, valueOf }: {
  choices: SiteChoices<T>;
  valueOf: (id: string) => string;
}) {
  return choices.groups.map(g => (
    <optgroup key={g.label} label={g.label}>
      {g.sites.map(s => <option key={s.id} value={valueOf(s.id)}>{s.name}</option>)}
    </optgroup>
  ));
}

export function RevealOption({ hidden }: { hidden: number }) {
  return hidden > 0 ? <option value={REVEAL_CHOICE}>{revealText(hidden)}</option> : null;
}

/** Граница формы для выхода «Показать все стройки»: раскрыли в одном списке — раскрыты все в форме. */
export function SiteRevealScope({ children }: { children: ReactNode }) {
  const [revealed, setRevealed] = useState(false);
  const value = useMemo(() => ({ revealed, reveal: () => setRevealed(true) }), [revealed]);
  return <SiteRevealContext.Provider value={value}>{children}</SiteRevealContext.Provider>;
}
