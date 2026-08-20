/**
 * AUTO-GENERATED from rules/guardrails.json + rules/schema/*.schema.json — DO NOT EDIT.
 * Run: node rules/scripts/combine.js
 *
 * Consumed by the MCP server's rule-authoring surface (get_resource +
 * validate_rule): the JSON Schemas are the validation contract, the guardrails
 * are the agent-side collection allowlists. Single source of truth is rules/;
 * the CI guardrails-in-sync job guards this file against drift.
 */

/** JSON Schema (2020-12) for gather rules — rules/schema/gather-rule.schema.json verbatim. */
export const GATHER_RULE_SCHEMA: Record<string, unknown> = {
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://raw.githubusercontent.com/OliverKieselbach/Autopilot-Monitor/main/rules/schema/gather-rule.schema.json",
  "title": "Autopilot Monitor Gather Rule",
  "description": "Defines what data the agent should collect from a Windows device during Autopilot enrollment.",
  "oneOf": [
    {
      "$ref": "#/$defs/gatherRule"
    },
    {
      "type": "object",
      "properties": {
        "$schema": {
          "type": "string"
        },
        "rules": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/gatherRule"
          }
        }
      },
      "required": [
        "rules"
      ],
      "additionalProperties": false
    }
  ],
  "$defs": {
    "gatherRule": {
      "type": "object",
      "properties": {
        "$schema": {
          "type": "string",
          "description": "JSON Schema reference for editor IntelliSense."
        },
        "ruleId": {
          "type": "string",
          "description": "Unique rule identifier (e.g., \"GATHER-NET-001\").",
          "pattern": "^GATHER-[A-Z]+-\\d{3}$"
        },
        "title": {
          "type": "string",
          "description": "Human-readable rule title (e.g., \"Collect WinHTTP Proxy Settings\")."
        },
        "description": {
          "type": "string",
          "description": "Detailed description of what this rule collects and why."
        },
        "category": {
          "type": "string",
          "description": "Rule category.",
          "enum": [
            "network",
            "identity",
            "apps",
            "device",
            "esp",
            "enrollment"
          ]
        },
        "version": {
          "type": "string",
          "description": "Semantic version of this rule (e.g., \"1.0.0\").",
          "default": "1.0.0"
        },
        "author": {
          "type": "string",
          "description": "Author of this rule.",
          "default": "Autopilot Monitor"
        },
        "enabled": {
          "type": "boolean",
          "description": "Whether this rule is enabled. Set to false to add a rule without activating it immediately.",
          "default": true
        },
        "isCommunity": {
          "type": "boolean",
          "description": "Whether this is a community-contributed rule. Community rules are stored in the global partition alongside built-in rules.",
          "default": false
        },
        "collectorType": {
          "type": "string",
          "description": "Type of data collection.",
          "enum": [
            "registry",
            "eventlog",
            "wmi",
            "file",
            "json",
            "xml",
            "command_allowlisted",
            "logparser"
          ]
        },
        "target": {
          "type": "string",
          "description": "Target for collection. Registry path, event log name, WMI query, file path, command string, or log file path depending on collectorType."
        },
        "parameters": {
          "type": "object",
          "description": "Additional parameters for the collector (all values are strings).",
          "additionalProperties": {
            "type": "string"
          }
        },
        "trigger": {
          "type": "string",
          "description": "When to collect data. \"phase_change\" fires once when triggerPhase is entered, \"phase_exit\" once when it is left.",
          "enum": [
            "startup",
            "phase_change",
            "phase_exit",
            "interval",
            "on_event"
          ]
        },
        "intervalSeconds": {
          "type": "integer",
          "description": "Interval in seconds (only used when trigger = \"interval\").",
          "minimum": 1
        },
        "triggerPhase": {
          "type": "string",
          "description": "Phase to trigger on (used when trigger = \"phase_change\" or \"phase_exit\"). Empty = every phase transition."
        },
        "triggerEventType": {
          "type": "string",
          "description": "Event type to trigger on (only used when trigger = \"on_event\")."
        },
        "activePhases": {
          "type": "array",
          "description": "Run the rule only while the current enrollment phase is one of these phases (EnrollmentPhase enum names). Empty/absent = unrestricted. Mutually exclusive with activeFromPhase.",
          "items": {
            "type": "string",
            "enum": [
              "Start",
              "DevicePreparation",
              "DeviceSetup",
              "AppsDevice",
              "AccountSetup",
              "AppsUser",
              "FinalizingSetup",
              "Complete"
            ]
          }
        },
        "activeFromPhase": {
          "type": "string",
          "description": "Activate the rule once the enrollment phase first reaches this phase, then keep it active for the rest of the session (sticky latch). Absent = unrestricted. Mutually exclusive with activePhases.",
          "enum": [
            "Start",
            "DevicePreparation",
            "DeviceSetup",
            "AppsDevice",
            "AccountSetup",
            "AppsUser",
            "FinalizingSetup",
            "Complete"
          ]
        },
        "emitMode": {
          "type": "string",
          "description": "Emit behavior: \"always\" (default — emit on every collection) or \"on_change\" (poll on the trigger cadence but emit only when the collected result changes; the first in-scope result always emits).",
          "enum": [
            "always",
            "on_change"
          ]
        },
        "outputEventType": {
          "type": "string",
          "description": "EventType for the emitted event (e.g., \"gather_proxy_settings\")."
        },
        "outputSeverity": {
          "type": "string",
          "description": "Severity for the emitted event.",
          "enum": [
            "Info",
            "Warning",
            "Error",
            "Critical"
          ],
          "default": "Info"
        },
        "tags": {
          "type": "array",
          "description": "Tags for filtering and categorization.",
          "items": {
            "type": "string"
          },
          "default": []
        }
      },
      "required": [
        "ruleId",
        "title",
        "collectorType",
        "target",
        "trigger",
        "outputEventType"
      ],
      "additionalProperties": false
    }
  }
};

