import type { ReactNode } from 'react';

interface MobileNavItem {
  key: string;
  label: string;
  icon: ReactNode;
}

interface MobileNavProps {
  items?: MobileNavItem[];
  activeKey?: string;
  onChange?: (key: string) => void;
  className?: string;
}

const defaultItems: MobileNavItem[] = [
  {
    key: 'monitor',
    label: 'Monitor',
    icon: (
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <path d="M3 13h8V3H3v10zm0 8h8v-6H3v6zm10 0h8V11h-8v10zm0-18v6h8V3h-8z" />
      </svg>
    ),
  },
  {
    key: 'hardware',
    label: 'Hardware',
    icon: (
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <path d="M15 9H9v6h6V9zm-2 4h-2v-2h2v2zm8-2V9h-2V7c0-1.1-.9-2-2-2h-2V3h-2v2h-2V3H9v2H7c-1.1 0-2 .9-2 2v2H3v2h2v2H3v2h2v2c0 1.1.9 2 2 2h2v2h2v-2h2v2h2v-2h2c1.1 0 2-.9 2-2v-2h2v-2h-2zm-4 6H7V7h10v10z" />
      </svg>
    ),
  },
  {
    key: 'settings',
    label: 'Settings',
    icon: (
      <svg viewBox="0 0 24 24" aria-hidden="true">
        <path d="M19.43 12.98c.04-.32.07-.64.07-.98s-.03-.66-.07-.98l2.11-1.65c.19-.15.24-.42.12-.64l-2-3.46c-.12-.22-.39-.3-.61-.22l-2.49 1c-.52-.4-1.08-.73-1.69-.98l-.38-2.65C14.46 2.18 14.25 2 14 2h-4c-.25 0-.46.18-.49.42l-.38 2.65c-.61.25-1.17.59-1.69.98l-2.49-1c-.23-.09-.49 0-.61.22l-2 3.46c-.13.22-.07.49.12.64l2.11 1.65c-.04.32-.07.65-.07.98s.03.66.07.98l-2.11 1.65c-.19.15-.24.42-.12.64l2 3.46c.12.22.39.3.61.22l2.49-1c.52.4 1.08.73 1.69.98l.38 2.65c.03.24.24.42.49.42h4c.25 0 .46-.18.49-.42l.38-2.65c.61-.25 1.17-.59 1.69-.98l2.49 1c.23.09.49 0 .61-.22l2-3.46c.12-.22.07-.49-.12-.64l-2.11-1.65zM12 15.5c-1.93 0-3.5-1.57-3.5-3.5s1.57-3.5 3.5-3.5 3.5 1.57 3.5 3.5-1.57 3.5-3.5 3.5z" />
      </svg>
    ),
  },
];

export const MobileNav = ({ items = defaultItems, activeKey, onChange, className }: MobileNavProps) => {
  return (
    <nav className={`mobile-nav ${className ?? ''}`.trim()} role="navigation">
      {items.map((item) => {
        const isActive = item.key === activeKey;
        return (
          <button
            key={item.key}
            type="button"
            className={`nav-icon ${isActive ? 'active' : ''}`.trim()}
            onClick={() => onChange?.(item.key)}
          >
            {item.icon}
            <span>{item.label}</span>
          </button>
        );
      })}
    </nav>
  );
};
