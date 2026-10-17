import { Monitor, Moon, Sun } from 'lucide-react';
import type { Theme } from '@/shared/ui/themeContext';

/**
 * Значения темы — одни на оба места выбора: переключатель профиля и меню профиля в шапке
 * (issue #1430). Подписи, порядок и набор берутся отсюда, иначе два выбора разошлись бы молча.
 *
 * «Авто», а не «Системная» (#1430): значок монитора сам по себе «как в системе» не читается, а
 * подпись словом стоит теперь рядом со значком. Что значит «авто», говорит `hint`.
 */
export const THEME_OPTIONS: { value: Theme; icon: typeof Sun; label: string; hint?: string }[] = [
  { value: 'light',  icon: Sun,     label: 'Светлая' },
  { value: 'dark',   icon: Moon,    label: 'Тёмная'  },
  { value: 'system', icon: Monitor, label: 'Авто', hint: 'как в системе' },
];
