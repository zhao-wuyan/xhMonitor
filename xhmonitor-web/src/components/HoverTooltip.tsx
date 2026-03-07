import type { ReactNode } from 'react';
import { useEffect, useRef, useState } from 'react';
import { createPortal } from 'react-dom';

interface HoverTooltipProps {
  content: string;
  children: ReactNode;
  delayMs?: number;
  className?: string;
}

export const HoverTooltip = ({ content, children, delayMs = 200, className }: HoverTooltipProps) => {
  const wrapRef = useRef<HTMLSpanElement | null>(null);
  const [open, setOpen] = useState(false);
  const [shownContent, setShownContent] = useState('');
  const [pos, setPos] = useState<{ left: number; top: number } | null>(null);
  const timerRef = useRef<number | null>(null);
  const contentRef = useRef(content);

  useEffect(() => {
    contentRef.current = content;
  }, [content]);

  const clearTimer = () => {
    if (timerRef.current == null) return;
    window.clearTimeout(timerRef.current);
    timerRef.current = null;
  };

  const handleMouseEnter = () => {
    clearTimer();
    timerRef.current = window.setTimeout(() => {
      const anchor = wrapRef.current;
      if (!anchor) return;

      const rect = anchor.getBoundingClientRect();
      setPos({ left: rect.left + rect.width / 2, top: rect.top - 8 });
      setShownContent(contentRef.current);
      setOpen(true);
    }, delayMs);
  };

  const handleMouseLeave = () => {
    clearTimer();
    setOpen(false);
  };

  useEffect(() => {
    return () => clearTimer();
  }, []);

  useEffect(() => {
    if (!open) {
      setPos(null);
      return;
    }

    const close = () => setOpen(false);
    window.addEventListener('scroll', close, true);
    window.addEventListener('resize', close);
    return () => {
      window.removeEventListener('scroll', close, true);
      window.removeEventListener('resize', close);
    };
  }, [open]);

  const wrapperClassName = className ? `xh-tooltip-wrap ${className}` : 'xh-tooltip-wrap';

  return (
    <span ref={wrapRef} className={wrapperClassName} onMouseEnter={handleMouseEnter} onMouseLeave={handleMouseLeave}>
      {children}
      {open && shownContent && pos
        ? createPortal(
            <span className="xh-tooltip-bubble" role="tooltip" style={{ left: pos.left, top: pos.top }}>
              {shownContent}
            </span>,
            document.body
          )
        : null}
    </span>
  );
};
