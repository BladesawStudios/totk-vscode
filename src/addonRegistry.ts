import * as vscode from 'vscode';
import {
    contributionToBridgeHandlers,
    mergeArchivePatternLists,
    parseTkvscContribution,
    type TkvscManifestContribution,
} from './addonManifest';
import { initArchiveRegistry } from './archiveRegistry';
import {
    getGameProfileRegistry,
    initGameProfileRegistry,
    registerGameProfile,
    type GameProfileRegistration,
} from './gameProfile';
import {
    getFormatRegistry,
    type FormatRegistration,
    type BridgeHandlerRegistration,
} from './formatRegistry';
import { writeHandlerManifest } from './handlerManifest';
import { writeAampHashNames } from './aampHashNames';
import { initProjectAdapterRegistry, registerProjectAdapter } from './projectAdapters/registry';
import type { ProjectAdapter } from './projectAdapters/types';

export function scanAddonManifests(context: vscode.ExtensionContext): void {
    for (const extension of vscode.extensions.all) {
        const contributes = extension.packageJSON.contributes as
            | { tkvsc?: unknown }
            | undefined;
        const contribution = parseTkvscContribution(contributes?.tkvsc);
        if (!contribution) {
            continue;
        }

        applyManifestContribution(contribution, extension.extensionPath, 'manifest');
    }
}

function applyManifestContribution(
    contribution: TkvscManifestContribution,
    extensionRoot: string,
    source: 'manifest' | 'api',
): void {
    const formatRegistry = getFormatRegistry();
    const registry = getGameProfileRegistry();

    if (contribution.gameProfile) {
        const profile: GameProfileRegistration = {
            ...contribution.gameProfile,
            id: contribution.gameProfile.id ?? contribution.id ?? contribution.gameProfile.id,
            archivePatterns: mergeArchivePatternLists(
                contribution.gameProfile.archivePatterns,
                contribution.archivePatterns,
            ),
        };
        if (profile.id) {
            registry.registerProfile(profile, source, { extensionRoot });
        }
    } else if (contribution.id && contribution.archivePatterns?.length) {
        const existing = registry.getProfile(contribution.id);
        if (existing) {
            registry.registerProfile(
                {
                    ...existing,
                    archivePatterns: mergeArchivePatternLists(
                        existing.archivePatterns,
                        contribution.archivePatterns,
                    ),
                },
                existing.source,
            );
        }
    }

    for (const format of contribution.formats ?? []) {
        formatRegistry.registerFormat(format, source);
    }

    if (contribution.aampExtensions?.length) {
        formatRegistry.registerAampExtensions(contribution.aampExtensions, source);
    }

    for (const handler of contributionToBridgeHandlers(contribution, extensionRoot)) {
        formatRegistry.registerBridgeHandler(handler, source);
    }
}

export function registerFormatHandler(
    context: vscode.ExtensionContext,
    registration: FormatRegistration,
): vscode.Disposable {
    getFormatRegistry().registerFormat(registration, 'api');
    writeHandlerManifest(context.globalStorageUri.fsPath);
    return new vscode.Disposable(() => {
        // Manifest merge does not track per-registration removal in v1.
    });
}

export function registerBridgeHandler(
    context: vscode.ExtensionContext,
    registration: BridgeHandlerRegistration,
): vscode.Disposable {
    getFormatRegistry().registerBridgeHandler(registration, 'api');
    writeHandlerManifest(context.globalStorageUri.fsPath);
    return new vscode.Disposable(() => {
        // See registerFormatHandler.
    });
}

export function registerGameProfileApi(
    context: vscode.ExtensionContext,
    registration: GameProfileRegistration,
    options?: { extensionRoot?: string },
): vscode.Disposable {
    registerGameProfile(registration, {
        extensionRoot: options?.extensionRoot,
        coreExtensionPath: context.extensionPath,
    });
    initArchiveRegistry();
    writeHandlerManifest(context.globalStorageUri.fsPath);
    return new vscode.Disposable(() => {
        // See registerFormatHandler.
    });
}

export function registerProjectAdapterApi(
    registration: ProjectAdapter,
): vscode.Disposable {
    registerProjectAdapter(registration);
    return new vscode.Disposable(() => {
        // Adapter removal not tracked in v1.
    });
}

export function refreshAddonManifests(context: vscode.ExtensionContext): void {
    scanAddonManifests(context);
    initArchiveRegistry();
    writeHandlerManifest(context.globalStorageUri.fsPath);
}

export function initAddonRegistries(
    context: vscode.ExtensionContext,
): void {
    initProjectAdapterRegistry();
    initGameProfileRegistry(context.extensionPath);
    initFormatRegistryOnly(context.extensionPath);
    refreshAddonManifests(context);
    writeAampHashNames(context.globalStorageUri.fsPath);

    context.subscriptions.push(
        vscode.extensions.onDidChange(() => {
            refreshAddonManifests(context);
        }),
        vscode.workspace.onDidChangeConfiguration((event) => {
            if (event.affectsConfiguration('TKVSC.extraAampExtensions')) {
                writeHandlerManifest(context.globalStorageUri.fsPath);
            }
            if (event.affectsConfiguration('TKVSC.aampHashNames')) {
                writeAampHashNames(context.globalStorageUri.fsPath);
            }
        }),
    );

    // Game addons may load after TKVSC's first scan (common in Extension Development Host).
    void Promise.resolve().then(() => refreshAddonManifests(context));
}

export function initFormatRegistryOnly(extensionPath: string): void {
    getFormatRegistry().initBuiltin(extensionPath);
}

export function refreshHandlerManifest(context: vscode.ExtensionContext): void {
    writeHandlerManifest(context.globalStorageUri.fsPath);
}