/** JSON Schema (2020-12) for analyze rules — rules/schema/analyze-rule.schema.json verbatim. */
export const ANALYZE_RULE_SCHEMA: Record<string, unknown> = {
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://raw.githubusercontent.com/OliverKieselbach/Autopilot-Monitor/main/rules/schema/analyze-rule.schema.json",
  "title": "Autopilot Monitor Analyze Rule",
  "description": "Defines how to analyze collected events to detect issues during Autopilot enrollment.",
  "oneOf": [
    {
      "$ref": "#/$defs/analyzeRule"
    },
    {
      "type": "object",
      "properties": {
        "$schema": {
          "type": "string"
        },
        "rules": {
          "type": "array",
          "items": {
            "$ref": "#/$defs/analyzeRule"
          }
        }
      },
      "required": [
        "rules"
      ],
      "additionalProperties": false
    }
  ],
  "$defs": {
    "analyzeRule": {
      "type": "object",
      "properties": {
        "$schema": {
          "type": "string",
          "description": "JSON Schema reference for editor IntelliSense."
        },
        "ruleId": {
          "type": "string",
          "description": "Unique rule identifier (e.g., \"ANALYZE-APP-001\").",
          "pattern": "^ANALYZE-[A-Z]+-\\d{3}$"
        },
        "title": {
          "type": "string",
          "description": "Human-readable rule title (e.g., \"Proxy Authentication Required\")."
        },
        "description": {
          "type": "string",
          "description": "Detailed description of what this rule detects."
        },
        "severity": {
          "type": "string",
          "description": "Severity level of the detected issue.",
          "enum": [
            "info",
            "warning",
            "high",
            "critical"
          ]
        },
        "category": {
          "type": "string",
          "description": "Rule category.",
          "enum": [
            "network",
            "identity",
            "enrollment",
            "apps",
            "esp",
            "device",
            "security"
          ]
        },
        "version": {
          "type": "string",
          "description": "Semantic version of this rule (e.g., \"1.0.0\").",
          "default": "1.0.0"
        },
        "author": {
          "type": "string",
          "description": "Author of this rule.",
          "default": "Autopilot Monitor"
        },
        "enabled": {
          "type": "boolean",
          "description": "Whether this rule is enabled. Set to false to add a rule without activating it immediately.",
          "default": true
        },
        "isCommunity": {
          "type": "boolean",
          "description": "Whether this is a community-contributed rule. Community rules are stored in the global partition alongside built-in rules.",
          "default": false
        },
        "trigger": {
          "type": "string",
          "description": "Rule trigger type: \"single\" matches individual events, \"correlation\" combines multiple event types.",
          "enum": [
            "single",
            "correlation"
          ],
          "default": "single"
        },
        "evaluateOn": {
          "type": "array",
          "description": "When this rule is evaluated. Absent = [\"enrollment_end\"] (terminal-only, the historical behavior). Interim triggers let the rule fire before the session is terminal: \"whiteglove_sealed\" (first genuine WhiteGlove seal) and \"on_event:<eventType>\" (an ingest batch contained that event type). High-frequency telemetry event types are HARD-BLOCKED as on_event triggers (guardrails blockedInterimTriggerEventTypes): the backend rejects them on save and the runtime ignores them. Interim runs suppress markSessionAsFailed and record no stats; findings notify once per (session, rule). Interim-enabled rules need monotonic conditions — a not_exists precondition on enrollment_complete/enrollment_failed/session_timeout passes trivially mid-run.",
          "items": {
            "type": "string",
            "pattern": "^(enrollment_end|whiteglove_sealed|on_event:[a-z0-9_]{1,128})$"
          },
          "minItems": 1,
          "uniqueItems": true
        },
        "conditions": {
          "type": "array",
          "description": "Conditions evaluated against the event stream. All required conditions must match for the rule to fire.",
          "items": {
            "$ref": "#/$defs/ruleCondition"
          },
          "minItems": 1
        },
        "preconditions": {
          "type": "array",
          "description": "Optional device-fact gates evaluated BEFORE conditions. ALL preconditions must pass; if any fails the rule is silently skipped — no result, no UI card. Useful for filtering out hardware/OS profiles where the rule does not apply (e.g. virtual machines).",
          "items": {
            "$ref": "#/$defs/rulePrecondition"
          },
          "default": []
        },
        "baseConfidence": {
          "type": "integer",
          "description": "Base confidence score (0-100) when required conditions match.",
          "minimum": 0,
          "maximum": 100,
          "default": 50
        },
        "confidenceFactors": {
          "type": "array",
          "description": "Additional factors that increase confidence when matched.",
          "items": {
            "$ref": "#/$defs/confidenceFactor"
          },
          "default": []
        },
        "confidenceThreshold": {
          "type": "integer",
          "description": "Minimum confidence score (0-100) to create a RuleResult.",
          "minimum": 0,
          "maximum": 100,
          "default": 40
        },
        "explanation": {
          "type": "string",
          "description": "Detailed explanation of the detected issue. Supports markdown formatting."
        },
        "remediation": {
          "type": "array",
          "description": "Steps to remediate the detected issue.",
          "items": {
            "$ref": "#/$defs/remediationStep"
          },
          "default": []
        },
        "relatedDocs": {
          "type": "array",
          "description": "Links to relevant documentation.",
          "items": {
            "$ref": "#/$defs/relatedDoc"
          },
          "default": []
        },
        "tags": {
          "type": "array",
          "description": "Tags for filtering and categorization.",
          "items": {
            "type": "string"
          },
          "default": []
        },
        "markSessionAsFailedDefault": {
          "type": "boolean",
          "description": "When true, a firing of this rule causes the entire enrollment session to be marked as failed (KO criterion). Tenants can override this via the portal.",
          "default": false
        },
        "templateVariables": {
          "type": "array",
          "description": "Template variables that must be customized per-tenant. If non-empty, the rule is a template: enabling it creates a tenant custom copy with the user's values substituted.",
          "items": {
            "$ref": "#/$defs/templateVariable"
          },
          "default": []
        },
        "derivedFromTemplateRuleId": {
          "type": "string",
          "description": "If this custom rule was created from a template, the original template rule's ID. Used to track lineage and prevent duplicate copies."
        }
      },
      "required": [
        "ruleId",
        "title",
        "severity",
        "category",
        "conditions",
        "explanation"
      ],
      "additionalProperties": false
    },
    "ruleCondition": {
      "type": "object",
      "description": "A condition evaluated against the event stream.",
      "properties": {
        "signal": {
          "type": "string",
          "description": "Descriptive name for this signal (e.g., \"proxy_407_error\")."
        },
        "source": {
          "type": "string",
          "description": "Source of the signal.",
          "enum": [
            "event_type",
            "event_data",
            "event_data_array",
            "phase_duration",
            "event_count",
            "app_install_duration",
            "event_correlation",
            "clock_skew"
          ]
        },
        "skewMetric": {
          "type": "string",
          "description": "For clock_skew only: which device-clock metric to evaluate. clock_jump = persistent step in the device's clock frame mid-session; sustained_offset = whole session ran on a clock off by at least the threshold. value is the threshold in seconds; operator is limited to gt/gte on the magnitude. IME-log-derived events are excluded from the measurement.",
          "enum": [
            "clock_jump",
            "sustained_offset"
          ]
        },
        "eventType": {
          "type": "string",
          "description": "Event type to match on. For event_correlation: the first event type (Event A)."
        },
        "dataField": {
          "type": "string",
          "description": "Data field to match on. Uses dot notation for nested fields (e.g., \"data.errorCode\"). For event_data_array: the array field to iterate (e.g., \"artifacts\")."
        },
        "itemField": {
          "type": "string",
          "description": "For event_data_array only: the sub-field on each array element to test with operator/value (e.g., \"identity\"). Empty = treat each element as a scalar. The condition matches when ANY element satisfies the operator."
        },
        "operator": {
          "type": "string",
          "description": "Comparison operator.",
          "enum": [
            "equals",
            "not_equals",
            "contains",
            "not_contains",
            "regex",
            "not_regex",
            "gt",
            "lt",
            "gte",
            "lte",
            "exists",
            "not_exists",
            "count_gte",
            "count_per_group_gte",
            "in",
            "not_in"
          ]
        },
        "value": {
          "type": "string",
          "description": "Value to compare against."
        },
        "required": {
          "type": "boolean",
          "description": "Whether this condition must match for the rule to fire. If false, it only contributes to confidence scoring.",
          "default": false
        },
        "filterField": {
          "type": "string",
          "description": "For event_count only: optional value filter — only events whose filterField satisfies filterOperator/filterValue are counted (e.g. count only performance_snapshot events with memory_used_percent > 90). Applies before counting for both count_gte and count_per_group_gte."
        },
        "filterOperator": {
          "type": "string",
          "description": "Operator for the event_count value filter.",
          "enum": [
            "equals",
            "not_equals",
            "contains",
            "not_contains",
            "regex",
            "not_regex",
            "gt",
            "lt",
            "gte",
            "lte",
            "exists",
            "not_exists",
            "in",
            "not_in"
          ]
        },
        "filterValue": {
          "type": "string",
          "description": "Value for the event_count value filter."
        },
        "correlateEventType": {
          "type": "string",
          "description": "The second event type to correlate with (Event B). Only used when source = \"event_correlation\"."
        },
        "joinField": {
          "type": "string",
          "description": "Data field to join on — must have the same value in both Event A and Event B."
        },
        "timeWindowSeconds": {
          "type": "integer",
          "description": "Maximum time in seconds between Event A and Event B. Null or 0 means no time limit.",
          "minimum": 0
        },
        "suppressByEvent": {
          "type": "object",
          "description": "Optional suppression: if an event of the specified type exists with the same joinField value as the matched event, the match is skipped. Used to prevent rules from firing when a subsequent event resolved the issue (e.g., app_install_completed suppresses app_install_failed for the same appId).",
          "properties": {
            "eventType": {
              "type": "string",
              "description": "The event type that resolves/suppresses the matched event (e.g., \"app_install_completed\")."
            },
            "joinField": {
              "type": "string",
              "description": "The data field to join on — must have the same value in both the matched event and the suppressing event (e.g., \"appId\")."
            }
          },
          "required": [
            "eventType",
            "joinField"
          ],
          "additionalProperties": false
        },
        "eventAFilterField": {
          "type": "string",
          "description": "Optional filter field on Event A (the first event)."
        },
        "eventAFilterOperator": {
          "type": "string",
          "description": "Operator for the Event A filter.",
          "enum": [
            "equals",
            "not_equals",
            "contains",
            "not_contains",
            "regex",
            "not_regex",
            "gt",
            "lt",
            "gte",
            "lte",
            "exists",
            "not_exists",
            "count_gte",
            "count_per_group_gte",
            "in",
            "not_in"
          ]
        },
        "eventAFilterValue": {
          "type": "string",
          "description": "Value for the Event A filter."
        }
      },
      "required": [
        "signal",
        "source"
      ],
      "additionalProperties": false
    },
    "rulePrecondition": {
      "type": "object",
      "description": "A device-fact gate evaluated before conditions. Pure boolean filter — does not contribute to evidence or confidence.",
      "properties": {
        "source": {
          "type": "string",
          "description": "Currently only event_data is supported.",
          "enum": [
            "event_data"
          ],
          "default": "event_data"
        },
        "eventType": {
          "type": "string",
          "description": "Event type carrying the field to test (e.g., \"hardware_spec\", \"os_info\", \"tpm_status\")."
        },
        "dataField": {
          "type": "string",
          "description": "Data field to test. Uses dot notation for nested fields."
        },
        "operator": {
          "type": "string",
          "description": "Comparison operator. Same vocabulary as conditions.",
          "enum": [
            "equals",
            "not_equals",
            "contains",
            "not_contains",
            "regex",
            "not_regex",
            "gt",
            "lt",
            "gte",
            "lte",
            "exists",
            "not_exists",
            "in",
            "not_in"
          ]
        },
        "value": {
          "type": "string",
          "description": "Value to compare against. Boolean values are stringified (\"true\"/\"false\")."
        },
        "description": {
          "type": "string",
          "description": "Human-readable note explaining the intent (e.g., \"skip on virtual machines\")."
        }
      },
      "required": [
        "source",
        "operator"
      ],
      "additionalProperties": false
    },
    "confidenceFactor": {
      "type": "object",
      "description": "A factor that increases confidence when matched.",
      "properties": {
        "signal": {
          "type": "string",
          "description": "Descriptive name for this factor."
        },
        "condition": {
          "type": "string",
          "description": "Condition expression (e.g., \"count >= 5\", \"exists\", \"duration > 300\")."
        },
        "weight": {
          "type": "integer",
          "description": "Confidence weight to add when this factor matches (0-100).",
          "minimum": 0,
          "maximum": 100
        }
      },
      "required": [
        "signal",
        "condition",
        "weight"
      ],
      "additionalProperties": false
    },
    "remediationStep": {
      "type": "object",
      "description": "A remediation step with title and sub-steps.",
      "properties": {
        "title": {
          "type": "string",
          "description": "Title of the remediation approach."
        },
        "steps": {
          "type": "array",
          "description": "Ordered steps to execute.",
          "items": {
            "type": "string"
          }
        }
      },
      "required": [
        "title",
        "steps"
      ],
      "additionalProperties": false
    },
    "relatedDoc": {
      "type": "object",
      "description": "A link to related documentation.",
      "properties": {
        "title": {
          "type": "string",
          "description": "Display title for the link."
        },
        "url": {
          "type": "string",
          "description": "URL to the documentation.",
          "format": "uri"
        }
      },
      "required": [
        "title",
        "url"
      ],
      "additionalProperties": false
    },
    "templateVariable": {
      "type": "object",
      "description": "A variable in a rule condition that must be customized per-tenant before the rule can be used.",
      "properties": {
        "name": {
          "type": "string",
          "description": "Machine name for this variable (e.g., \"cert_subject\")."
        },
        "label": {
          "type": "string",
          "description": "Human-readable label shown in the configuration UI."
        },
        "description": {
          "type": "string",
          "description": "Help text explaining what value is expected."
        },
        "conditionIndex": {
          "type": "integer",
          "description": "Zero-based index into the conditions array where this variable lives.",
          "minimum": 0
        },
        "field": {
          "type": "string",
          "description": "Which field on the condition to customize.",
          "enum": [
            "value",
            "eventType",
            "dataField",
            "eventAFilterValue"
          ],
          "default": "value"
        },
        "placeholder": {
          "type": "string",
          "description": "The placeholder value that ships with the template (e.g., \"CN=YOUR-CERTIFICATE-SUBJECT\")."
        },
        "validation": {
          "type": "string",
          "description": "Optional regex pattern to validate user input.",
          "format": "regex"
        }
      },
      "required": [
        "name",
        "label",
        "conditionIndex",
        "field",
        "placeholder"
      ],
      "additionalProperties": false
    }
  }
};

