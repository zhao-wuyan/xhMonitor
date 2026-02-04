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
  const onOrderChangeRef = useRef<UseSortableOptions['onOrderChange']>(onOrderChange);

  useEffect(() => {
    onOrderChangeRef.current = onOrderChange;
  }, [onOrderChange]);

  useEffect(() => {
    const container = containerRef.current;
    if (!container) return;

    const debugSid = 'DBG-web-drag-2026-02-04';
    const debugKey = '__xh_sortable_debug_ndjson__';
    const isDebugEnabled = () => {
      if (typeof window === 'undefined') return false;
      try {
        return window.localStorage.getItem('xh.debug.sortable') === '1';
      } catch {
        return false;
      }
    };

    const appendDebugLog = (entry: Record<string, unknown>) => {
      if (!isDebugEnabled()) return;
      try {
        const w = window as unknown as Record<string, unknown>;
        if (typeof w.__xhExportSortableDebug !== 'function') {
          w.__xhExportSortableDebug = () => {
            try {
              const content = typeof w[debugKey] === 'string' ? (w[debugKey] as string) : '';
              const blob = new Blob([content + (content ? '\n' : '')], { type: 'application/x-ndjson' });
              const url = URL.createObjectURL(blob);
              const a = document.createElement('a');
              a.href = url;
              a.download = `${debugSid}-debug.log`;
              a.rel = 'noopener';
              a.click();
              window.setTimeout(() => URL.revokeObjectURL(url), 1000);
            } catch {
              // ignore
            }
          };
        }

        const line = JSON.stringify(entry);
        const prev = typeof w[debugKey] === 'string' ? (w[debugKey] as string) : '';
        w[debugKey] = prev ? `${prev}\n${line}` : line;
        console.debug('[xh-sortable]', entry);
      } catch {
        // ignore
      }
    };

    if (disabled) {
      appendDebugLog({
        sid: debugSid,
        hid: 'SYS',
        loc: 'xhmonitor-web/src/hooks/useSortable.ts',
        msg: 'Sortable disabled -> destroy',
        data: {},
        ts: Date.now(),
      });
      sortableRef.current?.destroy();
      sortableRef.current = null;
      if (typeof document !== 'undefined') {
        document.body.classList.remove('is-sorting');
      }
      return;
    }

    if (sortableRef.current) {
      appendDebugLog({
        sid: debugSid,
        hid: 'SYS',
        loc: 'xhmonitor-web/src/hooks/useSortable.ts',
        msg: 'Sortable re-init -> destroy previous',
        data: {},
        ts: Date.now(),
      });
      sortableRef.current.destroy();
      sortableRef.current = null;
    }

    let isDragging = false;

    const cleanup = (options?: { removeOrphans?: boolean }) => {
      appendDebugLog({
        sid: debugSid,
        hid: 'H2',
        loc: 'xhmonitor-web/src/hooks/useSortable.ts:cleanup',
        msg: 'Cleanup sortable DOM artifacts',
        data: {
          containerChildren: container.children.length,
          isDragging,
          removeOrphans: Boolean(options?.removeOrphans),
        },
        ts: Date.now(),
      });

      container
        .querySelectorAll<HTMLElement>('.sortable-ghost, .sortable-chosen, .sortable-drag')
        .forEach((el) => {
          el.classList.remove('sortable-ghost', 'sortable-chosen', 'sortable-drag');
          el.style.removeProperty('opacity');
          el.style.removeProperty('transform');
        });

      if (typeof document !== 'undefined') {
        if (options?.removeOrphans && !isDragging) {
          const orphaned = Array.from(
            document.querySelectorAll<HTMLElement>('.sortable-ghost, .sortable-chosen, .sortable-drag')
          ).filter((el) => !container.contains(el));

          appendDebugLog({
            sid: debugSid,
            hid: 'H2',
            loc: 'xhmonitor-web/src/hooks/useSortable.ts:cleanup',
            msg: 'Orphaned sortable elements outside container',
            data: {
              count: orphaned.length,
              classes: orphaned.slice(0, 5).map((el) => el.className),
            },
            ts: Date.now(),
          });

          orphaned.forEach((el) => {
            el.parentElement?.removeChild(el);
          });
        }
      }
    };

    const getPointerPosition = (
      event: unknown
    ): { clientX: number; clientY: number } | null => {
      if (!event || typeof event !== 'object') return null;

      if ('clientX' in event && 'clientY' in event) {
        const clientX = (event as { clientX: unknown }).clientX;
        const clientY = (event as { clientY: unknown }).clientY;
        if (typeof clientX === 'number' && typeof clientY === 'number') {
          return { clientX, clientY };
        }
      }

      if ('touches' in event) {
        const touches = (event as { touches: unknown }).touches;
        if (Array.isArray(touches) && touches.length > 0) {
          const first = touches[0] as { clientX?: unknown; clientY?: unknown };
          if (typeof first.clientX === 'number' && typeof first.clientY === 'number') {
            return { clientX: first.clientX, clientY: first.clientY };
          }
        }
      }

      if ('changedTouches' in event) {
        const touches = (event as { changedTouches: unknown }).changedTouches;
        if (Array.isArray(touches) && touches.length > 0) {
          const first = touches[0] as { clientX?: unknown; clientY?: unknown };
          if (typeof first.clientX === 'number' && typeof first.clientY === 'number') {
            return { clientX: first.clientX, clientY: first.clientY };
          }
        }
      }

      return null;
    };

    const moveItem = (order: string[], from: number, to: number) => {
      if (from === to) return order;
      const next = order.slice();
      const [removed] = next.splice(from, 1);
      next.splice(to, 0, removed);
      return next;
    };

    let dragStartOrder: string[] | null = null;
    let draggedCardId: string | null = null;

    appendDebugLog({
      sid: debugSid,
      hid: 'SYS',
      loc: 'xhmonitor-web/src/hooks/useSortable.ts',
      msg: 'Sortable init',
      data: {
        handle,
      },
      ts: Date.now(),
    });

    const sortable = new Sortable(container, {
      animation: 200,
      handle,
      sort: false,
      ghostClass: 'sortable-ghost',
      chosenClass: 'sortable-chosen',
      dragClass: 'sortable-drag',
      forceFallback: true,
      fallbackOnBody: true,
      fallbackTolerance: 8,
      onStart: (evt) => {
        isDragging = true;
        dragStartOrder = Array.from(container.children)
          .map((child) => (child as HTMLElement).dataset.cardId)
          .filter((id): id is string => Boolean(id));
        draggedCardId = (evt.item as HTMLElement | undefined)?.dataset.cardId ?? null;

        appendDebugLog({
          sid: debugSid,
          hid: 'H1',
          loc: 'xhmonitor-web/src/hooks/useSortable.ts:onStart',
          msg: 'Drag started',
          data: {
            draggedCardId,
            startOrder: dragStartOrder,
          },
          ts: Date.now(),
        });

        if (typeof document !== 'undefined') {
          document.body.classList.add('is-sorting');
        }
      },
      onEnd: (evt) => {
        const startOrder = dragStartOrder ??
          Array.from(container.children)
            .map((child) => (child as HTMLElement).dataset.cardId)
            .filter((id): id is string => Boolean(id));

        const stableOrder = Array.from(new Set(startOrder));
        const activeId = draggedCardId;

        dragStartOrder = null;
        draggedCardId = null;
        isDragging = false;
        if (typeof document !== 'undefined') {
          document.body.classList.remove('is-sorting');
        }

        const originalEvent = (evt as unknown as { originalEvent?: unknown }).originalEvent;
        const pointer = getPointerPosition(originalEvent);
        appendDebugLog({
          sid: debugSid,
          hid: 'H3',
          loc: 'xhmonitor-web/src/hooks/useSortable.ts:onEnd',
          msg: 'Drag ended (commit on drop)',
          data: {
            activeId,
            stableOrder,
            pointer,
          },
          ts: Date.now(),
        });

        if (!pointer || !activeId) {
          onOrderChangeRef.current?.(stableOrder);
          window.setTimeout(() => cleanup({ removeOrphans: true }), 0);
          return;
        }

        const rect = container.getBoundingClientRect();
        const inSwapZone =
          pointer.clientX >= rect.left &&
          pointer.clientX <= rect.right &&
          pointer.clientY >= rect.top &&
          pointer.clientY <= rect.bottom;

        if (!inSwapZone) {
          onOrderChangeRef.current?.(stableOrder);
          window.setTimeout(() => cleanup({ removeOrphans: true }), 0);
          return;
        }

        const fromIndex = stableOrder.indexOf(activeId);
        if (fromIndex === -1) {
          onOrderChangeRef.current?.(stableOrder);
          window.setTimeout(() => cleanup({ removeOrphans: true }), 0);
          return;
        }

        let targetIndex: number | null = null;
        const elAtPoint = document.elementFromPoint(pointer.clientX, pointer.clientY);
        const cardEl = elAtPoint?.closest?.('[data-card-id]') as HTMLElement | null;
        if (cardEl && container.contains(cardEl)) {
          targetIndex = Array.from(container.children).indexOf(cardEl);
        } else {
          const children = Array.from(container.children) as HTMLElement[];
          const closest = children
            .map((child, index) => {
              const childRect = child.getBoundingClientRect();
              const centerX = (childRect.left + childRect.right) / 2;
              const centerY = (childRect.top + childRect.bottom) / 2;
              const dx = pointer.clientX - centerX;
              const dy = pointer.clientY - centerY;
              return { index, dist: dx * dx + dy * dy };
            })
            .sort((a, b) => a.dist - b.dist)[0];
          targetIndex = closest ? closest.index : null;
        }

        if (targetIndex == null || targetIndex < 0 || targetIndex >= stableOrder.length) {
          onOrderChangeRef.current?.(stableOrder);
          window.setTimeout(() => cleanup({ removeOrphans: true }), 0);
          return;
        }

        onOrderChangeRef.current?.(moveItem(stableOrder, fromIndex, targetIndex));
        window.setTimeout(() => cleanup({ removeOrphans: true }), 0);
      },
      onUnchoose: () => {
        // Sortable 在某些情况下会在拖拽进行中触发 unchoose。
        // 此处仅清理容器内残留 class，不清理 body 上的拖拽节点，避免把拖拽元素误删导致“脱离拖拽”。
        cleanup({ removeOrphans: false });
      },
    });

    sortableRef.current = sortable;

    return () => {
      isDragging = false;
      if (typeof document !== 'undefined') {
        document.body.classList.remove('is-sorting');
      }
      appendDebugLog({
        sid: debugSid,
        hid: 'SYS',
        loc: 'xhmonitor-web/src/hooks/useSortable.ts',
        msg: 'Sortable destroy (effect cleanup)',
        data: {},
        ts: Date.now(),
      });
      cleanup({ removeOrphans: true });
      sortable.destroy();
      sortableRef.current = null;
    };
  }, [containerRef, handle, disabled]);

  return sortableRef;
};
