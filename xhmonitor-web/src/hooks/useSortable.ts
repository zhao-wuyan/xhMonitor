import { useEffect, useRef } from 'react';
import Sortable from 'sortablejs';
import type { RefObject } from 'react';

export type SortableDragMode = 'sort' | 'swap';

interface UseSortableOptions {
  onOrderChange?: (order: string[]) => void;
  onPreviewOrderChange?: (order: string[]) => void;
  handle?: string;
  disabled?: boolean;
  mode?: SortableDragMode;
}

export const useSortable = (
  containerRef: RefObject<HTMLElement | null>,
  options: UseSortableOptions = {}
) => {
  const sortableRef = useRef<Sortable | null>(null);
  const {
    onOrderChange,
    onPreviewOrderChange,
    handle = '.drag-handle',
    disabled,
    mode = 'sort',
  } = options;
  const onOrderChangeRef = useRef<UseSortableOptions['onOrderChange']>(onOrderChange);
  const onPreviewOrderChangeRef =
    useRef<UseSortableOptions['onPreviewOrderChange']>(onPreviewOrderChange);

  useEffect(() => {
    onOrderChangeRef.current = onOrderChange;
  }, [onOrderChange]);

  useEffect(() => {
    onPreviewOrderChangeRef.current = onPreviewOrderChange;
  }, [onPreviewOrderChange]);

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
        .querySelectorAll<HTMLElement>(
          '.sortable-ghost, .sortable-chosen, .sortable-drag, .xh-sortable-hover-target'
        )
        .forEach((el) => {
          el.classList.remove('sortable-ghost', 'sortable-chosen', 'sortable-drag');
          el.classList.remove('xh-sortable-hover-target');
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

    const getOrderFromContainer = () =>
      Array.from(container.children)
        .map((child) => (child as HTMLElement).dataset.cardId)
        .filter((id): id is string => Boolean(id));

    const swapInOrder = (order: string[], a: string, b: string) => {
      if (a === b) return order;
      const next = order.slice();
      const aIndex = next.indexOf(a);
      const bIndex = next.indexOf(b);
      if (aIndex < 0 || bIndex < 0) return order;
      next[aIndex] = b;
      next[bIndex] = a;
      return next;
    };

    const findTargetCardIdByPoint = (
      clientX: number,
      clientY: number,
      options?: { activeId?: string | null }
    ) => {
      if (typeof document === 'undefined') return null;
      const activeId = options?.activeId ?? draggedCardId;

      const direct = document.elementFromPoint(clientX, clientY) as HTMLElement | null;
      const directCard = direct?.closest?.('[data-card-id]') as HTMLElement | null;
      const directId = directCard?.dataset.cardId;
      if (typeof directId === 'string' && directId) {
        if (!activeId || directId !== activeId) return directId;
        appendDebugLog({
          sid: debugSid,
          hid: 'H4',
          loc: 'xhmonitor-web/src/hooks/useSortable.ts:hover',
          msg: 'Direct hit is active card (ignored)',
          data: {
            activeId,
            pointer: { clientX, clientY },
            directTag: direct?.tagName,
            directClass: direct?.className,
          },
          ts: Date.now(),
        });
      }

      const cards = Array.from(
        container.querySelectorAll<HTMLElement>('[data-card-id]')
      );
      if (cards.length === 0) return null;

      let best: { id: string; dist: number } | null = null;
      for (const card of cards) {
        const id = card.dataset.cardId;
        if (!id) continue;
        if (activeId && id === activeId) continue;
        const rect = card.getBoundingClientRect();
        const dx = Math.max(rect.left - clientX, 0, clientX - rect.right);
        const dy = Math.max(rect.top - clientY, 0, clientY - rect.bottom);
        const dist = Math.hypot(dx, dy);
        if (!best || dist < best.dist) best = { id, dist };
      }

      if (!best) return null;
      if (best.dist > 48) {
        appendDebugLog({
          sid: debugSid,
          hid: 'H4',
          loc: 'xhmonitor-web/src/hooks/useSortable.ts:hover',
          msg: 'Target resolved by nearest card (far)',
          data: {
            activeId,
            targetId: best.id,
            dist: best.dist,
            pointer: { clientX, clientY },
          },
          ts: Date.now(),
        });
      }
      return best.id;
    };

    const applyOrderToContainer = (order: string[]) => {
      order.forEach((id) => {
        const card = container.querySelector<HTMLElement>(`[data-card-id="${id}"]`);
        if (card) {
          container.appendChild(card);
        }
      });
    };

    let dragStartOrder: string[] | null = null;
    let draggedCardId: string | null = null;
    let hoverTargetId: string | null = null;
    let hoverTargetEl: HTMLElement | null = null;
    let moveListenerAttached = false;
    let previewSyncScheduled = false;

    appendDebugLog({
      sid: debugSid,
      hid: 'SYS',
      loc: 'xhmonitor-web/src/hooks/useSortable.ts',
      msg: 'Sortable init',
      data: {
        handle,
        mode,
      },
      ts: Date.now(),
    });

    const clearHoverTarget = () => {
      if (hoverTargetEl) {
        hoverTargetEl.classList.remove('xh-sortable-hover-target');
      }
      hoverTargetEl = null;
      hoverTargetId = null;
    };

    const setHoverTarget = (nextId: string | null) => {
      if (mode !== 'swap') {
        clearHoverTarget();
        return;
      }
      if (!nextId || nextId === draggedCardId) {
        clearHoverTarget();
        return;
      }
      if (nextId === hoverTargetId) return;
      clearHoverTarget();
      const el = container.querySelector<HTMLElement>(`[data-card-id="${nextId}"]`);
      if (!el) return;
      el.classList.add('xh-sortable-hover-target');
      hoverTargetEl = el;
      hoverTargetId = nextId;
      appendDebugLog({
        sid: debugSid,
        hid: 'H4',
        loc: 'xhmonitor-web/src/hooks/useSortable.ts:hover',
        msg: 'Hover target changed',
        data: {
          activeId: draggedCardId,
          targetId: nextId,
          mode,
        },
        ts: Date.now(),
      });
    };

    const schedulePreviewSync = (reason: string) => {
      if (!isDragging) return;
      if (mode !== 'sort') return;
      if (previewSyncScheduled) return;
      if (typeof window === 'undefined') return;
      previewSyncScheduled = true;

      const run = () => {
        previewSyncScheduled = false;
        if (!isDragging) return;
        const order = getOrderFromContainer();
        onPreviewOrderChangeRef.current?.(order);
        appendDebugLog({
          sid: debugSid,
          hid: 'H4',
          loc: 'xhmonitor-web/src/hooks/useSortable.ts:preview',
          msg: 'Preview order synced',
          data: {
            reason,
            order,
          },
          ts: Date.now(),
        });
      };

      try {
        window.requestAnimationFrame(run);
      } catch {
        window.setTimeout(run, 0);
      }
    };

    const handlePointerLikeMove = (event: unknown) => {
      if (!isDragging) return;
      if (mode !== 'swap') return;
      const pointer = getPointerPosition(event);
      if (!pointer) return;
      const targetId = findTargetCardIdByPoint(pointer.clientX, pointer.clientY, {
        activeId: draggedCardId,
      });
      if (targetId) {
        setHoverTarget(targetId);
      }
    };

    const attachMoveListener = () => {
      if (moveListenerAttached || typeof document === 'undefined') return;
      moveListenerAttached = true;
      document.addEventListener('pointermove', handlePointerLikeMove, { passive: true });
      document.addEventListener('touchmove', handlePointerLikeMove, { passive: true });
      document.addEventListener('mousemove', handlePointerLikeMove, { passive: true });
    };

    const detachMoveListener = () => {
      if (!moveListenerAttached || typeof document === 'undefined') return;
      moveListenerAttached = false;
      document.removeEventListener('pointermove', handlePointerLikeMove);
      document.removeEventListener('touchmove', handlePointerLikeMove);
      document.removeEventListener('mousemove', handlePointerLikeMove);
    };

    const sortable = new Sortable(container, {
      animation: 200,
      handle,
      sort: mode === 'sort',
      ghostClass: 'sortable-ghost',
      chosenClass: 'sortable-chosen',
      dragClass: 'sortable-drag',
      forceFallback: true,
      fallbackOnBody: true,
      fallbackTolerance: 8,
      onMove: (_evt, _originalEvent) => {
        schedulePreviewSync('onMove');
      },
      onChange: () => {
        schedulePreviewSync('onChange');
      },
      onStart: (evt) => {
        isDragging = true;
        dragStartOrder = getOrderFromContainer();
        draggedCardId = (evt.item as HTMLElement | undefined)?.dataset.cardId ?? null;
        onPreviewOrderChangeRef.current?.(dragStartOrder ?? []);
        clearHoverTarget();
        if (mode === 'swap') {
          attachMoveListener();
        }
        schedulePreviewSync('onStart');

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
        const startOrder = dragStartOrder ?? getOrderFromContainer();
        const stableOrder = Array.from(new Set(startOrder));
        const activeId = draggedCardId;

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

        const consumeHoverTarget = () => {
          const id = hoverTargetId;
          clearHoverTarget();
          return id;
        };

        if (mode === 'sort') {
          const containerOrder = getOrderFromContainer();
          const uniqueContainerOrder = Array.from(new Set(containerOrder));
          const endOrder =
            uniqueContainerOrder.length === stableOrder.length ? uniqueContainerOrder : stableOrder;

          dragStartOrder = null;
          draggedCardId = null;
          isDragging = false;
          detachMoveListener();
          clearHoverTarget();
          previewSyncScheduled = false;

          if (typeof document !== 'undefined') {
            document.body.classList.remove('is-sorting');
          }

          if (getOrderFromContainer().join('|') !== endOrder.join('|')) {
            applyOrderToContainer(endOrder);
          }
          onPreviewOrderChangeRef.current?.(endOrder);
          onOrderChangeRef.current?.(endOrder);
          window.setTimeout(() => cleanup({ removeOrphans: true }), 0);
          return;
        }

        const targetId =
          pointer && activeId
            ? consumeHoverTarget() ??
              findTargetCardIdByPoint(pointer.clientX, pointer.clientY, { activeId })
            : null;

        dragStartOrder = null;
        draggedCardId = null;
        isDragging = false;
        detachMoveListener();
        clearHoverTarget();
        previewSyncScheduled = false;

        if (typeof document !== 'undefined') {
          document.body.classList.remove('is-sorting');
        }

        if (!pointer || !activeId) {
          if (getOrderFromContainer().join('|') !== stableOrder.join('|')) {
            applyOrderToContainer(stableOrder);
          }
          onPreviewOrderChangeRef.current?.(stableOrder);
          onOrderChangeRef.current?.(stableOrder);
          window.setTimeout(() => cleanup({ removeOrphans: true }), 0);
          return;
        }

        if (!targetId || targetId === activeId) {
          onPreviewOrderChangeRef.current?.(stableOrder);
          onOrderChangeRef.current?.(stableOrder);
          window.setTimeout(() => cleanup({ removeOrphans: true }), 0);
          return;
        }

        const nextOrder = swapInOrder(stableOrder, activeId, targetId);
        appendDebugLog({
          sid: debugSid,
          hid: 'H5',
          loc: 'xhmonitor-web/src/hooks/useSortable.ts:onEnd',
          msg: 'Drop target resolved (swap)',
          data: {
            activeId,
            targetId,
            nextOrder,
          },
          ts: Date.now(),
        });
        onPreviewOrderChangeRef.current?.(nextOrder);
        onOrderChangeRef.current?.(nextOrder);
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
      detachMoveListener();
      clearHoverTarget();
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
  }, [containerRef, handle, disabled, mode]);

  return sortableRef;
};
