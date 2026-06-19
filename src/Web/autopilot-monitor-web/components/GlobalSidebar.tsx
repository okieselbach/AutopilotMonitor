"use client";

import { useEffect, useRef, useState, useMemo, useCallback, ReactNode } from "react";
import Link from "next/link";
import { usePathname } from "next/navigation";
import { useAuth } from "../contexts/AuthContext";
import { useSidebar, PageSectionItem } from "../contexts/SidebarContext";
import { CollapseState } from "../hooks/useSidebarState";
import { DefaultSectionIcon, BookOpenIcon, RocketLaunchIcon, InformationCircleIcon, DocumentTextIcon, ShieldCheckIcon } from "../lib/sidebarIcons";
import { DASHBOARD_ITEM, NAV_GROUPS, EXPANDABLE_NAV_GROUPS, REGULAR_USER_ITEMS, NavItem, NavGroup, ExpandableNavGroup, ExpandableNavItem } from "../lib/globalNavConfig";
import { PublicSiteNavbar } from "./PublicSiteNavbar";
import { useAdminMode } from "../hooks/useAdminMode";

// Sidebar pixel widths
export const SIDEBAR_PX: Record<CollapseState, number> = {
  full: 224,   // w-56
  icons: 56,   // w-14
  hidden: 0,
};

const CHEVRON_W = 16;

const sidebarWidthClass: Record<CollapseState, string> = {
  full: "w-56",
  icons: "w-14",
  hidden: "w-0",
};

