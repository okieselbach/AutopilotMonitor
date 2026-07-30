"use client";

import { createContext, useContext, useState, useCallback, ReactNode } from "react";
import type { Route } from "next";
import { useSidebarState, CollapseState } from "../hooks/useSidebarState";

export interface PageSectionItem {
  id: string;
  label: string;
  icon?: ReactNode;
  href?: Route;
  /** Optional group name — items sharing the same group are rendered under a collapsible header */
  group?: string;
  /** Icon for the group header (only needs to be set on the first item of a group) */
  groupIcon?: ReactNode;
}

interface SidebarContextValue {
  // Collapse state (shared across app)
  collapseState: CollapseState;
  setCollapseState: (state: CollapseState) => void;
  cycleCollapseState: () => void;

  // Page sections (set by individual pages)
  pageSections: PageSectionItem[];
  pageSectionsTitle: string;
  pageSectionsMode: "scroll-spy" | "route";
  setPageSections: (items: PageSectionItem[], title: string, mode: "scroll-spy" | "route") => void;
  clearPageSections: () => void;

  // Mobile drawer
  mobileDrawerOpen: boolean;
  setMobileDrawerOpen: (open: boolean) => void;
}

const SidebarContext = createContext<SidebarContextValue | null>(null);

export function SidebarProvider({ children }: { children: ReactNode }) {
  const { collapseState, setCollapseState, cycleCollapseState } = useSidebarState();

  const [pageSections, setPageSectionsState] = useState<PageSectionItem[]>([]);
  const [pageSectionsTitle, setPageSectionsTitle] = useState("");
  const [pageSectionsMode, setPageSectionsMode] = useState<"scroll-spy" | "route">("scroll-spy");
  const [mobileDrawerOpen, setMobileDrawerOpen] = useState(false);

  const setPageSections = useCallback(
    (items: PageSectionItem[], title: string, mode: "scroll-spy" | "route") => {
      setPageSectionsState(items);
      setPageSectionsTitle(title);
      setPageSectionsMode(mode);
    },
    [],
  );

  const clearPageSections = useCallback(() => {
    setPageSectionsState([]);
    setPageSectionsTitle("");
    setPageSectionsMode("scroll-spy");
  }, []);

  return (
    <SidebarContext.Provider
      value={{
        collapseState,
        setCollapseState,
        cycleCollapseState,
        pageSections,
        pageSectionsTitle,
        pageSectionsMode,
        setPageSections,
        clearPageSections,
        mobileDrawerOpen,
        setMobileDrawerOpen,
      }}
    >
      {children}
    </SidebarContext.Provider>
  );
}

export function useSidebar() {
  const ctx = useContext(SidebarContext);
  if (!ctx) throw new Error("useSidebar must be used within SidebarProvider");
  return ctx;
}
