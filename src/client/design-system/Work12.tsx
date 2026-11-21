import { useTheme } from '@/shared/ui/themeContext';
import metadata from './work12/metadata.json';

export function Work12() {
  const { resolvedTheme } = useTheme();
  const url = `work12.html?theme=${resolvedTheme}`;
  return <section>
    <p><strong>{metadata.statusLabel}</strong> · Сохранён для дальнейшей работы 22.11.2026</p>
    <p className="catalog-note">Три новых концепции на компонентах текущей дизайн-системы и четвёртый образец из реализованного кода. Варианты, сценарии и тема переключаются внутри стенда. Прежний выбор А+ остаётся в силе.</p>
    <p><a href={url} target="_blank" rel="noreferrer">Открыть четыре макета на всю ширину ↗</a></p>
    <iframe title="work-12 — четыре макета назначений" src={url} style={{ width: '100%', height: 1500, border: '1px solid var(--md-sys-color-outline-variant)' }} />
  </section>;
}
