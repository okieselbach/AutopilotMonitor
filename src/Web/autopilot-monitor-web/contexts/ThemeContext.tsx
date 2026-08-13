"use client";

import { createContext, useCallback, useContext, useEffect, useMemo, useSyncExternalStore } from "react";
import {
  Theme,
  getServerThemeSnapshot,
  getThemeSnapshot,
  setStoredTheme,
  subscribeTheme,
} from "@/lib/themeStore";

interface ThemeContextType {
  theme: Theme;
  toggleTheme: () => void;
}

const ThemeContext = createContext<ThemeContextType>({
  theme: "light",
  toggleTheme: () => {},
});

export function ThemeProvider({ children }: { children: React.ReactNode }) {
  // localStorage preference (falling back to the OS preference) read as an external
  // store: the prerendered HTML hydrates light and re-renders with the real theme,
  // OS-level changes stream in via the store subscription.
  const theme = useSyncExternalStore(subscribeTheme, getThemeSnapshot, getServerThemeSnapshot);

  // Apply class to <html>
  useEffect(() => {
    document.documentElement.classList.toggle("dark", theme === "dark");
  }, [theme]);

  const toggleTheme = useCallback(() => {
    setStoredTheme(theme === "dark" ? "light" : "dark");
  }, [theme]);

  // Memoized (P6.3): stable value identity while the theme is unchanged.
  const value = useMemo(() => ({ theme, toggleTheme }), [theme, toggleTheme]);

  return (
    <ThemeContext.Provider value={value}>
      {children}
    </ThemeContext.Provider>
  );
}

export function useTheme() {
  return useContext(ThemeContext);
}
