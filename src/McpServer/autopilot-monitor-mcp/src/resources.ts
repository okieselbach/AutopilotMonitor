import { McpServer } from '@modelcontextprotocol/server';
import { EVENT_TYPES_CATALOG, DEVICE_PROPERTIES_CATALOG, getResourceContent } from './resource-catalog.js';
import { DIAG_ZIP_MAP } from './diag-zip-map.js';

/**
 * MCP-protocol resources. Note that some clients (e.g. Claude Code's HTTP-MCP
 * bridge in stateless mode) do not expose `resources/list` correctly — for
 * those clients, use the `get_resource(name)` tool which returns the same
 * data via a regular tool call.
 */
export function registerResources(server: McpServer): void {
  server.registerResource(
    'event_types',
    'autopilot://event-types',
    {
      title: 'Event Types Catalog',
      mimeType: 'application/json',
      description: 'Catalog of all known enrollment event type strings. Consult this before calling search_sessions_by_event to know valid eventType values.',
    },
    async () => ({
      contents: [
        {
          uri: 'autopilot://event-types',
          mimeType: 'application/json',
          text: JSON.stringify(EVENT_TYPES_CATALOG, null, 2),
        },
      ],
    })
  );

  server.registerResource(
    'device_properties',
    'autopilot://device-properties',
    {
      title: 'Device Properties Catalog',
      mimeType: 'application/json',
      description:
        'Catalog of known device property keys for the deviceProperties filter in search_sessions. ' +
        'Keys use "eventType.propertyName" dot notation. New agent properties are searchable immediately ' +
        'even before being added to this catalog — this list aids discoverability.',
    },
    async () => ({
      contents: [
        {
          uri: 'autopilot://device-properties',
          mimeType: 'application/json',
          text: JSON.stringify(DEVICE_PROPERTIES_CATALOG, null, 2),
        },
      ],
    })
  );

  server.registerResource(
    'diag_zip_layout',
    'autopilot://diag-zip-layout',
    {
      title: 'Diagnostics ZIP Layout',
      mimeType: 'application/json',
      description:
        'Expected file layout of an agent diagnostics ZIP. get_session_diagnostics returns a download ' +
        'URL for the archive plus this map so the client can extract + analyze it locally (the backend ' +
        'never unzips it). Read files in priority order; AppWorkload*.log can be hundreds of MB → grep only.',
    },
    async () => ({
      contents: [
        {
          uri: 'autopilot://diag-zip-layout',
          mimeType: 'application/json',
          text: JSON.stringify(DIAG_ZIP_MAP, null, 2),
        },
      ],
    })
  );

  server.registerResource(
    'rule_authoring_guide',
    'autopilot://rule-authoring-guide',
    {
      title: 'Rule Authoring Guide',
      mimeType: 'application/json',
      description:
        'Complete guide for authoring gather and analyze rules: collector types, triggers, ' +
        'condition sources, operators, confidence model, {{token}} interpolation, guardrail ' +
        'semantics and common pitfalls. Read together with rule_schemas and rule_guardrails; ' +
        'validate drafts with validate_rule and dry-run analyze rules with test_analyze_rule.',
    },
    async () => ({
      contents: [
        {
          uri: 'autopilot://rule-authoring-guide',
          mimeType: 'application/json',
          text: JSON.stringify(getResourceContent('rule_authoring_guide'), null, 2),
        },
      ],
    })
  );

  server.registerResource(
    'rule_schemas',
    'autopilot://rule-schemas',
    {
      title: 'Rule JSON Schemas',
      mimeType: 'application/json',
      description:
        'The JSON Schemas (2020-12) for gather rules and analyze rules — the exact validation ' +
        'contract (required fields, enums, patterns). Generated from rules/schema/ in the ' +
        'product repository.',
    },
    async () => ({
      contents: [
        {
          uri: 'autopilot://rule-schemas',
          mimeType: 'application/json',
          text: JSON.stringify(getResourceContent('rule_schemas'), null, 2),
        },
      ],
    })
  );

  server.registerResource(
    'ops_event_types',
    'autopilot://ops-event-types',
    {
      title: 'Ops Event Vocabularies',
      mimeType: 'application/json',
      description:
        'The three vocabularies get_ops_events filters by: OpsEvents categories (partition keys), ' +
        'severities in ladder order, and every ops event type the backend can write. Generated from ' +
        'the C# constants, so it never advertises a phantom type nor omits a real one.',
    },
    async () => ({
      contents: [
        {
          uri: 'autopilot://ops-event-types',
          mimeType: 'application/json',
          text: JSON.stringify(getResourceContent('ops_event_types'), null, 2),
        },
      ],
    })
  );

  server.registerResource(
    'rule_guardrails',
    'autopilot://rule-guardrails',
    {
      title: 'Gather Rule Guardrails',
      mimeType: 'application/json',
      description:
        'The collection allowlists enforced on-device for gather rules: registry/file/WMI/' +
        'diagnostics path prefixes, event log channels (plus hard-blocked channels) and the ' +
        'exact allowed command list. A gather rule whose target is not covered here will be ' +
        'blocked by the agent.',
    },
    async () => ({
      contents: [
        {
          uri: 'autopilot://rule-guardrails',
          mimeType: 'application/json',
          text: JSON.stringify(getResourceContent('rule_guardrails'), null, 2),
        },
      ],
    })
  );
}
