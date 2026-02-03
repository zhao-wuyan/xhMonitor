import { useMemo, useRef } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { useLayout } from '../contexts/LayoutContext';
import { useSortable } from '../hooks/useSortable';

interface DraggableGridProps {
  children: ReactNode;
  className?: string;
  enableDrag?: boolean;
}

export const DraggableGrid = ({ children, className, enableDrag = true }: DraggableGridProps) => {
  const { layoutState, updateLayout } = useLayout();
  const containerRef = useRef<HTMLElement | null>(null);

  useSortable(containerRef, {
    disabled: !enableDrag,
    onOrderChange: (order) => updateLayout({ cardOrder: order }),
  });

  const gridStyle = useMemo(
    () =>
      ({
        '--xh-grid-columns': layoutState.gridColumns,
        '--xh-grid-columns-effective': layoutState.gridColumns,
        '--xh-grid-gap': `${layoutState.gaps.grid}px`,
      }) as CSSProperties,
    [layoutState.gridColumns, layoutState.gaps.grid]
  );

  return (
    <section
      ref={containerRef}
      className={`stats-grid ${className ?? ''}`.trim()}
      style={gridStyle}
    >
      {children}
    </section>
  );
};
