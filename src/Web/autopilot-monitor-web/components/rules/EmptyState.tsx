"use client";

interface EmptyStateProps {
  message: string;
  onClearFilters: () => void;
  showClearButton?: boolean;
}

export function EmptyState({ message, onClearFilters, showClearButton = true }: EmptyStateProps) {
  return (
    <div className="bg-white rounded-lg shadow p-8 text-center">
      <svg className="w-12 h-12 text-gray-400 mx-auto" fill="none" stroke="currentColor" viewBox="0 0 24 24">
        <path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9.172 16.172a4 4 0 015.656 0M9 10h.01M15 10h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z" />
      </svg>
      <h3 className="mt-4 text-lg font-medium text-gray-900">No items found</h3>
      <p className="mt-2 text-sm text-gray-500">{message}</p>
      {showClearButton && (
        <button
          onClick={onClearFilters}
          className="mt-2 text-sm text-green-700 hover:text-green-800 transition-colors"
        >
          Clear all filters
        </button>
      )}
    </div>
  );
}
