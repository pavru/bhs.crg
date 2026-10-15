import { useMemo, useState, type ReactNode } from 'react';
import { SelectGroup, SelectItem } from '@/shared/ui/Select';
import { SiteRevealContext, REVEAL_CHOICE } from './siteReveal';
import { revealText, type SiteChoices } from './siteChoices';
import type { SiteEntry } from './siteFilterContext';

/**
 * Пункты списка выбора стройки (issue #1219) для `Select`: группы из `useSiteChoices`. Выход —
 * отдельным `RevealItem`, его носитель ставит ПОСЛЕДНИМ пунктом, после своих групп («Вне строек»),
 * и держит список открытым после него: `stayOpenOn={REVEAL_CHOICE}`.
 */
export function SiteSelectItems<T extends SiteEntry>({ choices, valueFor = id => id }: {
  choices: SiteChoices<T>;
  valueFor?: (id: string) => string;
}) {
  return choices.groups.map(g => (
    <SelectGroup key={g.label} label={g.label}>
      {g.sites.map(s => <SelectItem key={s.id} value={valueFor(s.id)}>{s.name}</SelectItem>)}
    </SelectGroup>
  ));
}

export function RevealItem({ hidden }: { hidden: number }) {
  return hidden > 0 ? <SelectItem value={REVEAL_CHOICE}>{revealText(hidden)}</SelectItem> : null;
}

/** Граница формы для выхода «Показать все стройки»: раскрыли в одном списке — раскрыты все в форме. */
export function SiteRevealScope({ children }: { children: ReactNode }) {
  const [revealed, setRevealed] = useState(false);
  const value = useMemo(() => ({ revealed, reveal: () => setRevealed(true) }), [revealed]);
  return <SiteRevealContext.Provider value={value}>{children}</SiteRevealContext.Provider>;
}
