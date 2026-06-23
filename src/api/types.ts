import type * as vscode from 'vscode';
import type { runBridgeJsonAsync } from '../bridge';
import type { FormatRegistration, BridgeHandlerRegistration } from '../formatRegistry';
import type { GameProfile, GameProfileRegistration } from '../gameProfile';
import type { ProjectAdapter } from '../projectAdapters/types';
import type { TKVSC_API_VERSION, TKVSC_ARCHIVE_CONTEXT, TKVSC_VIEWS } from './constants';
import type { TkvscReadyEmitter } from './readyEvent';

export type TkvscTreeItemLike = {
    contextValue?: string;
    resourceUri?: vscode.Uri;
};

export interface TkvscBridgeAccess {
    bridgePath: string;
    getPython(): string;
    getBridgeEnv(): NodeJS.ProcessEnv;
    runBridgeJsonAsync: typeof runBridgeJsonAsync;
}

/**
 * Public extension API returned from `activate()` for companion addon extensions.
 *
 * @see docs/api/v1.md - versioned reference (update when apiVersion changes)
 * @see docs/api/CHANGELOG.md
 */
export interface TkvscApi {
    readonly apiVersion: typeof TKVSC_API_VERSION;
    readonly extensionId: string;
    readonly views: typeof TKVSC_VIEWS;
    readonly contextValues: typeof TKVSC_ARCHIVE_CONTEXT;
    /** Fires after the projects archive tree is registered. Replays for late subscribers. */
    readonly onDidReady: vscode.Event<void>;
    /** @see docs/api/v1.md#resolveprojectrootitem */
    resolveProjectRoot(item: unknown): string | undefined;
    /** @see docs/api/v1.md#readrawbytesuri */
    readRawBytes(uri: vscode.Uri): Promise<Uint8Array>;
    /** @see docs/api/v1.md#writerawbytesuri-data */
    writeRawBytes(uri: vscode.Uri, data: Uint8Array): Promise<void>;
    /** @see docs/api/v1.md#getbridge */
    getBridge(): TkvscBridgeAccess;
    /** @see docs/api/v1.md#getprojectroots */
    getProjectRoots(): string[];
    /** @see docs/api/v1.md#registerformathandlerregistration */
    registerFormatHandler(registration: FormatRegistration): vscode.Disposable;
    /** @see docs/api/v1.md#registerbridgehandlerregistration */
    registerBridgeHandler(registration: BridgeHandlerRegistration): vscode.Disposable;
    /** @see docs/api/v1.md#registergameprofileregistration */
    registerGameProfile(registration: GameProfileRegistration): vscode.Disposable;
    /** @see docs/api/v1.md#getactivegameprofile */
    getActiveGameProfile(): GameProfile;
    /** @see docs/api/v1.md#getgameprofilegameid */
    getGameProfile(gameId: string): GameProfile | undefined;
    /** @see docs/api/v1.md#registerprojectadapteradapter */
    registerProjectAdapter(adapter: ProjectAdapter): vscode.Disposable;
    /** @see docs/api/v1.md#detectprojectadapterprojectrootpath */
    detectProjectAdapter(projectRootPath: string): ProjectAdapter;
    /** Async variant when {@link ProjectAdapter.isProjectRoot} returns a Promise. */
    detectProjectAdapterAsync(projectRootPath: string): Promise<ProjectAdapter>;
    /** @see docs/api/v1.md#getprojectadapters */
    getProjectAdapters(): ProjectAdapter[];
}

export interface CreateTkvscApiOptions {
    extensionId: string;
    bridgePath: string;
    getPython: () => string;
    getBridgeEnv: () => NodeJS.ProcessEnv;
    getProjectRoots: () => string[];
    onDidReadyEmitter: TkvscReadyEmitter;
    registerFormatHandler: (registration: FormatRegistration) => vscode.Disposable;
    registerBridgeHandler: (registration: BridgeHandlerRegistration) => vscode.Disposable;
    registerGameProfile: (registration: GameProfileRegistration) => vscode.Disposable;
    getActiveGameProfile: () => GameProfile;
    getGameProfile: (gameId: string) => GameProfile | undefined;
    registerProjectAdapter: (adapter: ProjectAdapter) => vscode.Disposable;
    detectProjectAdapter: (projectRootPath: string) => ProjectAdapter;
    detectProjectAdapterAsync: (projectRootPath: string) => Promise<ProjectAdapter>;
    getProjectAdapters: () => ProjectAdapter[];
}
