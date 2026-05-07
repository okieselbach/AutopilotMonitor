import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { EVENT_TYPES_CATALOG, DEVICE_PROPERTIES_CATALOG } from './resource-catalog.js';

/**
 * MCP-protocol resources. Note that some clients (e.g. Claude Code's HTTP-MCP
 * bridge in stateless mode) do not expose `resources/list` correctly — for
 * those clients, use the `get_resource(name)` tool which returns the same
 * data via a regular tool call.
 */
export function registerResources(server: McpServer): void {
  server.resource(
    'event_types',
    'autopilot://event-types',
    {
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

  server.resource(
    'device_properties',
    'autopilot://device-properties',
    {
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
}
