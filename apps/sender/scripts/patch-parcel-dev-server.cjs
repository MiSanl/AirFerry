/**
 * Backport Parcel's dev-server CORS fix to the Parcel 2.9.3 line required by
 * Plasmo 0.90.5. Upgrading @parcel/core independently breaks Plasmo's private
 * core adapter, while leaving `Access-Control-Allow-Origin: *` lets arbitrary
 * websites read the developer's local extension build server.
 *
 * Same-origin browser access needs no CORS. Extension development is allowed
 * explicitly for chrome-extension:// and moz-extension:// origins only.
 * This touches generated node_modules files and therefore runs on postinstall.
 */
const fs = require("fs")
const path = require("path")

const senderRoot = path.resolve(__dirname, "..")
let packagePath
try {
  // npm may hoist this transitive dependency, while pnpm keeps it nested.
  packagePath = require.resolve("@parcel/reporter-dev-server/package.json", {
    paths: [senderRoot],
  })
} catch {
  throw new Error("Parcel dev-server reporter not found; Plasmo dependency layout changed")
}
const reporterRoot = path.dirname(packagePath)

const reporterVersion = JSON.parse(fs.readFileSync(packagePath, "utf8")).version
if (reporterVersion !== "2.9.3") {
  throw new Error(
    `Unsupported @parcel/reporter-dev-server ${reporterVersion}; review/remove the AirFerry backport`,
  )
}

function replaceExactly(file, before, after) {
  const original = fs.readFileSync(file, "utf8")
  if (original.includes(after)) return
  if (!original.includes(before)) {
    throw new Error(`Expected vulnerable Parcel code not found in ${file}`)
  }
  fs.writeFileSync(file, original.replace(before, after))
}

const sourceServer = path.join(reporterRoot, "src", "Server.js")
replaceExactly(
  sourceServer,
  `export function setHeaders(res: Response) {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader(
    'Access-Control-Allow-Methods',
    'GET, HEAD, PUT, PATCH, POST, DELETE',
  );
  res.setHeader(
    'Access-Control-Allow-Headers',
    'Origin, X-Requested-With, Content-Type, Accept, Content-Type',
  );
  res.setHeader('Cache-Control', 'max-age=0, must-revalidate');
}`,
  `export function setHeaders(req: Request, res: Response) {
  const origin = req.headers.origin;
  if (
    typeof origin === 'string' &&
    /^(chrome-extension|moz-extension):\\/\\/[A-Za-z0-9_-]+$/.test(origin)
  ) {
    res.setHeader('Access-Control-Allow-Origin', origin);
    res.setHeader('Vary', 'Origin');
    res.setHeader(
      'Access-Control-Allow-Methods',
      'GET, HEAD, PUT, PATCH, POST, DELETE',
    );
    res.setHeader(
      'Access-Control-Allow-Headers',
      'Origin, X-Requested-With, Content-Type, Accept, Content-Type',
    );
  }
  res.setHeader('Cache-Control', 'max-age=0, must-revalidate');
}`,
)
replaceExactly(sourceServer, "setHeaders(res);", "setHeaders(req, res);")

const sourceHmr = path.join(reporterRoot, "src", "HMRServer.js")
replaceExactly(sourceHmr, "setHeaders(res);", "setHeaders(req, res);")

const compiled = path.join(reporterRoot, "lib", "ServerReporter.js")
replaceExactly(
  compiled,
  `function $c2eeb2f2a04d6455$export$1c94a12dbc96ed70(res) {
    res.setHeader("Access-Control-Allow-Origin", "*");
    res.setHeader("Access-Control-Allow-Methods", "GET, HEAD, PUT, PATCH, POST, DELETE");
    res.setHeader("Access-Control-Allow-Headers", "Origin, X-Requested-With, Content-Type, Accept, Content-Type");
    res.setHeader("Cache-Control", "max-age=0, must-revalidate");
}`,
  `function $c2eeb2f2a04d6455$export$1c94a12dbc96ed70(req, res) {
    const origin = req.headers && req.headers.origin;
    if (typeof origin === "string" && /^(chrome-extension|moz-extension):\\/\\/[A-Za-z0-9_-]+$/.test(origin)) {
        res.setHeader("Access-Control-Allow-Origin", origin);
        res.setHeader("Vary", "Origin");
        res.setHeader("Access-Control-Allow-Methods", "GET, HEAD, PUT, PATCH, POST, DELETE");
        res.setHeader("Access-Control-Allow-Headers", "Origin, X-Requested-With, Content-Type, Accept, Content-Type");
    }
    res.setHeader("Cache-Control", "max-age=0, must-revalidate");
}`,
)
replaceExactly(
  compiled,
  "$c2eeb2f2a04d6455$export$1c94a12dbc96ed70(res);",
  "$c2eeb2f2a04d6455$export$1c94a12dbc96ed70(req, res);",
)
replaceExactly(
  compiled,
  "(0, $c2eeb2f2a04d6455$export$1c94a12dbc96ed70)(res);",
  "(0, $c2eeb2f2a04d6455$export$1c94a12dbc96ed70)(req, res);",
)

console.log("[patch-parcel-dev-server] restricted CORS to extension origins")
