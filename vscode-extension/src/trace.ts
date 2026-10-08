/**
 * Parser de la salida de la tool trace_to_endpoints del motor:
 *   [exact-mediatr] [POST /api/payroll/calc] PayrollController.Calc ← ⇒CalcCommand ← Handler ← IGrossService
 *
 * De las hasta 15 cadenas que devuelve, se queda con la MÁS FIABLE: las
 * estructurales ([direct]/[nearby]/[exact-mediatr]) ganan a las heurísticas
 * ([heuristic], pivote por command/query compartido).
 */

export interface TracedEndpoint {
    label: string;
    verb: string;
    route: string;
    controller: string;
    method: string;
}

const LINE = /\[([^\]]+)\]\s*\[(GET|POST|PUT|DELETE|PATCH|HEAD|OPTIONS)\s+([^\]]+)\]\s+([\w.]+)\.(\w+)\s+←/g;

function reliability(label: string): number {
    return /exact|direct|nearby/i.test(label) ? 0 : 1;
}

export function parseTraceEndpoint(text: string): TracedEndpoint | undefined {
    let best: TracedEndpoint | undefined;
    let m: RegExpExecArray | null;
    LINE.lastIndex = 0;
    while ((m = LINE.exec(text)) !== null) {
        const hit: TracedEndpoint = { label: m[1], verb: m[2], route: m[3], controller: m[4], method: m[5] };
        if (!best || reliability(hit.label) < reliability(best.label)) {
            best = hit;
        }
    }
    return best;
}
