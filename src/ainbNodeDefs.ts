import * as fs from 'fs';
import * as path from 'path';
import * as zlib from 'zlib';

/** One parameter of a node definition. `c` is only present for Pointer params. */
export interface AinbParamDef {
    n: string;
    t: string;
    c?: string;
}

export interface AinbNodeDef {
    name: string;
    type: string;
    cats?: string[];
    flow?: string[];
    in?: AinbParamDef[];
    out?: AinbParamDef[];
    props?: AinbParamDef[];
}

interface AinbNodeDefCatalog {
    version: number;
    source?: string;
    definitions: AinbNodeDef[];
}

let cached: AinbNodeDefCatalog | undefined;
let loadFailed = false;

/**
 * Node signatures harvested from every AINB in the game, so new nodes can be created
 * with the right parameters instead of empty shells. Generated from Starlight's
 * database by `scripts/convert_ainb_defs.py`; see the README credits.
 *
 * Roughly 1.8 MB once decompressed, so it is read once and kept for the session.
 */
export function loadAinbNodeDefs(extensionPath: string): AinbNodeDef[] {
    if (cached) {
        return cached.definitions;
    }
    if (loadFailed) {
        return [];
    }

    const file = path.join(extensionPath, 'config', 'ainbNodeDefs.json.gz');
    try {
        const raw = zlib.gunzipSync(fs.readFileSync(file)).toString('utf-8');
        const parsed = JSON.parse(raw) as AinbNodeDefCatalog;
        if (!parsed || !Array.isArray(parsed.definitions)) {
            throw new Error('Malformed node definition catalog');
        }
        cached = parsed;
        return cached.definitions;
    } catch {
        // The editor still works without definitions - only the node catalog and
        // definition-declared flow pins go away.
        loadFailed = true;
        return [];
    }
}
