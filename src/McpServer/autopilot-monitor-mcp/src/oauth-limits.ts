/**
 * Field-level bounds shared by both client-registration mechanisms — Dynamic
 * Client Registration (RFC 7591, oauth.ts) and Client ID Metadata Documents
 * (cimd.ts). One source so a document-registered client can never carry more
 * redirect URIs, or a longer one, than a dynamically registered client could.
 * Lives apart from oauth.ts so cimd.ts can import it without a cycle.
 */
export const MAX_REDIRECT_URIS_PER_CLIENT = 10;
export const MAX_REDIRECT_URI_LENGTH = 1024;
export const MAX_CLIENT_NAME_LENGTH = 256;
