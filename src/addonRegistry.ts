import * as path from 'path';
import * as vscode from 'vscode';
import {
    contributionToBridgeHandlers,
    parseTkvscContribution,
    type TkvscManifestContribution,
} from './addonManifest';
import {
    getFormatRegistry,
    type FormatRegistration,
    type BridgeHandlerRegistration,
} from './formatRegistry';
import { writeHandlerManifest } from './handlerManifest';

export function scanAddonManifests(context: vscode.ExtensionContext): void {
    const registry = getFormatRegistry();

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
    const registry = getFormatRegistry();

    for (const format of contribution.formats ?? []) {
        registry.registerFormat(format, source);
    }

    if (contribution.aampExtensions?.length) {
        registry.registerAampExtensions(contribution.aampExtensions, source);
    }

    for (const handler of contributionToBridgeHandlers(contribution, extensionRoot)) {
        registry.registerBridgeHandler(handler, source);
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

export function initAddonRegistries(
    context: vscode.ExtensionContext,
): void {
    initFormatRegistryOnly(context.extensionPath);
    scanAddonManifests(context);
    writeHandlerManifest(context.globalStorageUri.fsPath);

    context.subscriptions.push(
        vscode.workspace.onDidChangeConfiguration((event) => {
            if (event.affectsConfiguration('TKVSC.extraAampExtensions')) {
                writeHandlerManifest(context.globalStorageUri.fsPath);
            }
        }),
    );
}

export function initFormatRegistryOnly(extensionPath: string): void {
    getFormatRegistry().initBuiltin(extensionPath);
}

export function refreshHandlerManifest(context: vscode.ExtensionContext): void {
    writeHandlerManifest(context.globalStorageUri.fsPath);
}