export function GlobalSidebar({ children }: { children: ReactNode }) {
  const {
    collapseState, cycleCollapseState, setCollapseState,
    pageSections, pageSectionsTitle, pageSectionsMode,
    mobileDrawerOpen, setMobileDrawerOpen,
  } = useSidebar();

  const { isAuthenticated, user } = useAuth();
  const pathname = usePathname();

  // Track desktop breakpoint
  const [isDesktop, setIsDesktop] = useState(false);
  useEffect(() => {
    const mql = window.matchMedia("(min-width: 768px)");
    setIsDesktop(mql.matches);
    const handler = (e: MediaQueryListEvent) => setIsDesktop(e.matches);
    mql.addEventListener("change", handler);
    return () => mql.removeEventListener("change", handler);
  }, []);

  // Global admin mode from hook
  const { globalAdminMode } = useAdminMode();

  // --- Scroll-spy for page sections ---
  const [activeSectionId, setActiveSectionId] = useState("");
  const observerRef = useRef<IntersectionObserver | null>(null);
  const visibleSections = useRef<Set<string>>(new Set());

  useEffect(() => {
    if (pageSectionsMode !== "scroll-spy" || pageSections.length === 0) return;

    observerRef.current?.disconnect();
    visibleSections.current.clear();

    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries) {
          if (entry.isIntersecting) {
            visibleSections.current.add(entry.target.id);
          } else {
            visibleSections.current.delete(entry.target.id);
          }
        }
        for (const item of pageSections) {
          if (visibleSections.current.has(item.id)) {
            setActiveSectionId(item.id);
            break;
          }
        }
      },
      { rootMargin: "-80px 0px -60% 0px", threshold: 0 },
    );

    observerRef.current = observer;

    for (const item of pageSections) {
      const el = document.getElementById(item.id);
      if (el) observer.observe(el);
    }

    return () => observer.disconnect();
  }, [pageSections, pageSectionsMode]);

  // Route mode for page sections
  useEffect(() => {
    if (pageSectionsMode !== "route" || pageSections.length === 0) return;
    // Match by href first (more precise), fall back to last segment matching id
    const hrefMatch = pageSections.find((item) => item.href && pathname === item.href);
    if (hrefMatch) {
      setActiveSectionId(hrefMatch.id);
    } else {
      const segment = pathname.split("/").pop() ?? "";
      const match = pageSections.find((item) => item.id === segment);
      if (match) setActiveSectionId(match.id);
    }
  }, [pathname, pageSections, pageSectionsMode]);

  const scrollTo = useCallback((id: string) => {
    const el = document.getElementById(id);
    if (el) {
      const y = el.getBoundingClientRect().top + window.scrollY - 90;
      window.scrollTo({ top: y, behavior: "smooth" });
    }
    setActiveSectionId(id);
    setMobileDrawerOpen(false);
  }, [setMobileDrawerOpen]);

  // --- Grouped page sections (expandable) ---
  const hasGroups = pageSections.some((item) => item.group);

  // Group items by their group name (preserving order of first appearance)
  const groupedSections = useMemo(() => {
    if (!hasGroups) return null;
    const groups: { name: string; icon?: ReactNode; items: PageSectionItem[] }[] = [];
    const seen = new Map<string, number>();
    for (const item of pageSections) {
      const g = item.group ?? "";
      if (!seen.has(g)) {
        seen.set(g, groups.length);
        groups.push({ name: g, icon: item.groupIcon, items: [] });
      }
      groups[seen.get(g)!].items.push(item);
    }
    return groups;
  }, [pageSections, hasGroups]);

  // Track which groups are expanded — default: group containing active item
  const [expandedGroups, setExpandedGroups] = useState<Set<string>>(new Set());

  // Auto-expand the group of the active section
  useEffect(() => {
    if (!groupedSections || !activeSectionId) return;
    for (const group of groupedSections) {
      if (group.items.some((item) => item.id === activeSectionId)) {
        setExpandedGroups((prev) => {
          if (prev.has(group.name)) return prev;
          const next = new Set(prev);
          next.add(group.name);
          return next;
        });
        break;
      }
    }
  }, [activeSectionId, groupedSections]);

  const toggleGroup = useCallback((groupName: string) => {
    setExpandedGroups((prev) => {
      const next = new Set(prev);
      if (next.has(groupName)) next.delete(groupName);
      else next.add(groupName);
      return next;
    });
  }, []);

  // Track which expandable nav group categories are collapsed (e.g. "Global Admin")
  const [collapsedCategories, setCollapsedCategories] = useState<Set<string>>(new Set());

  const toggleCategory = useCallback((categoryId: string) => {
    setCollapsedCategories((prev) => {
      const next = new Set(prev);
      if (next.has(categoryId)) next.delete(categoryId);
      else next.add(categoryId);
      return next;
    });
  }, []);

  // --- Visibility filtering ---
  const isTenantAdmin = user?.isTenantAdmin ?? false;
  const isOperator = user?.role === "Operator";
  const isAdminOrOperator = isTenantAdmin || isOperator;
  const isGlobalAdmin = user?.isGlobalAdmin ?? false;

  const isGroupVisible = (group: NavGroup | ExpandableNavGroup): boolean => {
    switch (group.visibility) {
      case "all": return true;
      case "adminOrOperator": return isAdminOrOperator;
      case "globalAdmin": return isGlobalAdmin && globalAdminMode;
      default: return false;
    }
  };

  // Visible expandable groups (with item-level filtering for feature-gated items)
  const hasMcpAccess = user?.hasMcpAccess ?? false;
  const isAdminLike = isTenantAdmin || isGlobalAdmin;
  const canManageBootstrapTokens = user?.canManageBootstrapTokens ?? false;
  const bootstrapTokenEnabled = user?.bootstrapTokenEnabled ?? false;
  const unrestrictedModeEnabled = user?.unrestrictedModeEnabled ?? false;
  const visibleExpandableGroups = useMemo(() => {
    return EXPANDABLE_NAV_GROUPS
      .filter(isGroupVisible)
      .map((group) => {
        // Filter out MCP item if user doesn't have MCP access
        const filteredItems = group.items
          .filter((item) => {
            if (item.id === "cfg-reporting") return hasMcpAccess;
            return true;
          })
          .map((item) => {
            // Sub-item gating for feature-flagged entries (tenant feature flags)
            const filteredSubs = item.items.filter((sub) => {
              if (sub.id === "cfg-bootstrap-sessions") {
                return bootstrapTokenEnabled && (isAdminLike || canManageBootstrapTokens);
              }
              if (sub.id === "cfg-agent-unrestricted") {
                return isAdminLike && unrestrictedModeEnabled;
              }
              return true;
            });
            return { ...item, items: filteredSubs };
          })
          // Drop items whose sub-items have all been filtered out
          .filter((item) => item.items.length > 0);
        return { ...group, items: filteredItems };
      });
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isAdminOrOperator, isGlobalAdmin, globalAdminMode, hasMcpAccess, isAdminLike, canManageBootstrapTokens, bootstrapTokenEnabled, unrestrictedModeEnabled]);

  // Auto-expand the group containing the current pathname
  useEffect(() => {
    for (const group of visibleExpandableGroups) {
      for (const item of group.items) {
        if (item.items.some((sub) => pathname === sub.href || pathname.startsWith(sub.href + "/"))) {
          // Auto-expand the category if collapsed
          setCollapsedCategories((prev) => {
            if (!prev.has(group.id)) return prev;
            const next = new Set(prev);
            next.delete(group.id);
            return next;
          });
          setExpandedGroups((prev) => {
            if (prev.has(item.id)) return prev;
            const next = new Set(prev);
            next.add(item.id);
            return next;
          });
          return;
        }
      }
    }
  }, [pathname]); // eslint-disable-line react-hooks/exhaustive-deps

  // Docs pages are always accessible (public + authenticated)
  const isDocsPage = pathname.startsWith("/docs");

  // All public pages that should show sidebar + navbar
  const PUBLIC_PATHS = ["/docs", "/terms", "/privacy", "/roadmap", "/about", "/changelog"];
  const isPublicPage = PUBLIC_PATHS.some((p) => pathname === p || pathname.startsWith(p + "/"));

  // Landing page: never show sidebar
  if (pathname === "/") {
    return <>{children}</>;
  }

  // Not authenticated and not a public page: no sidebar
  if (!isAuthenticated && !isPublicPage) {
    return <>{children}</>;
  }

  // Whether a top navbar is present (determines sidebar top offset)
  // Authenticated users always have the app navbar; public pages have the PublicSiteNavbar
  const hasNavbar = isAuthenticated || isPublicPage;

  // --- Render helpers ---

  const renderIcon = (icon: ReactNode | undefined, sizeClass = "w-4 h-4") => {
    if (icon) return <span className={`shrink-0 inline-flex items-center ${sizeClass}`}>{icon}</span>;
    return <DefaultSectionIcon className={`shrink-0 ${sizeClass}`} />;
  };

  const isNavActive = (href: string) => {
    if (href === "/dashboard") return pathname === "/dashboard" || pathname === "/";
    if (href === "/") return pathname === "/";
    return pathname.startsWith(href);
  };

  // Shared item classes
  const itemClass = (active: boolean, isGlobal = false) => {
    if (active) {
      return isGlobal
        ? "bg-purple-50 text-purple-700 font-semibold dark:bg-purple-900/30 dark:text-purple-300"
        : "bg-blue-50 text-blue-700 font-semibold dark:bg-blue-900/30 dark:text-blue-300";
    }
    return isGlobal
      ? "text-purple-600 hover:bg-purple-50 hover:text-purple-800 dark:text-purple-400 dark:hover:bg-purple-900/20 dark:hover:text-purple-300"
      : "text-gray-600 hover:bg-gray-100 hover:text-gray-900 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-gray-200";
  };

  // --- Render a global nav link ---
  const renderGlobalItem = (item: NavItem, isGlobal = false) => {
    const active = isNavActive(item.href);
    const base = `flex items-center gap-2.5 rounded-md text-sm transition-colors ${itemClass(active, isGlobal)}`;

    if (collapseState === "icons") {
      return (
        <li key={item.id}>
          <Link
            href={item.href}
            onClick={() => setMobileDrawerOpen(false)}
            className={`${base} px-2 py-2 justify-center relative group`}
            title={item.label}
          >
            <span className="flex items-center justify-center w-full">
              {renderIcon(item.icon, "w-4.5 h-4.5")}
            </span>
            <span className="absolute left-full ml-2 px-2 py-1 rounded bg-gray-900 text-white text-xs whitespace-nowrap opacity-0 pointer-events-none group-hover:opacity-100 transition-opacity z-50 dark:bg-gray-700">
              {item.label}
            </span>
          </Link>
        </li>
      );
    }

    return (
      <li key={item.id}>
        <Link href={item.href} onClick={() => setMobileDrawerOpen(false)} className={`${base} px-3 py-1.5`}>
          {renderIcon(item.icon, "w-4 h-4")}
          <span className="truncate">{item.label}</span>
        </Link>
      </li>
    );
  };

  // --- Render a page section item ---
  const renderSectionItem = (item: PageSectionItem) => {
    const active = activeSectionId === item.id;
    const base = `flex items-center gap-2.5 rounded-md text-sm transition-colors ${itemClass(active)}`;

    if (collapseState === "icons") {
      const inner = (
        <span className="flex items-center justify-center w-full">
          {renderIcon(item.icon, "w-4.5 h-4.5")}
        </span>
      );

      if (pageSectionsMode === "route" && item.href) {
        return (
          <li key={item.id}>
            <Link href={item.href} onClick={() => setMobileDrawerOpen(false)} className={`${base} px-2 py-2 justify-center relative group`} title={item.label}>
              {inner}
              <span className="absolute left-full ml-2 px-2 py-1 rounded bg-gray-900 text-white text-xs whitespace-nowrap opacity-0 pointer-events-none group-hover:opacity-100 transition-opacity z-50 dark:bg-gray-700">{item.label}</span>
            </Link>
          </li>
        );
      }

      return (
        <li key={item.id}>
          <button onClick={() => scrollTo(item.id)} className={`${base} w-full px-2 py-2 justify-center relative group`} title={item.label}>
            {inner}
            <span className="absolute left-full ml-2 px-2 py-1 rounded bg-gray-900 text-white text-xs whitespace-nowrap opacity-0 pointer-events-none group-hover:opacity-100 transition-opacity z-50 dark:bg-gray-700">{item.label}</span>
          </button>
        </li>
      );
    }

    // Full mode
    if (pageSectionsMode === "route" && item.href) {
      return (
        <li key={item.id}>
          <Link href={item.href} onClick={() => setMobileDrawerOpen(false)} className={`${base} px-3 py-1.5`}>
            {renderIcon(item.icon, "w-4 h-4")}
            <span className="truncate">{item.label}</span>
          </Link>
        </li>
      );
    }

    return (
      <li key={item.id}>
        <button onClick={() => scrollTo(item.id)} className={`${base} w-full text-left px-3 py-1.5`}>
          {renderIcon(item.icon, "w-4 h-4")}
          <span className="truncate">{item.label}</span>
        </button>
      </li>
    );
  };

  // --- Build the nav content (shared between desktop and mobile) ---
  const visibleGroups = NAV_GROUPS.filter(isGroupVisible);
  const hasPageSections = pageSections.length > 0;

  // Regular users see minimal nav
  const isRegularUser = !isAdminOrOperator && !isGlobalAdmin;

  const renderNavContent = (isMobile = false) => (
    <>
      {/* Global nav — only when authenticated */}
      {isAuthenticated && (
        <>
          {/* Dashboard — hidden for regular users (no admin/operator access) */}
          {!isRegularUser && (
            <ul className="space-y-0.5">
              {renderGlobalItem(DASHBOARD_ITEM)}
            </ul>
          )}

          {/* Regular user: only show Progress Portal */}
          {isRegularUser && (
            <div className={`${collapseState === "full" || isMobile ? "mt-3" : "mt-1"}`}>
              <ul className="space-y-0.5">
                {REGULAR_USER_ITEMS.map((item) => renderGlobalItem(item))}
              </ul>
            </div>
          )}

          {/* Admin/Operator flat groups */}
          {!isRegularUser && visibleGroups.map((group) => (
            <div key={group.id} className={`${collapseState === "full" || isMobile ? "mt-4" : "mt-1"}`}>
              {(collapseState === "full" || isMobile) && (
                <p className={`text-[11px] font-semibold uppercase tracking-wider mb-1 px-3 ${
                  group.style === "global"
                    ? "text-purple-500 dark:text-purple-400"
                    : "text-gray-400 dark:text-gray-500"
                }`}>
                  {group.label}
                </p>
              )}
              {collapseState === "icons" && !isMobile && (
                <hr className="mx-2 my-1.5 border-gray-200 dark:border-gray-700" />
              )}
              <ul className="space-y-0.5">
                {group.items.map((item) => renderGlobalItem(item, group.style === "global"))}
              </ul>
            </div>
          ))}

          {/* Expandable groups (GitHub-style) — Configuration, Global Admin */}
          {!isRegularUser && visibleExpandableGroups.map((group) => {
            const isCategoryCollapsed = collapsedCategories.has(group.id);
            return (
            <div key={group.id} className={`${collapseState === "full" || isMobile ? "mt-4" : "mt-1"}`}>
              {/* Group label — clickable to collapse/expand the category */}
              {(collapseState === "full" || isMobile) && (
                <button
                  onClick={() => toggleCategory(group.id)}
                  className={`w-full flex items-center justify-between text-[11px] font-semibold uppercase tracking-wider mb-1 px-3 py-0.5 rounded-sm transition-colors hover:opacity-80 ${
                    group.style === "global"
                      ? "text-purple-500 dark:text-purple-400"
                      : "text-gray-400 dark:text-gray-500"
                  }`}
                >
                  <span>{group.label}</span>
                  <svg
                    className={`w-3 h-3 shrink-0 transition-transform duration-150 ${isCategoryCollapsed ? "" : "rotate-90"}`}
                    fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}
                  >
                    <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                  </svg>
                </button>
              )}
              {collapseState === "icons" && !isMobile && (
                <hr className={`mx-2 my-1.5 ${
                  group.style === "global"
                    ? "border-purple-200 dark:border-purple-800"
                    : "border-gray-200 dark:border-gray-700"
                }`} />
              )}
              {!isCategoryCollapsed && (
              <ul className="space-y-0.5">
                {group.items.map((expandItem) => {
                  const isExpanded = expandedGroups.has(expandItem.id);
                  const itemHasActive = expandItem.items.some((sub) => pathname === sub.href || pathname.startsWith(sub.href + "/"));
                  const firstHref = expandItem.items[0]?.href ?? "#";
                  const isGlobalStyle = group.style === "global";

                  if (collapseState === "icons" && !isMobile) {
                    // Icons mode: show group icon as link
                    return (
                      <li key={expandItem.id}>
                        <Link
                          href={firstHref}
                          onClick={() => setMobileDrawerOpen(false)}
                          className={`flex items-center justify-center px-2 py-2 rounded-md text-sm transition-colors relative group ${
                            itemHasActive
                              ? isGlobalStyle
                                ? "bg-purple-50 text-purple-700 font-semibold dark:bg-purple-900/30 dark:text-purple-300"
                                : "bg-blue-50 text-blue-700 font-semibold dark:bg-blue-900/30 dark:text-blue-300"
                              : isGlobalStyle
                                ? "text-purple-600 hover:bg-purple-50 hover:text-purple-800 dark:text-purple-400 dark:hover:bg-purple-900/20"
                                : "text-gray-600 hover:bg-gray-100 hover:text-gray-900 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-gray-200"
                          }`}
                          title={expandItem.label}
                        >
                          <span className="shrink-0 inline-flex items-center justify-center w-4.5 h-4.5">{expandItem.icon}</span>
                          <span className="absolute left-full ml-2 px-2 py-1 rounded bg-gray-900 text-white text-xs whitespace-nowrap opacity-0 pointer-events-none group-hover:opacity-100 transition-opacity z-50 dark:bg-gray-700">
                            {expandItem.label}
                          </span>
                        </Link>
                      </li>
                    );
                  }

                  // Full mode: GitHub-style expandable item
                  return (
                    <li key={expandItem.id}>
                      <button
                        onClick={() => toggleGroup(expandItem.id)}
                        className={`w-full flex items-center gap-2.5 px-3 py-1.5 rounded-md text-sm transition-colors ${
                          itemHasActive
                            ? isGlobalStyle
                              ? "text-purple-700 font-semibold dark:text-purple-300"
                              : "text-blue-700 font-semibold dark:text-blue-300"
                            : isGlobalStyle
                              ? "text-purple-600 hover:bg-purple-50 hover:text-purple-800 dark:text-purple-400 dark:hover:bg-purple-900/20"
                              : "text-gray-600 hover:bg-gray-100 hover:text-gray-900 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-gray-200"
                        }`}
                      >
                        <span className="shrink-0 inline-flex items-center w-4 h-4">{expandItem.icon}</span>
                        <span className="truncate flex-1 text-left">{expandItem.label}</span>
                        <svg
                          className={`w-3.5 h-3.5 shrink-0 text-gray-400 transition-transform duration-150 ${isExpanded ? "rotate-90" : ""}`}
                          fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}
                        >
                          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                        </svg>
                      </button>
                      {isExpanded && (() => {
                        // Pick the longest sub.href that matches the current pathname so a
                        // shorter sibling whose href is a strict prefix of the active route
                        // doesn't also light up. Example: with /admin/ops (Maintenance) and
                        // /admin/ops/session-cleanup, navigating to the latter previously
                        // marked BOTH active. Computed once per render and applied per sub.
                        const winningSubHref = expandItem.items
                          .filter((s) => pathname === s.href || pathname.startsWith(s.href + "/"))
                          .reduce<string | null>(
                            (longest, s) => longest == null || s.href.length > longest.length ? s.href : longest,
                            null,
                          );
                        return (
                        <ul className="mt-0.5 space-y-px">
                          {expandItem.items.map((sub) => {
                            const subActive = sub.href === winningSubHref;
                            return (
                              <li key={sub.id}>
                                <Link
                                  href={sub.href}
                                  onClick={() => setMobileDrawerOpen(false)}
                                  className={`block pl-10 pr-3 py-1 rounded-md text-[13px] transition-colors ${
                                    subActive
                                      ? isGlobalStyle
                                        ? "bg-purple-50 text-purple-700 font-medium dark:bg-purple-900/30 dark:text-purple-300"
                                        : "bg-blue-50 text-blue-700 font-medium dark:bg-blue-900/30 dark:text-blue-300"
                                      : "text-gray-500 hover:bg-gray-100 hover:text-gray-900 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-gray-200"
                                  }`}
                                >
                                  {sub.label}
                                </Link>
                              </li>
                            );
                          })}
                        </ul>
                        );
                      })()}
                    </li>
                  );
                })}
              </ul>
              )}
            </div>
            );
          })}
        </>
      )}

      {/* Public (unauthenticated): show public nav items */}
      {!isAuthenticated && (
        <>
          <ul className="space-y-0.5">
            {renderGlobalItem({ id: "home", label: "Home", href: "/", icon: DASHBOARD_ITEM.icon })}
          </ul>
          <div className={`${collapseState === "full" || isMobile ? "mt-3" : "mt-1"}`}>
            <ul className="space-y-0.5">
              {renderGlobalItem({ id: "docs", label: "Docs", href: "/docs", icon: <BookOpenIcon /> })}
              {renderGlobalItem({ id: "roadmap", label: "Roadmap", href: "/roadmap", icon: <RocketLaunchIcon /> })}
              {renderGlobalItem({ id: "about", label: "About", href: "/about", icon: <InformationCircleIcon /> })}
              {renderGlobalItem({ id: "terms", label: "Terms", href: "/terms", icon: <DocumentTextIcon /> })}
              {renderGlobalItem({ id: "privacy", label: "Privacy", href: "/privacy", icon: <ShieldCheckIcon /> })}
            </ul>
          </div>
        </>
      )}

      {/* Divider (only when both global nav and page sections exist) + page sections */}
      {hasPageSections && (
        <>
          {/* Divider / spacing between nav and page sections */}
          {collapseState === "icons" && !isMobile ? (
            <hr className="mx-2 my-2 border-blue-200 dark:border-blue-800" />
          ) : (
            <div className="mt-4 mb-2 mx-3">
              <hr className="border-gray-200 dark:border-gray-700" />
            </div>
          )}
          {/* Title only for ungrouped sections (grouped sections have their own headers) */}
          {!groupedSections && (collapseState === "full" || isMobile) && (
            <p className="text-[11px] font-semibold uppercase tracking-wider mb-1 px-3 text-blue-500 dark:text-blue-400">
              {pageSectionsTitle}
            </p>
          )}

          {/* Grouped sections with expand/collapse (GitHub-style) */}
          {groupedSections ? (
            <ul className="space-y-0.5">
            {groupedSections.map((group) => {
              const isExpanded = expandedGroups.has(group.name);
              const groupHasActive = group.items.some((item) => item.id === activeSectionId);
              const firstHref = group.items[0]?.href;

              if (collapseState === "icons" && !isMobile) {
                // In icons mode: show group icon as a link to the first item
                return (
                  <li key={group.name}>
                    <Link
                      href={firstHref ?? "#"}
                      onClick={() => setMobileDrawerOpen(false)}
                      className={`flex items-center justify-center px-2 py-2 rounded-md text-sm transition-colors relative group ${
                        groupHasActive
                          ? "bg-blue-50 text-blue-700 font-semibold dark:bg-blue-900/30 dark:text-blue-300"
                          : "text-gray-600 hover:bg-gray-100 hover:text-gray-900 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-gray-200"
                      }`}
                      title={group.name}
                    >
                      <span className="shrink-0 inline-flex items-center justify-center w-4.5 h-4.5">
                        {group.icon ? group.icon : renderIcon(undefined, "w-4.5 h-4.5")}
                      </span>
                      <span className="absolute left-full ml-2 px-2 py-1 rounded bg-gray-900 text-white text-xs whitespace-nowrap opacity-0 pointer-events-none group-hover:opacity-100 transition-opacity z-50 dark:bg-gray-700">
                        {group.name}
                      </span>
                    </Link>
                  </li>
                );
              }

              // Full mode: GitHub-style with icon + label + chevron
              return (
                <li key={group.name} className="mt-0.5">
                  <button
                    onClick={() => toggleGroup(group.name)}
                    className={`w-full flex items-center gap-2.5 px-3 py-1.5 rounded-md text-sm transition-colors ${
                      groupHasActive
                        ? "text-blue-700 font-semibold dark:text-blue-300"
                        : "text-gray-600 hover:bg-gray-100 hover:text-gray-900 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-gray-200"
                    }`}
                  >
                    {/* Group icon */}
                    <span className="shrink-0 inline-flex items-center w-4 h-4">
                      {group.icon ? group.icon : renderIcon(undefined, "w-4 h-4")}
                    </span>
                    {/* Group label */}
                    <span className="truncate flex-1 text-left">{group.name}</span>
                    {/* Chevron */}
                    <svg
                      className={`w-3.5 h-3.5 shrink-0 text-gray-400 transition-transform duration-150 ${isExpanded ? "rotate-90" : ""}`}
                      fill="none"
                      viewBox="0 0 24 24"
                      stroke="currentColor"
                      strokeWidth={2}
                    >
                      <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
                    </svg>
                  </button>
                  {/* Sub-items — smaller, indented, no icons (GitHub-style) */}
                  {isExpanded && (
                    <ul className="mt-0.5 space-y-px">
                      {group.items.map((item) => {
                        const active = activeSectionId === item.id;
                        if (pageSectionsMode === "route" && item.href) {
                          return (
                            <li key={item.id}>
                              <Link
                                href={item.href}
                                onClick={() => setMobileDrawerOpen(false)}
                                className={`block pl-10 pr-3 py-1 rounded-md text-[13px] transition-colors ${
                                  active
                                    ? "bg-blue-50 text-blue-700 font-medium dark:bg-blue-900/30 dark:text-blue-300"
                                    : "text-gray-500 hover:bg-gray-100 hover:text-gray-900 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-gray-200"
                                }`}
                              >
                                {item.label}
                              </Link>
                            </li>
                          );
                        }
                        return (
                          <li key={item.id}>
                            <button
                              onClick={() => scrollTo(item.id)}
                              className={`w-full text-left block pl-10 pr-3 py-1 rounded-md text-[13px] transition-colors ${
                                active
                                  ? "bg-blue-50 text-blue-700 font-medium dark:bg-blue-900/30 dark:text-blue-300"
                                  : "text-gray-500 hover:bg-gray-100 hover:text-gray-900 dark:text-gray-400 dark:hover:bg-gray-700 dark:hover:text-gray-200"
                              }`}
                            >
                              {item.label}
                            </button>
                          </li>
                        );
                      })}
                    </ul>
                  )}
                </li>
              );
            })}
            </ul>
          ) : (
            /* Flat (ungrouped) sections — existing behavior */
            <ul className="space-y-0.5">
              {pageSections.map(renderSectionItem)}
            </ul>
          )}
        </>
      )}
    </>
  );

  return (
    <>
      {/* ===== Public pages: show branded navbar without section links ===== */}
      {!isAuthenticated && isPublicPage && <PublicSiteNavbar showSectionLinks={false} fullWidth />}

      {/* ===== Mobile: overlay ===== */}
      {mobileDrawerOpen && (
        <div
          className="fixed inset-0 z-40 bg-black/40 md:hidden"
          onClick={() => setMobileDrawerOpen(false)}
        />
      )}

      {/* ===== Mobile: drawer ===== */}
      <div
        className={`fixed top-0 left-0 z-50 h-full w-56 bg-white shadow-xl transition-transform duration-200 md:hidden dark:bg-gray-800 ${
          mobileDrawerOpen ? "translate-x-0" : "-translate-x-full"
        }`}
      >
        <div className="flex items-center justify-between px-4 pt-5 pb-3 border-b border-gray-100 dark:border-gray-700">
          <p className="text-xs font-semibold text-gray-400 uppercase tracking-wider dark:text-gray-500">
            Navigation
          </p>
          <button
            onClick={() => setMobileDrawerOpen(false)}
            className="p-1 rounded-md text-gray-400 hover:text-gray-600 hover:bg-gray-100 dark:hover:text-gray-300 dark:hover:bg-gray-700"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
              <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
            </svg>
          </button>
        </div>
        <div className="p-3 overflow-y-auto max-h-[calc(100%-4rem)]" style={{ scrollbarWidth: "none" }}>
          {renderNavContent(true)}
        </div>
      </div>

      {/* ===== Mobile: chevron toggle (fixed, left edge, vertically centered) ===== */}
      <button
        onClick={() => setMobileDrawerOpen(true)}
        className="md:hidden fixed left-0 top-1/2 -translate-y-1/2 z-30 flex items-center justify-center w-5 h-16 rounded-r-lg bg-gray-200/90 text-gray-500 shadow-sm border border-l-0 border-gray-300 active:bg-gray-300 transition-colors dark:bg-gray-600/90 dark:text-gray-400 dark:border-gray-500 dark:active:bg-gray-500"
        aria-label="Open navigation"
      >
        <svg className="w-3 h-3" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={3}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
        </svg>
      </button>

      {/* ===== Desktop: fixed sidebar ===== */}
      <aside
        className={`hidden md:flex fixed left-0 ${hasNavbar ? "top-14" : "top-0"} bottom-0 z-20 flex-col transition-all duration-200 ease-in-out overflow-hidden ${sidebarWidthClass[collapseState]}`}
      >
        {collapseState !== "hidden" && (
          <nav className="flex flex-col h-full bg-white border-r border-gray-200 dark:bg-gray-800 dark:border-gray-700">
            <div className={`flex-1 overflow-y-auto overscroll-contain ${
              collapseState === "full" ? "p-3" : "p-1.5"
            }`} style={{ scrollbarWidth: "none" }}>
              {renderNavContent()}
            </div>
          </nav>
        )}
      </aside>

      {/* ===== Desktop: chevron toggle ===== */}
      <button
        onClick={collapseState === "hidden" ? () => setCollapseState("full") : cycleCollapseState}
        className={`hidden md:flex fixed z-30 top-1/2 -translate-y-1/2 items-center justify-center h-16 rounded-r-lg bg-gray-100 text-gray-400 shadow-sm border border-l-0 border-gray-200 transition-all duration-200 hover:bg-gray-200 hover:text-gray-600 dark:bg-gray-700 dark:text-gray-500 dark:border-gray-600 dark:hover:bg-gray-600 dark:hover:text-gray-300 ${
          collapseState === "hidden" ? "w-5" : "w-3.5"
        }`}
        style={{ left: SIDEBAR_PX[collapseState] }}
        aria-label={collapseState === "hidden" ? "Show sidebar" : "Collapse sidebar"}
        title={
          collapseState === "full"
            ? "Show icons only"
            : collapseState === "icons"
              ? "Hide sidebar"
              : "Show sidebar"
        }
      >
        <svg
          className={`w-2.5 h-2.5 transition-transform duration-200 ${collapseState === "hidden" ? "" : "rotate-180"}`}
          fill="none"
          viewBox="0 0 24 24"
          stroke="currentColor"
          strokeWidth={2.5}
        >
          <path strokeLinecap="round" strokeLinejoin="round" d="M9 5l7 7-7 7" />
        </svg>
      </button>

      {/* ===== Content — pushed right by sidebar width ===== */}
      <div
        style={{
          marginLeft: isDesktop ? SIDEBAR_PX[collapseState] : 0,
          transition: "margin-left 200ms ease-in-out",
        }}
      >
        {children}
      </div>
    </>
  );
}
