declare module "*.png" {
  const url: string
  export default url
}

/**
 * The receiver source lives in the sender package but resolves zxing-wasm from
 * apps/web at build time. Keep the shared source type-checkable even when the
 * extension-only dependency tree intentionally does not install zxing-wasm.
 */
declare module "zxing-wasm/reader" {
  interface ReaderResult {
    bytes?: Uint8Array
  }

  export function getZXingModule(options?: {
    locateFile?: (file: string) => string
  }): Promise<unknown>

  export function readBarcodesFromImageData(
    image: ImageData,
    options?: Record<string, unknown>
  ): Promise<ReaderResult[]>
}
