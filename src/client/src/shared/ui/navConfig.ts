import { FolderOpen, BookOpen, FileText, Settings, Layers, Database, Tag, ShieldCheck, Users, ScanText, Scale, Bug } from 'lucide-react';

/** Пункты навигации — общий источник для сайдбара (AppShell) и командной палитры (Ctrl+K). */
export interface NavItem { to: string; label: string; icon: typeof FolderOpen }

export const workNav: NavItem[] = [
  { to: '/document-sets', label: 'Стройки',           icon: FolderOpen },
  { to: '/common-data',   label: 'Общие данные',       icon: Database   },
  { to: '/datasets',      label: 'Наборы данных',      icon: Layers     },
  { to: '/quality-docs',  label: 'Документы качества', icon: ShieldCheck },
  { to: '/reconciliations', label: 'Сверка',             icon: Scale      },
];

export const settingsNav: NavItem[] = [
  { to: '/document-types',  label: 'Типы документов', icon: BookOpen  },
  { to: '/composite-types', label: 'Составные типы',  icon: Layers    },
  { to: '/field-types',     label: 'Типы полей',      icon: Tag       },
  { to: '/templates',       label: 'Шаблоны',         icon: FileText  },
  { to: '/recognition-profiles', label: 'Профили распознавания', icon: ScanText },
  { to: '/users',           label: 'Пользователи',    icon: Users     },
  { to: '/bug-reports',     label: 'Сообщения об ошибках', icon: Bug  },
  { to: '/settings',        label: 'Настройки',       icon: Settings  },
];
