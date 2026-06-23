import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

export type GameProfileSource = 'builtin' | 'manifest' | 'api';

export interface GameIndexingConfig {
    enableRomfsSearch?: boolean;
    enableCanonicalPaths?: boolean;
    archiveExtensions?: string[];
}

export interface GameProfileRegistration {
    id: string;
    displayName: string;
    romfsSentinel: string;
    compressionBackend: string;
    /** Key under the `TKVSC` settings namespace, e.g. `romfsPath` or `splatoon3.romfsPath`. */
    romfsSettingsKey?: string;
    /** Additional settings keys checked before romfsSettingsKey (legacy aliases). */
    legacyRomfsSettingsKeys?: string[];
    indexing?: GameIndexingConfig;
    archivePatterns?: string[];
}

export interface GameProfile extends GameProfileRegistration {
    source: GameProfileSource;
}

const DEFAULT_TOTK_PROFILE: GameProfileRegistration = {
    id: 'totk',
    displayName: 'Tears of the Kingdom',
    romfsSentinel: 'Pack/ZsDic.pack.zs',
    compressionBackend: 'totk-zstd',
    romfsSettingsKey: 'romfsPath',
    indexing: {
        enableRomfsSearch: true,
        enableCanonicalPaths: true,
    },
};

const DEFAULT_ARCHIVE_EXTENSIONS = [
    '.pack',
    '.sarc',
    '.genvb',
    '.blarc',
    '.bfarc',
    '.bntx',
    '.pack.zs',
    '.sarc.zs',
    '.genvb.zs',
    '.blarc.zs',
    '.bfarc.zs',
    '.bntx.zs',
];

class GameProfileRegistry {
    private readonly profiles = new Map<string, GameProfile>();
    private initialized = false;

    initBuiltin(extensionPath: string): void {
        this.profiles.clear();
        const totkPath = path.join(extensionPath, 'config', 'games', 'totk.json');
        if (fs.existsSync(totkPath)) {
            const raw = JSON.parse(fs.readFileSync(totkPath, 'utf8')) as GameProfileRegistration;
            this.registerProfile(raw, 'builtin');
        } else {
            this.registerProfile(DEFAULT_TOTK_PROFILE, 'builtin');
        }
        this.initialized = true;
    }

    registerProfile(registration: GameProfileRegistration, source: GameProfileSource = 'api'): void {
        if (!registration.id) {
            return;
        }
        const indexing = {
            enableRomfsSearch: registration.indexing?.enableRomfsSearch ?? true,
            enableCanonicalPaths: registration.indexing?.enableCanonicalPaths ?? false,
            archiveExtensions:
                registration.indexing?.archiveExtensions?.length
                    ? [...registration.indexing.archiveExtensions]
                    : [...DEFAULT_ARCHIVE_EXTENSIONS],
        };
        this.profiles.set(registration.id, {
            ...registration,
            indexing,
            source,
        });
    }

    getProfile(gameId: string): GameProfile | undefined {
        return this.profiles.get(gameId);
    }

    getAllProfiles(): GameProfile[] {
        return [...this.profiles.values()];
    }

    getActiveGameId(): string {
        const configured = vscode.workspace
            .getConfiguration('TKVSC')
            .get<string>('activeGameId', 'totk')
            .trim();
        if (configured && this.profiles.has(configured)) {
            return configured;
        }
        return 'totk';
    }

    getActiveProfile(): GameProfile {
        const activeId = this.getActiveGameId();
        return this.profiles.get(activeId) ?? this.profiles.get('totk')!;
    }

    resolveRomfsPath(gameId?: string): string {
        const profile = gameId ? this.profiles.get(gameId) : this.getActiveProfile();
        if (!profile) {
            return '';
        }

        const config = vscode.workspace.getConfiguration('TKVSC');
        const keys = [
            profile.romfsSettingsKey,
            ...(profile.legacyRomfsSettingsKeys ?? []),
        ].filter((key): key is string => Boolean(key));

        for (const key of keys) {
            const value = config.get<string>(key, '').trim();
            if (value) {
                return path.normalize(value);
            }
        }

        const sentinelParts = profile.romfsSentinel.replace(/\\/g, '/').split('/');
        const sentinelFile = sentinelParts.pop() ?? '';
        const sentinelDir = sentinelParts.join(path.sep);

        for (const folder of vscode.workspace.workspaceFolders ?? []) {
            if (folder.uri.scheme !== 'file' && folder.uri.scheme !== 'sarc') {
                continue;
            }
            const candidateRoot = folder.uri.fsPath;
            const candidate = sentinelDir
                ? path.join(candidateRoot, sentinelDir, sentinelFile)
                : path.join(candidateRoot, sentinelFile);
            if (fs.existsSync(candidate)) {
                return path.normalize(candidateRoot);
            }
        }

        return '';
    }

    isValidRomfsPath(romfsPath: string, gameId?: string): boolean {
        const profile = gameId ? this.profiles.get(gameId) : this.getActiveProfile();
        if (!profile || !romfsPath) {
            return false;
        }
        const sentinel = profile.romfsSentinel.replace(/\//g, path.sep);
        return fs.existsSync(path.join(romfsPath, sentinel));
    }

    getArchiveExtensions(gameId?: string): string[] {
        const profile = gameId ? this.profiles.get(gameId) : this.getActiveProfile();
        return profile?.indexing?.archiveExtensions ?? [...DEFAULT_ARCHIVE_EXTENSIONS];
    }

    ensureInitialized(): void {
        if (!this.initialized) {
            throw new Error('GameProfileRegistry not initialized');
        }
    }
}

const registry = new GameProfileRegistry();

export function initGameProfileRegistry(extensionPath: string): void {
    registry.initBuiltin(extensionPath);
}

export function getGameProfileRegistry(): GameProfileRegistry {
    return registry;
}

export function registerGameProfile(registration: GameProfileRegistration): void {
    registry.registerProfile(registration, 'api');
}

export function getActiveGameProfile(): GameProfile {
    return registry.getActiveProfile();
}

export function getActiveGameId(): string {
    return registry.getActiveGameId();
}

export function resolveRomfsPathForGame(gameId?: string): string {
    return registry.resolveRomfsPath(gameId);
}

export function isRomfsPathValid(romfsPath: string, gameId?: string): boolean {
    return registry.isValidRomfsPath(romfsPath, gameId);
}

export function getRomfsSentinelPath(gameId?: string): string {
    const profile = gameId ? registry.getProfile(gameId) : registry.getActiveProfile();
    return profile?.romfsSentinel.replace(/\//g, path.sep) ?? path.join('Pack', 'ZsDic.pack.zs');
}
