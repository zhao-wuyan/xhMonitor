import type { ReactNode } from 'react';
import { useEffect, useRef, useState } from 'react';

interface HoverTooltipProps {
  content: string;
  children: ReactNode;
  delayMs?: number;
  className?: string;
}

export const HoverTooltip = ({ content, children, delayMs = 200, className }: HoverTooltipProps) => {
  const [open, setOpen] = useState(false);
  const [shownContent, setShownContent] = useState('');
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

  const wrapperClassName = className ? `xh-tooltip-wrap ${className}` : 'xh-tooltip-wrap';

  return (
    <span className={wrapperClassName} onMouseEnter={handleMouseEnter} onMouseLeave={handleMouseLeave}>
      {children}
      {open && shownContent ? (
        <span className="xh-tooltip-bubble" role="tooltip">
          {shownContent}
        </span>
      ) : null}
    </span>
  );
};
