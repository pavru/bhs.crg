import { useState } from 'react';
import { Button, IconButton } from '@/shared/ui/Button';
import { DarkMode, LightMode } from '@/shared/ui/icons';
import { Select, SelectItem } from '@/shared/ui/Select';
import { useTheme } from '@/shared/ui/themeContext';
import { scenarios, variants } from './model';

export function Work19() {
  const { resolvedTheme } = useTheme();
  return <ul className="space-y-3">
    {variants.map(variant => <li key={variant.id}>
      <a className="text-primary underline" href={`work19.html?variant=${variant.id}&theme=${resolvedTheme}`}>{variant.label}</a>
      <p className="text-body-medium text-on-surface-variant">{variant.summary}</p>
    </li>)}
  </ul>;
}

export function Review({ params }: { params: URLSearchParams }) {
  const [variant, setVariant] = useState(variants.find(item => item.id === params.get('variant')) ?? variants[0]);
  const [scenario, setScenario] = useState(scenarios.find(item => item.id === params.get('scenario')) ?? scenarios[0]);
  const { resolvedTheme, setTheme } = useTheme();
  function remember(key: string, value: string) {
    const url = new URL(location.href);
    url.searchParams.set(key, value);
    history.replaceState(history.state, '', url);
  }
  const frame = new URLSearchParams({ canvas: '1', variant: variant.id, scenario: scenario.id, theme: resolvedTheme });

  return <main className="r4-review">
    <header className="r4-heading">
      <div>
        <a className="text-label-large text-primary underline" href={`./?section=work19&theme=${resolvedTheme}`}>BHS.CRG UI</a>
        <h1 className="text-headline-medium">Р4 · Закрытие периода</h1>
      </div>
      <IconButton icon={resolvedTheme === 'dark' ? LightMode : DarkMode}
        label={resolvedTheme === 'dark' ? 'Включить светлую тему' : 'Включить тёмную тему'}
        onClick={() => {
          const next = resolvedTheme === 'dark' ? 'light' : 'dark';
          setTheme(next);
          remember('theme', next);
        }} />
    </header>
    <div className="r4-toolbar">
      <nav className="r4-variants" aria-label="Варианты макета">
        {variants.map(item => <Button key={item.id} variant={variant === item ? 'tonal' : 'text'}
          aria-current={variant === item ? 'page' : undefined}
          onClick={() => { setVariant(item); remember('variant', item.id); }}>{item.label}</Button>)}
      </nav>
      <Select label="Сценарий" density="dense" value={scenario.id} onValueChange={id => {
        const next = scenarios.find(item => item.id === id);
        if (next) { setScenario(next); remember('scenario', next.id); }
      }}>
        {scenarios.map(item => <SelectItem key={item.id} value={item.id}>{item.label}</SelectItem>)}
      </Select>
    </div>
    <p className="r4-summary text-body-medium text-on-surface-variant">{variant.summary} Данные демонстрационные.</p>
    <iframe title={`Диалог закрытия периода · ${variant.label}`} src={`?${frame}`} />
  </main>;
}
