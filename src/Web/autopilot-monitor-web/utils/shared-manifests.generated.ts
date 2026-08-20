// GENERATED from shared-manifests.json — do not edit by hand.
// Regenerate: node scripts/generate-shared-manifest-types.js
// (after AM_WRITE_SHARED_MANIFESTS=1 dotnet test --filter SharedManifestParityTests)

export const SHARED_MANIFEST = {
  "$comment": "GENERATED from AutopilotMonitor.Shared by SharedManifestParityTests — do not edit by hand. Regenerate: AM_WRITE_SHARED_MANIFESTS=1 dotnet test --filter SharedManifestParityTests, then node scripts/generate-shared-manifest-types.js.",
  "adminConfiguration": {
    "fields": [
      "partitionKey",
      "rowKey",
      "lastUpdated",
      "updatedBy",
      "globalRateLimitRequestsPerMinute",
      "userRateLimitRequestsPerMinute",
      "globalAdminRateLimitRequestsPerMinute",
      "planTierDefinitionsJson",
      "platformStatsBlobSasUrl",
      "collectorIdleTimeoutMinutes",
      "desktopDetectorNoCandidateTimeoutMinutes",
      "excessiveEventCountThreshold",
      "excessiveEventAutoActionMode",
      "excessiveEventAutoActionThreshold",
      "excessiveEventAutoActionDurationHours",
      "opsEventRetentionDays",
      "slaNotificationCooldownHours",
      "feedbackEnabled",
      "feedbackMinTenantAgeDays",
      "feedbackCooldownDays",
      "diagnosticsGlobalLogPathsJson",
      "maxDiagnosticsDownloadSizeMB",
      "diagnosticsDownloadTimeoutSeconds",
      "mcpAccessPolicy",
      "nvdApiKey",
      "opsAlertRulesJson",
      "opsAlertTelegramEnabled",
      "opsAlertTelegramChatId",
      "opsAlertTeamsEnabled",
      "opsAlertTeamsWebhookUrl",
      "opsAlertSlackEnabled",
      "opsAlertSlackWebhookUrl",
      "latestAgentV2Version",
      "latestAgentV2Sha256",
      "latestAgentV2ExeSha256",
      "latestBootstrapV2ScriptVersion",
      "allowAgentDowngrade",
      "agentMigrateApiBaseUrl",
      "agentMigrateTenantOverridesJson",
      "modernDeploymentHarmlessEventIdsJson",
      "whiteGloveSealingPatternIdsJson",
      "vulnerabilityCorrelationEnabled",
      "enableIndexDualWrite",
      "autoApproveNewTenants",
      "imeMsiArchivingEnabled",
      "maxImeMsiDownloadSizeMB",
      "selfServiceAppHomingEnabled",
      "sessionDeletionKillSwitch",
      "vulnerabilityDataLastSyncUtc",
      "msrcLastSyncUtc"
    ]
  },
  "tenantConfiguration": {
    "fields": [
      "tenantId",
      "domainName",
      "lastUpdated",
      "updatedBy",
      "onboardedBy",
      "contactEmail",
      "onboardedAt",
      "homedAppClientId",
      "lastAuthClientId",
      "lastAuthClientIdSince",
      "disabled",
      "disabledReason",
      "disabledUntil",
      "customRateLimitRequestsPerMinute",
      "customUserRateLimitRequestsPerMinute",
      "planTier",
      "trialExpiresUtc",
      "trialStartedUtc",
      "trialConsumed",
      "trialGrantedBy",
      "manufacturerWhitelist",
      "modelWhitelist",
      "validateAutopilotDevice",
      "validateCorporateIdentifier",
      "validateDeviceAssociation",
      "validateCloudPcDevice",
      "allowInsecureAgentRequests",
      "dataRetentionDays",
      "sessionTimeoutHours",
      "sessionGraceHours",
      "maxNdjsonPayloadSizeMB",
      "enablePerformanceCollector",
      "performanceCollectorIntervalSeconds",
      "helloWaitTimeoutSeconds",
      "maxAuthFailures",
      "authFailureTimeoutMinutes",
      "agentMaxLifetimeMinutes",
      "absoluteMaxSessionHours",
      "selfDestructOnComplete",
      "keepLogFile",
      "rebootOnComplete",
      "rebootDelaySeconds",
      "enableGeoLocation",
      "ntpServer",
      "enableTimezoneAutoSet",
      "enableImeMatchLog",
      "enableGatherRuleDebugLog",
      "enableEspContinueAnywayObservation",
      "logLevel",
      "maxBatchSize",
      "showEnrollmentSummary",
      "enrollmentSummaryTimeoutSeconds",
      "enrollmentSummaryBrandingImageUrl",
      "enrollmentSummaryLaunchRetrySeconds",
      "showScriptOutput",
      "enableLocalAdminAnalyzer",
      "enableSoftwareInventoryAnalyzer",
      "enableIntegrityBypassAnalyzer",
      "enableRealmJoinWatcher",
      "keepAwakeDuringUserEsp",
      "enableConsoleBypassDetection",
      "localAdminAllowedAccountsJson",
      "bootstrapTokenEnabled",
      "unrestrictedModeEnabled",
      "unrestrictedMode",
      "entraAppRolesEnabled",
      "diagnosticsLogPathsJson",
      "diagnosticsBlobSasUrl",
      "diagnosticsUploadMode",
      "diagnosticsUploadDestination",
      "sendTraceEvents",
      "teamsWebhookUrl",
      "teamsNotifyOnSuccess",
      "teamsNotifyOnFailure",
      "teamsNotifyOnStart",
      "webhookProviderType",
      "webhookUrl",
      "webhookNotifyOnSuccess",
      "webhookNotifyOnFailure",
      "webhookNotifyOnHardwareRejection",
      "webhookNotifyOnStart",
      "webhookCustomHeadersJson",
      "notificationChannelsJson",
      "slaTargetSuccessRate",
      "slaTargetMaxDurationMinutes",
      "slaTargetAppInstallSuccessRate",
      "slaNotifyOnSuccessRateBreach",
      "slaSuccessRateNotifyThreshold",
      "slaNotifyOnDurationBreach",
      "slaNotifyOnAppInstallBreach",
      "slaNotifyOnConsecutiveFailures",
      "slaConsecutiveFailureThreshold"
    ]
  },
  "sessionSummary": {
    "fields": [
      {
        "name": "sessionId",
        "optional": false
      },
      {
        "name": "tenantId",
        "optional": false
      },
      {
        "name": "serialNumber",
        "optional": false
      },
      {
        "name": "deviceName",
        "optional": false
      },
      {
        "name": "manufacturer",
        "optional": false
      },
      {
        "name": "model",
        "optional": false
      },
      {
        "name": "startedAt",
        "optional": false
      },
      {
        "name": "completedAt",
        "optional": true
      },
      {
        "name": "currentPhase",
        "optional": false
      },
      {
        "name": "currentPhaseDetail",
        "optional": false
      },
      {
        "name": "status",
        "optional": false
      },
      {
        "name": "failureReason",
        "optional": false
      },
      {
        "name": "failureSource",
        "optional": false
      },
      {
        "name": "reconcileReason",
        "optional": false
      },
      {
        "name": "espSoftFailure",
        "optional": false
      },
      {
        "name": "completionSource",
        "optional": false
      },
      {
        "name": "adminMarkedAction",
        "optional": true
      },
      {
        "name": "validatedBy",
        "optional": false
      },
      {
        "name": "eventCount",
        "optional": false
      },
      {
        "name": "durationSeconds",
        "optional": true
      },
      {
        "name": "avgApiLatencyMs",
        "optional": true
      },
      {
        "name": "apiRequestCount",
        "optional": true
      },
      {
        "name": "connectionType",
        "optional": true
      },
      {
        "name": "enrollmentType",
        "optional": false
      },
      {
        "name": "diagnosticsBlobName",
        "optional": false
      },
      {
        "name": "diagnosticsBlobDestination",
        "optional": true
      },
      {
        "name": "lastEventAt",
        "optional": true
      },
      {
        "name": "lastIngestAt",
        "optional": true
      },
      {
        "name": "isPreProvisioned",
        "optional": false
      },
      {
        "name": "resumedAt",
        "optional": true
      },
      {
        "name": "stalledAt",
        "optional": true
      },
      {
        "name": "isHybridJoin",
        "optional": false
      },
      {
        "name": "isSelfDeployingProfile",
        "optional": false
      },
      {
        "name": "isCloudPc",
        "optional": false
      },
      {
        "name": "osName",
        "optional": false
      },
      {
        "name": "osBuild",
        "optional": false
      },
      {
        "name": "osDisplayVersion",
        "optional": false
      },
      {
        "name": "osEdition",
        "optional": false
      },
      {
        "name": "osLanguage",
        "optional": false
      },
      {
        "name": "isUserDriven",
        "optional": false
      },
      {
        "name": "agentVersion",
        "optional": false
      },
      {
        "name": "imeAgentVersion",
        "optional": false
      },
      {
        "name": "geoCountry",
        "optional": false
      },
      {
        "name": "geoRegion",
        "optional": false
      },
      {
        "name": "geoCity",
        "optional": false
      },
      {
        "name": "geoLoc",
        "optional": false
      },
      {
        "name": "platformScriptCount",
        "optional": false
      },
      {
        "name": "remediationScriptCount",
        "optional": false
      },
      {
        "name": "rebootCount",
        "optional": false
      },
      {
        "name": "excessiveEventsAlerted",
        "optional": false
      },
      {
        "name": "excessiveEventsAutoActioned",
        "optional": false
      },
      {
        "name": "pendingActionsJson",
        "optional": false
      },
      {
        "name": "pendingActionsQueuedAt",
        "optional": true
      },
      {
        "name": "failureSnapshotJson",
        "optional": false
      },
      {
        "name": "deletionState",
        "optional": false
      },
      {
        "name": "pendingDeletionManifestId",
        "optional": true
      }
    ]
  },
  "sessionStatuses": [
    "InProgress",
    "Pending",
    "Stalled",
    "Succeeded",
    "Failed",
    "Unknown",
    "AwaitingUser",
    "Incomplete"
  ],
  "enrollmentPhases": {
    "Start": 0,
    "DevicePreparation": 1,
    "DeviceSetup": 2,
    "AppsDevice": 3,
    "AccountSetup": 4,
    "AppsUser": 5,
    "FinalizingSetup": 6,
    "Complete": 7,
    "Failed": 99,
    "Unknown": -1
  },
  "eventSeverities": {
    "Debug": 0,
    "Info": 1,
    "Warning": 2,
    "Error": 3,
    "Critical": 4,
    "Trace": -1
  },
  "webhookProviderTypes": {
    "None": 0,
    "TeamsLegacyConnector": 1,
    "TeamsWorkflowWebhook": 2,
    "Slack": 10,
    "GenericJson": 20
  },
  "annotationLanes": [
    "operator",
    "tenantadmin",
    "globaladmin"
  ],
  "annotationVerdicts": [
    "root_cause_confirmed",
    "analysis_wrong",
    "different_problem",
    "inconclusive"
  ],
  "tenantRoles": [
    "Admin",
    "Operator",
    "Viewer"
  ],
  "globalRoles": [
    "GlobalAdmin",
    "GlobalReader"
  ],
  "delegatedRoles": [
    "DelegatedReader",
    "DelegatedAdmin"
  ],
  "analyzeRuleSources": [
    "app_install_duration",
    "event_correlation",
    "event_count",
    "event_data",
    "event_data_array",
    "event_type",
    "phase_duration"
  ],
  "analyzeRuleOperators": [
    "contains",
    "count_gte",
    "count_per_group_gte",
    "equals",
    "exists",
    "gt",
    "gte",
    "in",
    "lt",
    "lte",
    "not_contains",
    "not_equals",
    "not_exists",
    "not_in",
    "not_regex",
    "regex"
  ],
  "eventTypes": [
    "aad_join_status",
    "aad_placeholder_user_detected",
    "aad_user_joined_observed",
    "admin_marked_session",
    "agent_emergency_break",
    "agent_late_start",
    "agent_metrics_collector_stopped",
    "agent_metrics_snapshot",
    "agent_shutdown",
    "agent_shutting_down",
    "agent_started",
    "agent_trace",
    "agent_unrestricted_mode_changed",
    "agent_version_check",
    "all_apps_completed",
    "app_download_started",
    "app_install_completed",
    "app_install_failed",
    "app_install_skipped",
    "app_install_started",
    "app_install_starved",
    "app_state_reconciliation",
    "app_tracking_summary",
    "autologon_analysis",
    "autopilot_profile",
    "autopilot_profile_missing",
    "bitlocker_status",
    "boot_time",
    "cert_validation",
    "collector_degraded",
    "completion_check",
    "completion_waiting",
    "configmgr_client_detected",
    "console_prefetch_detected",
    "decision_process_completion",
    "desktop_arrived",
    "desktop_detector_first_poll",
    "desktop_detector_no_candidate",
    "desktop_detector_started",
    "device_location",
    "diagnostics_collecting",
    "diagnostics_upload_failed",
    "diagnostics_uploaded",
    "disk_space_low",
    "dns_configuration",
    "do_telemetry",
    "download_progress",
    "enrollment_complete",
    "enrollment_failed",
    "enrollment_summary_shown",
    "enrollment_type_detected",
    "enrollment_type_mismatch",
    "error_detected",
    "esp_apps_failure_correlation",
    "esp_appx_failure_analysis",
    "esp_config_detected",
    "esp_exiting",
    "esp_failure",
    "esp_failure_advisory",
    "esp_failure_advisory_resolved",
    "esp_failure_recovered",
    "esp_failure_retry_detected",
    "esp_failure_settle_recovered",
    "esp_failure_settle_started",
    "esp_phase_changed",
    "esp_policy_provider_stalled",
    "esp_provisioning_raw",
    "esp_provisioning_settle_started",
    "esp_provisioning_status",
    "esp_resumed",
    "esp_state_change",
    "esp_ui_state",
    "gather_result",
    "gather_rules_collection_completed",
    "gather_rules_collection_started",
    "hardware_spec",
    "hello_completion_timeout",
    "hello_pin_status",
    "hello_policy_detected",
    "hello_policy_detection_mismatch",
    "hello_processing_started",
    "hello_processing_stopped",
    "hello_provisioning_blocked",
    "hello_provisioning_completed",
    "hello_provisioning_failed",
    "hello_skipped",
    "hello_wait_timeout",
    "hello_wizard_started",
    "historic_ime_replay_detected",
    "hybrid_login_pending",
    "ime_agent_version",
    "ime_process_exited",
    "ime_session_change",
    "ime_token_failure",
    "ime_user_session_completed",
    "ingress_backpressure",
    "integrity_bypass_analysis",
    "keep_awake_engaged",
    "keep_awake_released",
    "local_admin_analysis",
    "log_entry",
    "mdm_policy_reboot_required",
    "modern_deployment_error",
    "modern_deployment_log",
    "modern_deployment_warning",
    "network_adapters",
    "network_bandwidth_estimate",
    "network_connectivity_check",
    "network_interface_info",
    "network_state_change",
    "ntp_time_check",
    "office_install_completed",
    "office_install_failed",
    "office_install_progress",
    "office_install_started",
    "office_preinstalled_detected",
    "oobe_console_spawned",
    "oobe_state_completed",
    "os_build_changed",
    "os_info",
    "outbound_ip",
    "performance_collector_stopped",
    "performance_snapshot",
    "phase_transition",
    "power_state_check",
    "previous_crash_detected",
    "prior_run_died_with_state",
    "provisioning_package_scan",
    "proxy_configuration",
    "realmjoin_detected",
    "realmjoin_first_deployment_incomplete",
    "realmjoin_package_completed",
    "realmjoin_package_started",
    "realmjoin_phase_changed",
    "realmjoin_resolved",
    "realmjoin_timeout",
    "reboot_triggered",
    "registry_app_baseline",
    "registry_app_state",
    "remote_config_fetch_failed",
    "script_completed",
    "script_failed",
    "script_started",
    "script_timeout_suspected",
    "secureboot_status",
    "security_audit",
    "security_warning",
    "server_action_executed",
    "server_action_failed",
    "server_action_received",
    "session_parked_without_deadline",
    "session_stalled",
    "session_timeout",
    "shadow_discrepancy",
    "software_inventory_analysis",
    "spool_pressure_detected",
    "stall_probe_check",
    "stall_probe_result",
    "state_quarantine_recovered",
    "system_reboot_detected",
    "telemetry_upload_blocked",
    "telemetry_upload_poisoned",
    "timezone_auto_set",
    "tpm_status",
    "vulnerability_report",
    "waiting_for_hello",
    "whiteglove_classification",
    "whiteglove_complete",
    "whiteglove_part1_complete",
    "whiteglove_resumed",
    "whiteglove_started",
    "wifi_signal_info",
    "windows_update_channel_census",
    "windows_update_failed",
    "windows_update_history",
    "windows_update_reboot_pending",
    "windows_update_started",
    "windows_update_succeeded"
  ],
  "signalRMessages": [
    "newSession",
    "newevents",
    "eventStream",
    "ruleResultsReady",
    "vulnerabilityReportReady",
    "sessionDeleted",
    "tenantNotification",
    "tenantNotificationDismissed",
    "tenantNotificationsDismissedAll",
    "globalNotification",
    "globalNotificationDismissed",
    "globalNotificationsDismissedAll",
    "accessRevoked"
  ]
} as const;
