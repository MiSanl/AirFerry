/**
 * Build all four extension targets sequentially.
 *
 * Outputs:
 *   build/chrome-mv3-prod/   — Chrome / Edge MV3  (uses wasm-pkg-simd)
 *   build/chrome-mv2-prod/   — Chrome / Edge MV2  (uses wasm-pkg-legacy)
 *   build/firefox-mv3-prod/  — Firefox MV3        (uses wasm-pkg-simd)
 *   build/firefox-mv2-prod/  — Firefox MV2        (uses wasm-pkg-legacy)
 *
 * ## Per-target WASM selection
 *
 * The two WASM variants are pre-built by scripts/build-wasm.cjs into
 * wasm-pkg-legacy/ (MV2, Chrome87-safe) and wasm-pkg-simd/ (the historically
 * named modern scalar variant, wasm-bindgen 0.2.125). The loader uses the
 * `@airferry-wasm` alias,
 * which Plasmo resolves to `wasm-pkg/`; before each build we copy the matching
 * variant there. This keeps application imports static while still shipping
 * per-target WASM.
 */
const { execSync } = require("child_process");
const fs = require("fs");
const path = require("path");
const { acquireWasmLock } = require("./wasm-lock.cjs");

const root = path.resolve(__dirname, "..");

const targets = [
  "chrome-mv3",
  "chrome-mv2",
  "firefox-mv3",
  "firefox-mv2",
];
const requested = process.argv.slice(2);
const selectedTargets = requested.length === 0 ? targets : requested;
for (const target of selectedTargets) {
  if (!targets.includes(target)) {
    console.error(`Unknown target: ${target}`);
    process.exit(2);
  }
}

function run(cmd) {
  console.log(`\n▶ ${cmd}`);
  execSync(cmd, { cwd: root, stdio: "inherit" });
}

/**
 * Copy wasm-pkg-<variant>/ over wasm-pkg/ so the next plasmo build bundles
 * the right WASM module. `variant` is the historical name "simd" for modern
 * MV3 targets, and "legacy" for MV2. Both modules are scalar. We wipe and
 * re-copy rather than symlink so Plasmo/Vite's file-watching
 * sees a real directory (some bundlers mis-handle symlinks in the build).
 */
function useWasmPkg(variant) {
  const src = path.resolve(root, `wasm-pkg-${variant}`);
  const dst = path.resolve(root, "wasm-pkg");
  if (!fs.existsSync(src)) {
    console.error(
      `\n✖ wasm-pkg-${variant}/ missing. Run \`npm run wasm\` (scripts/build-wasm.cjs) first.`
    );
    process.exit(1);
  }
  fs.rmSync(dst, { recursive: true, force: true });
  fs.cpSync(src, dst, { recursive: true });
  console.log(`   (wasm: ${variant})`);
}

// Hold the same lock used by build-wasm and web preparation for the entire
// consumer build. No publisher can replace a variant while Parcel is reading
// it, and no second target build can switch the shared wasm-pkg underneath us.
const releaseLock = acquireWasmLock(root);
try {
  // Build each target. MV3 → modern scalar variant (historically named
  // "simd"); MV2 → legacy scalar variant (Chrome87-safe).
  for (const target of selectedTargets) {
    const isMV3 = target.endsWith("mv3");
    useWasmPkg(isMV3 ? "simd" : "legacy");
    const outDir = `${target}-prod`;
    run(`plasmo build --target=${target}`);
    run(`node scripts/fix-manifest.cjs ${outDir}`);
    fs.copyFileSync(
      path.join(root, "node_modules/@foxglove/wasm-zstd/dist/wasm-zstd.wasm"),
      path.join(root, `build/${outDir}/wasm-zstd.wasm`)
    );
  }
} finally {
  releaseLock();
}

console.log("\n✅ All targets built:");
for (const target of selectedTargets) {
  const variant = target.endsWith("mv3") ? "simd" : "legacy";
  console.log(`   build/${target}-prod/  (wasm: ${variant})`);
}
