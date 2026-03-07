import { Children, isValidElement, useCallback, useMemo, useRef, useState } from 'react';
import type { CSSProperties, ReactNode } from 'react';
import { useLayout } from '../contexts/useLayout';
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

  const handlePreviewOrderChange = useCallback(
    (order: string[] | null) => {
      if (!order) {
        setPreviewOrder(null);
        return;
      }

      const stableOrder = layoutState.cardOrder;
      const isStableOrder =
        order.length === stableOrder.length && order.every((id, index) => id === stableOrder[index]);

      setPreviewOrder(isStableOrder ? null : order);
    },
    [layoutState.cardOrder]
  );

  useSortable(containerRef, {
    disabled: !enableDrag,
    mode: layoutState.dragMode,
    onOrderChange: (order) => updateLayout({ cardOrder: order }),
    onPreviewOrderChange: handlePreviewOrderChange,
  });

  const gridStyle = useMemo(
    () =>
      ({
        '--xh-grid-columns': layoutState.gridColumns,
        '--xh-grid-gap': `${layoutState.gaps.grid}px`,
      }) as CSSProperties,
    [layoutState.gridColumns, layoutState.gaps.grid]
  );

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
