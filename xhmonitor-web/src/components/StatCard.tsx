import { memo, useMemo } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { t } from '../i18n';

interface StatCardProps {
  cardId: string;
  title: string;
  value: string | number;
  unit?: string;
  subtitle?: string;
  trend?: string;
  subtitles?: string[];
  temperature?: number;
  accentColor: string;
  valueSize?: 'normal' | 'small';
  className?: string;
  children?: ReactNode;
  showDragHandle?: boolean;
}

const StatCardBase = ({
  cardId,
  title,
  value,
  unit,
  subtitle,
  trend,
  subtitles,
  temperature,
  accentColor,
  valueSize = 'normal',
  className,
  children,
  showDragHandle = true,
}: StatCardProps) => {
  const subtitleItems = useMemo(() => {
    const items: string[] = [];
    if (subtitle) items.push(subtitle);
    if (trend) items.push(trend);
    if (subtitles?.length) items.push(...subtitles);
    return items;
  }, [subtitle, trend, subtitles]);

  const cardStyle = useMemo(
    () => ({
      '--xh-card-accent': accentColor,
    }) as CSSProperties,
    [accentColor]
  );

  return (
    <div
      className={`xh-stat-card xh-glass-panel ${className ?? ''}`.trim()}
      data-card-id={cardId}
      style={cardStyle}
    >
      <div className="xh-stat-card__glow" style={{ background: 'var(--xh-card-accent)' }} />
      {showDragHandle && (
        <button
          className="drag-handle"
          type="button"
          aria-label={t('Drag to reorder')}
          title={t('Drag to reorder')}
        />
      )}
      <div className="xh-stat-card__info">
        <div className="xh-stat-card__label">
          <span
            className="xh-stat-card__label-indicator"
            style={{ color: 'var(--xh-card-accent)' }}
          >
            ●
          </span>
          {title}
          {temperature !== undefined && (
            <span className="xh-stat-card__label-temp">
              · {Number.isFinite(temperature) ? temperature.toFixed(1) : '-'}°C
            </span>
          )}
        </div>
        <div
          className={`xh-stat-card__value ${
            valueSize === 'small' ? 'xh-stat-card__value--small' : ''
          }`.trim()}
        >
          {value}
          {unit ? ` ${unit}` : ''}
        </div>
        {subtitleItems.map((item, index) => (
          <div key={`${cardId}-subtitle-${index}`} className="xh-stat-card__subtitle">
            {item}
          </div>
        ))}
      </div>
      {children}
    </div>
  );
};

const areEqual = (prev: StatCardProps, next: StatCardProps) => {
  return (
    prev.cardId === next.cardId &&
    prev.title === next.title &&
    prev.value === next.value &&
    prev.unit === next.unit &&
    prev.subtitle === next.subtitle &&
    prev.trend === next.trend &&
    prev.temperature === next.temperature &&
    prev.accentColor === next.accentColor &&
    prev.valueSize === next.valueSize &&
    prev.className === next.className &&
    prev.showDragHandle === next.showDragHandle &&
    prev.children === next.children &&
    prev.subtitles === next.subtitles
  );
};

export const StatCard = memo(StatCardBase, areEqual);
