/**
 * Base64 of a byte array, built in 32 KB chunks: `String.fromCharCode(...bytes)` on a whole
 * MB-sized array overflows the argument stack, and byte-wise string concatenation takes
 * seconds at upload sizes.
 */
export function bytesToBase64(bytes: Uint8Array): string {
  let binary = "";
  for (let i = 0; i < bytes.length; i += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
  }
  return btoa(binary);
}