/** Gather-rule collection guardrails — rules/guardrails.json verbatim. */
export const RULE_GUARDRAILS = {
  "registryPrefixes": [
    {
      "category": "MDM / Enrollment",
      "prefixes": [
        "SOFTWARE\\Microsoft\\Enrollments",
        "SOFTWARE\\Microsoft\\EnterpriseDesktopAppManagement",
        "SOFTWARE\\Microsoft\\Provisioning",
        "SOFTWARE\\Microsoft\\PolicyManager",
        "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\MDM"
      ]
    },
    {
      "category": "AAD / Entra Join",
      "prefixes": [
        "SOFTWARE\\Microsoft\\IdentityStore",
        "SYSTEM\\CurrentControlSet\\Control\\CloudDomainJoin"
      ]
    },
    {
      "category": "Windows Update / WUfB",
      "prefixes": [
        "SOFTWARE\\Microsoft\\WindowsUpdate",
        "SOFTWARE\\Policies\\Microsoft\\Windows\\WindowsUpdate"
      ]
    },
    {
      "category": "BitLocker",
      "prefixes": [
        "SOFTWARE\\Microsoft\\BitLocker",
        "SYSTEM\\CurrentControlSet\\Control\\BitLockerStatus"
      ]
    },
    {
      "category": "Network / Proxy",
      "prefixes": [
        "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Internet Settings",
        "SYSTEM\\CurrentControlSet\\Services\\Tcpip"
      ]
    },
    {
      "category": "Autopilot / OOBE / Setup",
      "prefixes": [
        "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Setup",
        "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\OOBE",
        "SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\Winlogon",
        "SOFTWARE\\Microsoft\\Windows\\Autopilot"
      ]
    },
    {
      "category": "TPM",
      "prefixes": [
        "SYSTEM\\CurrentControlSet\\Services\\TPM",
        "SOFTWARE\\Microsoft\\Tpm"
      ]
    },
    {
      "category": "Secure Boot",
      "prefixes": [
        "SYSTEM\\CurrentControlSet\\Control\\SecureBoot"
      ]
    },
    {
      "category": "Intune IME",
      "prefixes": [
        "SOFTWARE\\Microsoft\\IntuneManagementExtension"
      ]
    },
    {
      "category": "Certificates (SCEP)",
      "prefixes": [
        "SOFTWARE\\Microsoft\\SystemCertificates",
        "SOFTWARE\\Policies\\Microsoft\\SystemCertificates"
      ]
    },
    {
      "category": "Servicing",
      "prefixes": [
        "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Component Based Servicing"
      ]
    },
    {
      "category": "RealmJoin",
      "prefixes": [
        "SYSTEM\\CurrentControlSet\\Services\\realmjoin",
        "SOFTWARE\\RealmJoin"
      ]
    }
  ],
  "filePrefixes": [
    "C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs",
    "C:\\Windows\\CCM\\Logs",
    "C:\\Windows\\Logs",
    "C:\\Windows\\Panther",
    "C:\\Windows\\SetupDiag",
    "C:\\ProgramData\\Microsoft\\DiagnosticLogCSP",
    "C:\\Windows\\SoftwareDistribution\\ReportingEvents.log"
  ],
  "wmiQueryPrefixes": [
    "SELECT * FROM Win32_OperatingSystem",
    "SELECT * FROM Win32_ComputerSystem",
    "SELECT * FROM Win32_BIOS",
    "SELECT * FROM Win32_Processor",
    "SELECT * FROM Win32_BaseBoard",
    "SELECT * FROM Win32_Battery",
    "SELECT * FROM Win32_TPM",
    "SELECT * FROM Win32_NetworkAdapter",
    "SELECT * FROM Win32_NetworkAdapterConfiguration",
    "SELECT * FROM Win32_DiskDrive",
    "SELECT * FROM Win32_LogicalDisk",
    "SELECT * FROM SoftwareLicensingProduct"
  ],
  "eventLogChannels": [
    {
      "category": "Core Windows logs",
      "channels": [
        "Application",
        "System",
        "Setup",
        "Microsoft-Windows-Kernel-Boot",
        "Microsoft-Windows-Diagnostics-Performance"
      ]
    },
    {
      "category": "MDM / Enrollment",
      "channels": [
        "Microsoft-Windows-DeviceManagement-Enterprise-Diagnostics-Provider",
        "Microsoft-Windows-ModernDeployment-Diagnostics-Provider",
        "Microsoft-Windows-Provisioning-Diagnostics-Provider",
        "Microsoft-Windows-AAD",
        "Microsoft-Windows-User Device Registration"
      ]
    },
    {
      "category": "ESP / Shell / Apps",
      "channels": [
        "Microsoft-Windows-Shell-Core",
        "Microsoft-Windows-AppXDeployment",
        "Microsoft-Windows-AppXDeploymentServer",
        "Microsoft-Windows-AppReadiness",
        "Microsoft-Windows-Store"
      ]
    },
    {
      "category": "Security posture / Crypto",
      "channels": [
        "Microsoft-Windows-BitLocker-API",
        "Microsoft-Windows-BitLocker-DrivePreparationTool",
        "Microsoft-Windows-TPM-WMI",
        "Microsoft-Windows-CertificateServicesClient-Lifecycle-System"
      ]
    },
    {
      "category": "Update / Servicing",
      "channels": [
        "Microsoft-Windows-WindowsUpdateClient"
      ]
    },
    {
      "category": "Logon / Session",
      "channels": [
        "Microsoft-Windows-Winlogon",
        "Microsoft-Windows-User Profile Service",
        "Microsoft-Windows-GroupPolicy",
        "Microsoft-Windows-TaskScheduler"
      ]
    },
    {
      "category": "Network",
      "channels": [
        "Microsoft-Windows-NetworkProfile",
        "Microsoft-Windows-Dhcp-Client",
        "Microsoft-Windows-DNS-Client",
        "Microsoft-Windows-NCSI",
        "Microsoft-Windows-WLAN-AutoConfig",
        "Microsoft-Windows-Time-Service"
      ]
    }
  ],
  "blockedEventLogChannels": [
    "Security",
    "Microsoft-Windows-PowerShell",
    "Windows PowerShell",
    "Microsoft-Windows-Sysmon"
  ],
  "allowedCommands": [
    {
      "category": "TPM and Security",
      "commands": [
        "Get-Tpm",
        "Get-SecureBootPolicy",
        "Get-SecureBootUEFI -Name SetupMode"
      ]
    },
    {
      "category": "BitLocker",
      "commands": [
        "Get-BitLockerVolume -MountPoint C:"
      ]
    },
    {
      "category": "Network",
      "commands": [
        "Get-NetAdapter | Select-Object Name, Status, InterfaceDescription, MacAddress, LinkSpeed",
        "Get-DnsClientServerAddress | Select-Object InterfaceAlias, ServerAddresses",
        "Get-NetIPConfiguration | Select-Object InterfaceAlias, IPv4Address, IPv4DefaultGateway, DNSServer",
        "netsh winhttp show proxy",
        "ipconfig /all"
      ]
    },
    {
      "category": "Domain / Identity",
      "commands": [
        "nltest /dsgetdc:",
        "dsregcmd /status"
      ]
    },
    {
      "category": "Certificate",
      "commands": [
        "certutil -store My",
        "$c = Get-ChildItem Cert:\\LocalMachine\\My; if ($c) { $c | Select-Object Subject, Thumbprint, Issuer, NotBefore, NotAfter, HasPrivateKey | ConvertTo-Json } else { '{\"CertificateCount\": 0}' }"
      ]
    },
    {
      "category": "Windows Update",
      "commands": [
        "Get-HotFix | Select-Object -First 10 HotFixID, InstalledOn, Description"
      ]
    },
    {
      "category": "Autopilot / Hardware Identity",
      "commands": [
        "try { $cs = Get-CimInstance -ClassName Win32_ComputerSystem; $bios = Get-CimInstance -ClassName Win32_BIOS; $hash = $null; try { $hash = (Get-CimInstance -Namespace root/cimv2/mdm/dmmap -ClassName MDM_DevDetail_Ext01 -Filter \"InstanceID='Ext' AND ParentID='./DevDetail'\" -ErrorAction Stop).DeviceHardwareData } catch { $hash = \"ERROR: $($_.Exception.Message)\" }; [pscustomobject]@{ Manufacturer = $cs.Manufacturer; Model = $cs.Model; SystemSKU = $cs.SystemSKUNumber; SerialNumber = $bios.SerialNumber; BiosVersion = $bios.SMBIOSBIOSVersion; HardwareHash = $hash } | ConvertTo-Json -Compress } catch { @{ error = $_.Exception.Message } | ConvertTo-Json -Compress }"
      ]
    }
  ],
  "diagnosticsPathPrefixes": [
    "C:\\ProgramData\\AutopilotMonitor",
    "C:\\ProgramData\\Microsoft\\IntuneManagementExtension\\Logs",
    "C:\\Windows\\Panther",
    "C:\\Windows\\Logs",
    "C:\\Windows\\SetupDiag",
    "C:\\Windows\\SoftwareDistribution\\ReportingEvents.log",
    "C:\\Windows\\System32\\winevt\\Logs",
    "C:\\Windows\\CCM\\Logs",
    "C:\\ProgramData\\Microsoft\\DiagnosticLogCSP",
    "C:\\ProgramData\\Microsoft\\Windows\\WER",
    "C:\\Windows\\Logs\\CBS",
    "C:\\Install\\Log"
  ],
  "blockedFilePrefixes": [
    "C:\\Users",
    "C:\\Windows\\System32\\config"
  ],
  "blockedCommandPatterns": [
    "Invoke-WebRequest",
    "Invoke-RestMethod",
    "Start-BitsTransfer",
    "wget",
    "curl",
    "certutil -urlcache",
    "New-LocalUser",
    "Add-LocalGroupMember",
    "net user",
    "net localgroup",
    "bcdedit",
    "bcdboot",
    "schtasks /create",
    "Register-ScheduledTask",
    "Remove-Item -Recurse",
    "Format-Volume",
    "Set-ExecutionPolicy"
  ],
  "maxCommandLength": 2000,
  "blockedInterimTriggerEventTypes": [
    "performance_snapshot",
    "agent_metrics_snapshot",
    "download_progress",
    "network_state_change",
    "network_connectivity_check",
    "log_entry",
    "agent_trace",
    "stall_probe_check"
  ]
} as const;
