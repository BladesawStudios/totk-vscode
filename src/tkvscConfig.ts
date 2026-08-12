import * as fs from 'fs';
import * as path from 'path';
import { logger } from './logger';

export const DEFAULT_PROJECT_GAME_ID = 'totk';

export interface TkvscConfig {
    gameId?: string;
    canonicalSyncBlacklistPrefixes?: string[];
    canonicalSyncFileExtensionBlacklist?: string[];
}

export function getTkvscConfigPath(projectRoot: string): string {
    return path.join(projectRoot, '.tkvsc');
}

export function projectRootExists(projectRoot: string): boolean {
    try {
        return fs.statSync(projectRoot).isDirectory();
    } catch {
        return false;
    }
}

export function readTkvscConfig(projectRoot: string): TkvscConfig {
    try {
        const configPath = getTkvscConfigPath(projectRoot);
        if (fs.existsSync(configPath)) {
            const raw = fs.readFileSync(configPath, 'utf8');
            return JSON.parse(raw) as TkvscConfig;
        }
    } catch (e) {
        logger.error('Failed to parse existing .tkvsc:', e as Error);
    }
    return {};
}

export function writeTkvscConfig(projectRoot: string, data: TkvscConfig): void {
    const configPath = getTkvscConfigPath(projectRoot);
    fs.writeFileSync(configPath, JSON.stringify(data, null, 2), 'utf8');
}

/** Returns the project's gameId, defaulting to TotK when missing. */
export function getProjectGameId(projectRoot: string): string {
    const configured = readTkvscConfig(projectRoot).gameId?.trim();
    return configured || DEFAULT_PROJECT_GAME_ID;
}

/**
 * Ensure `.tkvsc` has a `gameId`. Does not overwrite an existing value.
 * Creates the file when needed.
 *
 * Best effort: a project folder that is gone (or read-only) is skipped instead of
 * throwing, so a stale stored project cannot break extension startup.
 */
export function ensureProjectGameId(projectRoot: string, gameIdIfMissing: string): void {
    if (!projectRootExists(projectRoot)) {
        logger.debug(`Skipping .tkvsc gameId write, project folder is missing: ${projectRoot}`);
        return;
    }
    const config = readTkvscConfig(projectRoot);
    if (config.gameId?.trim()) {
        return;
    }
    config.gameId = gameIdIfMissing;
    try {
        writeTkvscConfig(projectRoot, config);
    } catch (e) {
        logger.debug(`Failed to write .tkvsc for ${projectRoot}: ${(e as Error).message}`);
    }
}
