import { useEffect, useRef } from 'react';
import Sortable from 'sortablejs';
import type { RefObject } from 'react';

interface UseSortableOptions {
  onOrderChange?: (order: string[]) => void;
  handle?: string;
  disabled?: boolean;
}

export const useSortable = (
  containerRef: RefObject<HTMLElement | null>,
  options: UseSortableOptions = {}
) => {
  const sortableRef = useRef<Sortable | null>(null);
  const { onOrderChange, handle = '.drag-handle', disabled } = options;

  useEffect(() => {
    const container = containerRef.current;
    if (!container || disabled) return;

    if (sortableRef.current) {
      sortableRef.current.destroy();
      sortableRef.current = null;
    }

    const sortable = new Sortable(container, {
      animation: 150,
      handle,
      ghostClass: 'sortable-ghost',
      chosenClass: 'sortable-chosen',
      dragClass: 'sortable-drag',
      forceFallback: true,
      onEnd: () => {
        const order = Array.from(container.children)
          .map((child) => (child as HTMLElement).dataset.cardId)
          .filter((id): id is string => Boolean(id));
        onOrderChange?.(order);
      },
    });

    sortableRef.current = sortable;

    return () => {
      sortable.destroy();
      sortableRef.current = null;
    };
  }, [containerRef, onOrderChange, handle, disabled]);

  return sortableRef;
};
