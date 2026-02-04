import { Children, isValidElement, useEffect, useMemo, useRef, useState } from 'react';
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
  const [previewOrder, setPreviewOrder] = useState<string[] | null>(null);

  useSortable(containerRef, {
    disabled: !enableDrag,
    mode: layoutState.dragMode,
    onOrderChange: (order) => updateLayout({ cardOrder: order }),
    onPreviewOrderChange: (order) => setPreviewOrder(order),
  });

  const gridStyle = useMemo(
    () =>
      ({
        '--xh-grid-columns': layoutState.gridColumns,
        '--xh-grid-gap': `${layoutState.gaps.grid}px`,
      }) as CSSProperties,
    [layoutState.gridColumns, layoutState.gaps.grid]
  );

  useEffect(() => {
    if (!previewOrder) return;
    const stableOrder = layoutState.cardOrder;
    if (
      previewOrder.length === stableOrder.length &&
      previewOrder.every((id, index) => id === stableOrder[index])
    ) {
      setPreviewOrder(null);
    }
  }, [previewOrder, layoutState.cardOrder]);

  const orderedChildren = useMemo(() => {
    const items = Children.toArray(children);
    if (!previewOrder || previewOrder.length === 0) return items;

    const map = new Map<string, ReactNode>();
    const rest: ReactNode[] = [];

    items.forEach((child) => {
      if (!isValidElement(child)) {
        rest.push(child);
        return;
      }
      const props = child.props as { cardId?: unknown };
      if (typeof props.cardId === 'string') {
        map.set(props.cardId, child);
      } else {
        rest.push(child);
      }
    });

    const ordered = previewOrder
      .map((id) => map.get(id))
      .filter((item): item is ReactNode => Boolean(item));

    map.forEach((value, key) => {
      if (!previewOrder.includes(key)) ordered.push(value);
    });

    return ordered.concat(rest);
  }, [children, previewOrder]);

  return (
    <section
      ref={containerRef}
      className={`stats-grid ${className ?? ''}`.trim()}
      style={gridStyle}
    >
      {orderedChildren}
    </section>
  );
};
