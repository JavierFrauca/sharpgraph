// Preview standalone del webview: compila src/flowHtml.ts con esbuild, lo carga
// y vuelca un HTML con un JSON de endpoint_flow real (argv[2]) o uno sintético.
// Uso: node tools/preview.js [ruta-a-flow.json] [salida.html]
const path = require("path");
const fs = require("fs");
const esbuild = require("esbuild");

async function main() {
    const built = await esbuild.build({
        entryPoints: [path.join(__dirname, "..", "src", "flowHtml.ts")],
        bundle: true,
        format: "cjs",
        write: false,
        logLevel: "silent",
    });
    const mod = { exports: {} };
    new Function("module", "exports", "require", built.outputFiles[0].text)(mod, mod.exports, require);
    const { renderFlowHtml } = mod.exports;

    const jsonPath = process.argv[2];
    const outPath = process.argv[3] || path.join(__dirname, "..", "..", "design", "flow-preview.html");
    let flow;
    if (jsonPath && fs.existsSync(jsonPath)) {
        flow = JSON.parse(fs.readFileSync(jsonPath, "utf8"));
    } else {
        // sintético: réplica del caso Orders del prototipo (back-edge de IUnitOfWork)
        const n = (id, kind, name, i) => ({ id, kind, name, fqn: "X." + name, file: i ? `C:/demo/File${i}.cs` : null, line: 10 * i + 2, infra: !i });
        flow = {
            endpoint: "POST /api/orders",
            root: "N0",
            nodes: [
                n("N0", "endpoint", "POST /api/orders", 0),
                n("N1", "controller", "OrdersController", 1),
                n("N2", "command", "CreateOrderCommand", 2),
                n("N3", "handler", "CreateOrderCommandHandler", 3),
                n("N4", "validator", "CreateOrderCommandValidator", 4),
                n("N5", "interface", "IOrderRepository", 5),
                n("N6", "interface", "IEmailService", 6),
                n("N7", "implementation", "SqlOrderRepository", 7),
                n("N8", "implementation", "SmtpEmailService", 8),
                n("N9", "interface", "IUnitOfWork", 9),
                n("N10", "infra", "ILogger<T>", 0),
            ],
            edges: [
                { from: "N0", to: "N1", relation: "call", line: 58 },
                { from: "N1", to: "N2", relation: "sends", line: 59 },
                { from: "N2", to: "N3", relation: "handled-by", line: 3 },
                { from: "N3", to: "N4", relation: "call", line: 44 },
                { from: "N3", to: "N5", relation: "ctor-param", line: 39 },
                { from: "N3", to: "N6", relation: "ctor-param", line: 41 },
                { from: "N3", to: "N10", relation: "ctor-param", line: 42 },
                { from: "N5", to: "N7", relation: "di-bound", line: 77 },
                { from: "N6", to: "N8", relation: "di-bound", line: 78 },
                { from: "N7", to: "N9", relation: "ctor-param", line: 12 },
                { from: "N8", to: "N9", relation: "ctor-param", line: 20, back: true },
            ],
            mermaid: "```mermaid\nflowchart TD\n  N0 --> N1\n```\n",
        };
    }

    const html = renderFlowHtml(flow, { nonce: "preview-nonce", includeInfra: false });
    fs.writeFileSync(outPath, html);
    console.log("preview:", outPath);
}

main().catch((e) => {
    console.error(e);
    process.exit(1);
});
