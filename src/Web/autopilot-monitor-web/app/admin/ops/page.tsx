"use client";

import { useEffect } from "react";
import { useAdminConfig } from "../AdminConfigContext";
import { MaintenanceTriggerSection } from "../components/MaintenanceTriggerSection";
import { OpsEventsSection } from "../components/OpsEventsSection";
import { AdminNotifications } from "../AdminNotifications";

export default function OpsPage() {
  const {
    ensureAdminConfigLoaded,
    getAccessToken,
    setError,
    setSuccessMessage,
    opsEventRetentionDays,
    setOpsEventRetentionDays,
    handleSaveAdminConfig,
    savingConfig,
  } = useAdminConfig();

  // opsEventRetentionDays + handleSaveAdminConfig operate on adminConfig
  useEffect(() => { ensureAdminConfigLoaded(); }, [ensureAdminConfigLoaded]);

  return (
    <div className="min-h-screen bg-gray-50 dark:bg-gray-900">
      <header className="bg-white dark:bg-gray-800 shadow dark:shadow-gray-700">
        <div className="py-6 px-4 sm:px-6 lg:px-8">
          <h1 className="text-2xl font-normal text-gray-900 dark:text-white">Ops</h1>
          <p className="text-sm text-gray-600 dark:text-gray-400 mt-1">Platform operations, monitoring, and maintenance</p>
        </div>
      </header>
      <main className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 py-8 space-y-8">
        <AdminNotifications />
        <OpsEventsSection
          getAccessToken={getAccessToken}
          setError={setError}
          opsEventRetentionDays={opsEventRetentionDays}
          setOpsEventRetentionDays={setOpsEventRetentionDays}
          onSaveConfig={handleSaveAdminConfig}
          savingConfig={savingConfig}
        />
        <MaintenanceTriggerSection
          getAccessToken={getAccessToken}
          setError={setError}
          setSuccessMessage={setSuccessMessage}
        />
      </main>
    </div>
  );
}
