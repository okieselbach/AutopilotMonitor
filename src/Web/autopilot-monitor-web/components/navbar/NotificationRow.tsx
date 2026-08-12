"use client";

import Link from 'next/link';
import { trustedRoute } from '@/lib/routes';
import { formatRelativeTime } from '@/lib/navbarPolicy';
import { MenuIcon } from './icons';

/**
 * Visual lane of a notification row. Tenant and global (GA) rows carry an
 * accent border + tinted background; ephemeral rows tint only while unread.
 */
export type NotificationLane = 'tenant' | 'global' | 'ephemeral';

const LANE_CLASSES: Record<NotificationLane, string> = {
  tenant:
    'hover:bg-blue-50/50 dark:hover:bg-blue-900/45 border-l-4 border-blue-500 bg-blue-50/30 dark:bg-blue-900/20',
  global:
    'hover:bg-purple-50/50 dark:hover:bg-purple-900/45 border-l-4 border-purple-500 bg-purple-50/30 dark:bg-purple-900/20',
  ephemeral: 'hover:bg-gray-50',
};

export interface NotificationRowProps {
  lane: NotificationLane;
  icon: string;
  title: string;
  message: string;
  createdAt: Date;
  now: Date;
  href?: string;
  /** Ephemeral only: unread rows are tinted and clicking marks them read. */
  unread?: boolean;
  /** Show the GA badge next to the title (global lane). */
  showGaBadge?: boolean;
  canDismiss: boolean;
  onDismiss: () => void;
  /** Row / "View" link activation: mark-read side effects + close + navigate live in the caller. */
  onOpen: () => void;
  onViewLinkClick: () => void;
}

/**
 * One notification row for all three lanes of the bell dropdown. The previous
 * navbar kept three hand-copied variants of this block that drifted apart.
 */
export function NotificationRow(props: NotificationRowProps) {
  const {
    lane,
    icon,
    title,
    message,
    createdAt,
    now,
    href,
    unread,
    showGaBadge,
    canDismiss,
    onDismiss,
    onOpen,
    onViewLinkClick,
  } = props;

  const clickable = lane === 'ephemeral' || !!href;
  const laneClass = LANE_CLASSES[lane];
  const unreadClass = lane === 'ephemeral' && unread ? 'bg-blue-50' : '';

  return (
    <div
      className={`px-4 py-3 transition-colors ${laneClass} ${unreadClass} ${clickable ? 'cursor-pointer' : ''}`}
      onClick={() => {
        if (clickable) onOpen();
      }}
    >
      <div className="flex items-start justify-between">
        <div className="flex items-start space-x-2.5 flex-1">
          <span className="text-lg">{icon}</span>
          <div className="flex-1 min-w-0">
            {showGaBadge ? (
              <div className="flex items-center gap-1.5">
                <p className="text-sm font-medium text-gray-900">{title}</p>
                <span className="text-[9px] font-semibold text-purple-700 bg-purple-100 px-1 py-0.5 rounded">
                  GA
                </span>
              </div>
            ) : (
              <p className="text-sm font-medium text-gray-900">{title}</p>
            )}
            <p className="text-xs text-gray-600 mt-0.5">{message}</p>
            <div className="flex items-center gap-3 mt-1">
              <p className="text-[10px] text-gray-400">{formatRelativeTime(createdAt, now)}</p>
              {href && (
                <Link
                  href={trustedRoute(href)}
                  prefetch={false}
                  onClick={(e) => {
                    e.stopPropagation();
                    onViewLinkClick();
                  }}
                  className="text-[10px] text-green-700 hover:text-green-800 font-medium underline"
                >
                  View
                </Link>
              )}
            </div>
          </div>
        </div>
        {canDismiss && (
          <button
            onClick={(e) => {
              e.stopPropagation();
              onDismiss();
            }}
            className="ml-2 text-gray-300 hover:text-gray-500"
            title={lane === 'ephemeral' ? undefined : 'Dismiss'}
          >
            <MenuIcon name="close" className="w-3.5 h-3.5" />
          </button>
        )}
      </div>
    </div>
  );
}
