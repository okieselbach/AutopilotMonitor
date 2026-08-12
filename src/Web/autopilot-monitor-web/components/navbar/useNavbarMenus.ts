"use client";

import { useCallback, useEffect, useRef, useState } from 'react';

export type NavbarMenuId = 'notifications' | 'user' | 'settings' | 'help' | 'overflow';

export interface NavbarMenus {
  /** The single menu that is open right now — at most one, by construction. */
  openMenu: NavbarMenuId | null;
  isOpen: (id: NavbarMenuId) => boolean;
  toggle: (id: NavbarMenuId) => void;
  close: () => void;
  /** Ref callback for a menu's container (trigger + dropdown). Enables outside-click close. */
  containerRef: (id: NavbarMenuId) => (el: HTMLDivElement | null) => void;
}

/**
 * One piece of state for all navbar dropdowns. Replaces per-menu booleans so
 * two menus can never be open at once, and adding a menu means adding an id —
 * not a new useState + useRef + branch in a click-outside handler.
 */
export function useNavbarMenus(): NavbarMenus {
  const [openMenu, setOpenMenu] = useState<NavbarMenuId | null>(null);
  const containers = useRef<Partial<Record<NavbarMenuId, HTMLDivElement | null>>>({});

  const containerRef = useCallback(
    (id: NavbarMenuId) => (el: HTMLDivElement | null) => {
      containers.current[id] = el;
    },
    [],
  );

  const toggle = useCallback((id: NavbarMenuId) => {
    setOpenMenu((prev) => (prev === id ? null : id));
  }, []);

  const close = useCallback(() => setOpenMenu(null), []);

  const isOpen = useCallback((id: NavbarMenuId) => openMenu === id, [openMenu]);

  // Close the open menu on any click outside its container. The listener only
  // exists while a menu is open.
  useEffect(() => {
    if (!openMenu) return;
    function handleMouseDown(event: MouseEvent) {
      const container = containers.current[openMenu!];
      if (container && !container.contains(event.target as Node)) {
        setOpenMenu(null);
      }
    }
    document.addEventListener('mousedown', handleMouseDown);
    return () => document.removeEventListener('mousedown', handleMouseDown);
  }, [openMenu]);

  return { openMenu, isOpen, toggle, close, containerRef };
}
