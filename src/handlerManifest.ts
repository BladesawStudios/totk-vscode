import * as fs from 'fs';
import * as path from 'path';
import { getFormatRegistry, type HandlerManifestJson } from './formatRegistry';

const MANIFEST_FILE_NAME = 'tkvsc-handler-manifest.json';

let manifestPath: string | undefined;

export function setHandlerManifestPath(globalStorageFsPath: string): void {
    manifestPath = path.join(globalStorageFsPath, MANIFEST_FILE_NAME);
}

export function getHandlerManifestPath(): string | undefined {
    return manifestPath;
}

export function writeHandlerManifest(globalStorageFsPath: string): string {
    const target = path.join(globalStorageFsPath, MANIFEST_FILE_NAME);
    manifestPath = target;
    const manifest = getFormatRegistry().buildHandlerManifest();
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, JSON.stringify(manifest, null, 2), 'utf8');
    return target;
}

export function readHandlerManifest(globalStorageFsPath: string): HandlerManifestJson {
    const target = path.join(globalStorageFsPath, MANIFEST_FILE_NAME);
    if (!fs.existsSync(target)) {
        return getFormatRegistry().buildHandlerManifest();
    }
    return JSON.parse(fs.readFileSync(target, 'utf8')) as HandlerManifestJson;
}
