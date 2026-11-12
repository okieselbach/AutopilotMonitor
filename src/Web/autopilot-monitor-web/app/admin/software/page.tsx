"use client";

import { useAdminConfig } from "../AdminConfigContext";
import { SoftwareMappingSection } from "../components/SoftwareMappingSection";
import { AdminNotifications } from "../AdminNotifications";

export default function SoftwareMappingPage() {
  const { getAccessToken, setError, setSuccessMessage } = useAdminConfig();

  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
      <header className="bg-white dark:bg-gray-800 shadow dark:shadow-gray-700">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900 dark:text-white">Software Mapping</h1>
          <p className="text-sm text-gray-600 dark:text-gray-400 mt-1">CVE and software correlation management</p>
        </div>
      </header>
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8">
        <AdminNotifications />
        <SoftwareMappingSection
          getAccessToken={getAccessToken}
          setError={setError}
          setSuccessMessage={setSuccessMessage}
        />
      </main>
    </div>
  );
}
