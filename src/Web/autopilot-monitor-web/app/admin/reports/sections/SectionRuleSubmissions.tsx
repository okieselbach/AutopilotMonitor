"use client";

import { useAdminConfig } from "../../AdminConfigContext";
import { RuleSubmissionsSection } from "../../components/RuleSubmissionsSection";
import { AdminNotifications } from "../../AdminNotifications";

export function SectionRuleSubmissions() {
  const { getAccessToken, setError } = useAdminConfig();
  return (
    <>
      <AdminNotifications />
      <RuleSubmissionsSection getAccessToken={getAccessToken} setError={setError} />
    </>
  );
}
