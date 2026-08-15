/**
 * Receiver-side payload parsing: detect text / bundle / single file and parse.
 *
 * Mirrors the Android `TextParser.kt` / `BundleParser.kt` and Windows
 * `TextParser.cs` / `BundleParser.cs` byte-level layouts. The sender builds
 * these in `apps/sender/src/wasm/text.ts` + `bundle.ts`; this is the inverse.
 */

import { isTextPayload, TEXT_MAGIC } from "@/wasm/text"
import { isBundle, BUNDLE_MAGIC, MAX_BUNDLE_FILES } from "@/wasm/bundle"

export type RecoveredKind = "text" | "bundle" | "file"

export interface RecoveredText {
  kind: "text"
  /** The decoded UTF-8 message (without the 8-byte magic). */
  text: string
  /** True if every byte was valid UTF-8 (strict decode, no replacement). */
  validUtf8: boolean
}

export interface RecoveredFile {
  kind: "file"
  name: string
  data: Uint8Array
}

export interface RecoveredBundle {
  kind: "bundle"
  entries: RecoveredFile[]
}

export type Recovered = RecoveredText | RecoveredFile | RecoveredBundle

export class ParseError extends Error {}

/** Read a big-endian u16 at `offset`. */
function readU16BE(bytes: Uint8Array, offset: number): number {
  return ((bytes[offset] << 8) | bytes[offset + 1]) >>> 0
}

/**
 * Read a big-endian u64 at `offset` as a JS number. Safe for values < 2^53
 * (the 32 MiB transfer cap keeps every size well within this).
 */
function readU64BE(bytes: Uint8Array, offset: number): number {
  let v = 0
  for (let i = 0; i < 8; i++) v = v * 256 + bytes[offset + i]
  return v
}

/**
 * Strict UTF-8 decode that rejects invalid sequences instead of producing
 * replacement characters. Mirrors the Android `decodeUtf8Strict`. Returns
 * `{ text, valid }`; when invalid, `text` is a best-effort decode and `valid`
 * is false so the caller can offer "save as file" instead of rendering.
 */
function decodeUtf8Strict(bytes: Uint8Array): { text: string; valid: boolean } {
  try {
    return { text: new TextDecoder("utf-8", { fatal: true }).decode(bytes), valid: true }
  } catch {
    return { text: new TextDecoder("utf-8").decode(bytes), valid: false }
  }
}

// Inline-text cap, mirrored from Android TextLike.MAX_TEXT_UI_BYTES and
// Windows FileNameUtil.MaxTextUiBytes (256 KiB). Oversized ETTEXT messages
// are handed to the UI as an ordinary .txt FILE (magic already stripped).
const MAX_TEXT_UI_BYTES = 256 * 1024

/**
 * Parse a recovered, decompressed payload into a text / bundle / file result.
 *
 * @param bytes the original (decompressed) payload bytes
 * @param descriptorName the filename from the descriptor
 */
export function parseRecovered(
  bytes: Uint8Array,
  descriptorName: string
): Recovered {
  // Text magic takes priority (a single text message).
  if (isTextPayload(bytes)) {
    const body = bytes.subarray(TEXT_MAGIC.length)
    if (body.byteLength > MAX_TEXT_UI_BYTES) {
      return { kind: "file", name: descriptorName || "文字消息.txt", data: body }
    }
    const { text, valid } = decodeUtf8Strict(body)
    return { kind: "text", text, validUtf8: valid }
  }

  // Bundle magic → multi-file unpack.
  if (isBundle(bytes)) {
    return parseBundle(bytes)
  }

  // Otherwise: ordinary single file under the descriptor filename.
  return { kind: "file", name: descriptorName, data: bytes }
}

/** Parse an ETBUNDL1 byte array into its file entries. */
export function parseBundle(bytes: Uint8Array): RecoveredBundle {
  // layout: [8 magic][2 version][2 count][entries...]
  // each entry: [2 name_len][name bytes][8 size][content]
  if (bytes.length < 12) {
    throw new ParseError("bundle 太短，无法解析")
  }
  let o = 8 // skip magic
  const version = readU16BE(bytes, o)
  o += 2
  if (version !== 1) {
    throw new ParseError(`不支持的 bundle 版本: ${version}`)
  }
  const count = readU16BE(bytes, o)
  o += 2
  if (count <= 0 || count > MAX_BUNDLE_FILES) {
    throw new ParseError(`bundle 文件数 ${count} 超出上限 ${MAX_BUNDLE_FILES}`)
  }
  const entries: RecoveredFile[] = []
  for (let i = 0; i < count; i++) {
    if (o + 2 > bytes.length) {
      throw new ParseError(`bundle 条目 ${i} 缺少 name_len`)
    }
    const nameLen = readU16BE(bytes, o)
    o += 2
    if (o + nameLen + 8 > bytes.length) {
      throw new ParseError(`bundle 条目 ${i} 头部越界`)
    }
    const name = new TextDecoder("utf-8", { fatal: false }).decode(
      bytes.subarray(o, o + nameLen)
    )
    o += nameLen
    const size = readU64BE(bytes, o)
    o += 8
    if (o + size > bytes.length) {
      throw new ParseError(
        `bundle 条目 ${i}（${name}）内容越界: 需要 ${size} 字节，剩 ${bytes.length - o}`
      )
    }
    // Keep views into one buffer; the receive worker transfers that buffer to
    // the UI once instead of cloning the entire bundle plus every member.
    const data = bytes.subarray(o, o + size)
    o += size
    entries.push({ kind: "file", name, data })
  }
  return { kind: "bundle", entries }
}

export { isTextPayload, isBundle, TEXT_MAGIC, BUNDLE_MAGIC }
