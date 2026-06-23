import * as path from 'path';
import type { FormatRegistration, BridgeHandlerRegistration } from './formatRegistry';
import type { GameProfileRegistration } from './gameProfile';

export interface TkvscManifestContribution {
    id?: string;
    gameProfile?: GameProfileRegistration;
    formats?: FormatRegistration[];
    aampExtensions?: string[];
    archivePatterns?: string[];
    bridgeHandlers?: Array<{
        kind: string;
        modulePath: string;
        readFunction?: string;
        writeFunction?: string;
    }>;
}

export function parseTkvscContribution(raw: unknown): TkvscManifestContribution | undefined {
    if (!raw || typeof raw !== 'object') {
        return undefined;
    }
    return raw as TkvscManifestContribution;
}

export function contributionToBridgeHandlers(
    contribution: TkvscManifestContribution,
    extensionRoot: string,
): BridgeHandlerRegistration[] {
    const handlers: BridgeHandlerRegistration[] = [];
    for (const entry of contribution.bridgeHandlers ?? []) {
        if (!entry.kind || !entry.modulePath) {
            continue;
        }
        const resolved = path.isAbsolute(entry.modulePath)
            ? entry.modulePath
            : path.join(extensionRoot, entry.modulePath);
        handlers.push({
            kind: entry.kind,
            modulePath: resolved,
            readFunction: entry.readFunction,
            writeFunction: entry.writeFunction,
        });
    }
    return handlers;
}
