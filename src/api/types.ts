import type * as vscode from 'vscode';
import type { runBridgeJsonAsync } from '../bridge';
import type { FormatRegistration, BridgeHandlerRegistration } from '../formatRegistry';
import type { TKVSC_API_VERSION, TKVSC_ARCHIVE_CONTEXT, TKVSC_VIEWS } from './constants';

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
 * @see docs/api/v1.md — versioned reference (update when apiVersion changes)
 * @see docs/api/CHANGELOG.md
 */
export interface TkvscApi {
    readonly apiVersion: typeof TKVSC_API_VERSION;
    readonly extensionId: string;
    readonly views: typeof TKVSC_VIEWS;
    readonly contextValues: typeof TKVSC_ARCHIVE_CONTEXT;
    /** Fires after the projects archive tree is registered at startup. */
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
}

export interface CreateTkvscApiOptions {
    extensionId: string;
    bridgePath: string;
    getPython: () => string;
    getBridgeEnv: () => NodeJS.ProcessEnv;
    getProjectRoots: () => string[];
    onDidReadyEmitter: vscode.EventEmitter<void>;
    registerFormatHandler: (registration: FormatRegistration) => vscode.Disposable;
    registerBridgeHandler: (registration: BridgeHandlerRegistration) => vscode.Disposable;
}
