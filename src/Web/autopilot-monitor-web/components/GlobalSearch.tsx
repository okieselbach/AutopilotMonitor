"use client";

import { sessionUrl } from "@/lib/routes";
import { useState, useRef, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { useAuth } from '@/contexts/AuthContext';
import { authenticatedFetch } from '@/lib/authenticatedFetch';
import { api } from '@/lib/api';
import { trackEvent } from '@/lib/appInsights';

interface QuickSearchResult {
  sessionId: string;
  serialNumber: string;
  deviceName: string;
  status: string;
  startedAt: string;
  matchedField: 'sessionId' | 'serialNumber' | 'deviceName';
}

export default function GlobalSearch() {
  const [query, setQuery] = useState('');
  const [mobileOpen, setMobileOpen] = useState(false);
  const [results, setResults] = useState<QuickSearchResult[]>([]);
  const [loading, setLoading] = useState(false);
  const [showDropdown, setShowDropdown] = useState(false);
  const [selectedIndex, setSelectedIndex] = useState(-1);

  const inputRef = useRef<HTMLInputElement>(null);
  const mobileInputRef = useRef<HTMLInputElement>(null);
  const containerRef = useRef<HTMLDivElement>(null);
  const mobileContainerRef = useRef<HTMLDivElement>(null);
  const debounceRef = useRef<ReturnType<typeof setTimeout>>();
  const abortRef = useRef<AbortController>();
  const router = useRouter();
  const { getAccessToken } = useAuth();

  // Close dropdown on outside click
  useEffect(() => {
    function handleClickOutside(e: MouseEvent) {
      if (containerRef.current && !containerRef.current.contains(e.target as Node)) {
        setShowDropdown(false);
      }
      if (mobileContainerRef.current && !mobileContainerRef.current.contains(e.target as Node)) {
        setMobileOpen(false);
        setShowDropdown(false);
      }
    }
    document.addEventListener('mousedown', handleClickOutside);
    return () => document.removeEventListener('mousedown', handleClickOutside);
  }, []);

  // Auto-focus mobile input when opened
  useEffect(() => {
    if (mobileOpen) {
      setTimeout(() => mobileInputRef.current?.focus(), 50);
    }
  }, [mobileOpen]);

  // Keyboard shortcut: Ctrl+K to focus search
  useEffect(() => {
    function handleKeyDown(e: KeyboardEvent) {
      if ((e.ctrlKey || e.metaKey) && e.key === 'k') {
        e.preventDefault();
        inputRef.current?.focus();
      }
    }
    document.addEventListener('keydown', handleKeyDown);
    return () => document.removeEventListener('keydown', handleKeyDown);
  }, []);

  const doSearch = useCallback(async (q: string) => {
    if (q.length < 2) {
      setResults([]);
      setShowDropdown(false);
      return;
    }

    // Cancel any in-flight request
    abortRef.current?.abort();
    const controller = new AbortController();
    abortRef.current = controller;

    setLoading(true);
    try {
      const response = await authenticatedFetch(
        api.sessions.quickSearch(q),
        getAccessToken,
        { signal: controller.signal },
      );

      if (!response.ok) {
        setResults([]);
        setShowDropdown(true);
        return;
      }

      const data = await response.json();
      setResults(data.results ?? []);
      setShowDropdown(true);
      setSelectedIndex(-1);
      trackEvent('global_search', { query: q, resultCount: String(data.count ?? 0) });
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      setResults([]);
    } finally {
      setLoading(false);
    }
  }, [getAccessToken]);

  const handleInputChange = (value: string) => {
    setQuery(value);
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => doSearch(value), 250);
  };

  const navigateToResult = (result: QuickSearchResult) => {
    setShowDropdown(false);
    setMobileOpen(false);
    setQuery('');
    router.push(sessionUrl(result.sessionId));
  };

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === 'ArrowDown') {
      e.preventDefault();
      setSelectedIndex(prev => Math.min(prev + 1, results.length - 1));
    } else if (e.key === 'ArrowUp') {
      e.preventDefault();
      setSelectedIndex(prev => Math.max(prev - 1, -1));
    } else if (e.key === 'Enter' && selectedIndex >= 0 && results[selectedIndex]) {
      e.preventDefault();
      navigateToResult(results[selectedIndex]);
    } else if (e.key === 'Escape') {
      setShowDropdown(false);
      setMobileOpen(false);
      setQuery('');
      inputRef.current?.blur();
    }
  };

  const fieldLabel = (field: string) => {
    switch (field) {
      case 'sessionId': return 'Session ID';
      case 'serialNumber': return 'Serial';
      case 'deviceName': return 'Device';
      default: return field;
    }
  };

  const statusColor = (status: string) => {
    switch (status) {
      case 'Succeeded': return 'text-green-600';
      case 'Failed': return 'text-red-600';
      case 'InProgress': return 'text-blue-600';
      case 'Pending': return 'text-amber-600';
      case 'Stalled': return 'text-orange-600';
      default: return 'text-gray-500';
    }
  };

  const SearchIcon = () => (
    <svg className="w-4 h-4 text-gray-400" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
      <path strokeLinecap="round" strokeLinejoin="round" d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
    </svg>
  );

  const ResultsDropdown = ({ mobile }: { mobile?: boolean }) => {
    if (!showDropdown) return null;
    return (
      <div className={`absolute ${mobile ? 'left-0 right-0 mx-3' : 'left-0 w-full'} top-full mt-1 bg-white rounded-lg shadow-lg border border-gray-200 z-50 max-h-80 overflow-y-auto`}>
        {loading ? (
          <div className="px-4 py-3 text-sm text-gray-500 flex items-center gap-2">
            <svg className="w-4 h-4 animate-spin" fill="none" viewBox="0 0 24 24">
              <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
              <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4z" />
            </svg>
            Searching...
          </div>
        ) : results.length === 0 ? (
          <div className="px-4 py-3 text-sm text-gray-500">
            {query.length >= 2 ? 'No results found' : 'Type at least 2 characters'}
          </div>
        ) : (
          results.map((result, idx) => (
            <button
              key={result.sessionId}
              onClick={() => navigateToResult(result)}
              className={`w-full text-left px-4 py-2.5 flex items-center gap-3 transition-colors ${idx === selectedIndex ? 'bg-blue-50' : 'hover:bg-gray-50'}`}
            >
              <span className="text-gray-400 flex-shrink-0">
                <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                  <path strokeLinecap="round" strokeLinejoin="round" d="M9 5H7a2 2 0 00-2 2v12a2 2 0 002 2h10a2 2 0 002-2V7a2 2 0 00-2-2h-2M9 5a2 2 0 002 2h2a2 2 0 002-2M9 5a2 2 0 012-2h2a2 2 0 012 2" />
                </svg>
              </span>
              <div className="min-w-0 flex-1">
                <div className="flex items-center gap-2">
                  <p className="text-sm font-medium text-gray-900 truncate">
                    {result.deviceName || result.serialNumber || result.sessionId}
                  </p>
                  <span className={`text-[10px] font-semibold ${statusColor(result.status)}`}>
                    {result.status}
                  </span>
                </div>
                <p className="text-xs text-gray-500 truncate">
                  <span className="text-gray-400">Matched: </span>
                  {fieldLabel(result.matchedField)}
                  {result.serialNumber && ` \u00B7 ${result.serialNumber}`}
                </p>
              </div>
            </button>
          ))
        )}
      </div>
    );
  };

  return (
    <div className="flex-1 flex items-center px-3">
      {/* Desktop: inline search field centered (visible md+) */}
      <div ref={containerRef} className="hidden md:flex relative w-full max-w-md mx-auto">
        <div className="relative w-full">
          <div className="absolute inset-y-0 left-0 pl-3 flex items-center pointer-events-none">
            <SearchIcon />
          </div>
          <input
            ref={inputRef}
            type="text"
            value={query}
            onChange={(e) => handleInputChange(e.target.value)}
            onKeyDown={handleKeyDown}
            onFocus={() => { if (query.length >= 2) setShowDropdown(true); }}
            placeholder="Search serial, device, session... (Ctrl+K)"
            className="w-full pl-9 pr-8 py-1.5 text-sm bg-gray-100 border border-gray-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent focus:bg-white transition-colors"
          />
          {query && (
            <button
              onClick={() => { setQuery(''); setShowDropdown(false); inputRef.current?.focus(); }}
              className="absolute inset-y-0 right-0 pr-2.5 flex items-center text-gray-400 hover:text-gray-600 transition-colors"
              title="Clear search"
            >
              <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          )}
        </div>
        <ResultsDropdown />
      </div>

      {/* Mobile: magnifying glass button pushed right (visible <md) */}
      <button
        onClick={() => setMobileOpen(true)}
        className="md:hidden ml-auto p-2 rounded-lg hover:bg-gray-100 transition-colors"
        title="Search"
      >
        <svg className="w-5 h-5 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
          <path strokeLinecap="round" strokeLinejoin="round" d="M21 21l-6-6m2-5a7 7 0 11-14 0 7 7 0 0114 0z" />
        </svg>
      </button>

      {/* Mobile: expanded search overlay */}
      {mobileOpen && (
        <div
          ref={mobileContainerRef}
          className="md:hidden fixed inset-x-0 top-0 z-50 bg-white border-b border-gray-200 shadow-lg"
        >
          <div className="flex items-center h-14 px-3 gap-2">
            <div className="relative flex-1">
              <div className="absolute inset-y-0 left-0 pl-3 flex items-center pointer-events-none">
                <SearchIcon />
              </div>
              <input
                ref={mobileInputRef}
                type="text"
                value={query}
                onChange={(e) => handleInputChange(e.target.value)}
                onKeyDown={handleKeyDown}
                placeholder="Search serial, device, session..."
                className="w-full pl-9 pr-8 py-1.5 text-sm bg-gray-100 border border-gray-200 rounded-lg focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent focus:bg-white transition-colors"
              />
              {query && (
                <button
                  onClick={() => { setQuery(''); setShowDropdown(false); mobileInputRef.current?.focus(); }}
                  className="absolute inset-y-0 right-0 pr-2.5 flex items-center text-gray-400 hover:text-gray-600 transition-colors"
                  title="Clear search"
                >
                  <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                    <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
                  </svg>
                </button>
              )}
            </div>
            <button
              onClick={() => { setMobileOpen(false); setQuery(''); setShowDropdown(false); }}
              className="p-2 rounded-lg hover:bg-gray-100 transition-colors"
            >
              <svg className="w-5 h-5 text-gray-600" fill="none" viewBox="0 0 24 24" stroke="currentColor" strokeWidth={2}>
                <path strokeLinecap="round" strokeLinejoin="round" d="M6 18L18 6M6 6l12 12" />
              </svg>
            </button>
          </div>
          <ResultsDropdown mobile />
        </div>
      )}
    </div>
  );
}
