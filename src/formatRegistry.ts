import * as fs from 'fs';
import * as path from 'path';
import * as vscode from 'vscode';

export type FormatSource = 'builtin' | 'manifest' | 'api';

export interface FormatDefinition {
    handler: string;
    language?: string;
    editable: boolean;
    source: FormatSource;
}

export interface FormatRegistration {
    extensions: string[];
    handler: string;
    language?: string;
    editable?: boolean;
}

export interface BridgeHandlerRegistration {
    kind: string;
    modulePath: string;
    readFunction?: string;
    writeFunction?: string;
}

export interface HandlerManifestJson {
    version: number;
    extensionToHandler: Record<string, string>;
    aampExtensions: string[];
    handlers: Record<
        string,
        {
            modulePath: string;
            readFunction: string;
            writeFunction: string;
        }
    >;
}

const LANGUAGE_BY_HANDLER: Record<string, string> = {
    byml: 'byml',
    msbt: 'msbt',
    asb: 'json',
    baev: 'json',
    xlnk: 'yaml',
};

const BUILTIN_HANDLERS = new Set(['byml', 'msbt', 'aamp', 'asb', 'baev', 'xlnk']);

function normalizeExtension(ext: string): string {
    return ext.toLowerCase().replace(/^\./, '');
}

export function fileExtensionFromPath(filePath: string): string {
    let lower = filePath.toLowerCase().replace(/\\/g, '/');
    if (lower.endsWith('.zs')) {
        lower = lower.slice(0, -3);
    }
    const dot = lower.lastIndexOf('.');
    return dot === -1 ? '' : lower.slice(dot + 1);
}

class FormatRegistry {
    private readonly extensionToFormat = new Map<string, FormatDefinition>();
    private readonly baseAampExtensions = new Set<string>();
    private readonly bridgeHandlers = new Map<string, BridgeHandlerRegistration>();
    private initialized = false;

    initBuiltin(extensionPath: string): void {
        const corePath = path.join(extensionPath, 'config', 'coreFsExtensions.json');
        const aampPath = path.join(extensionPath, 'config', 'aamp-extensions.json');
        const core = JSON.parse(fs.readFileSync(corePath, 'utf8')) as Record<string, string>;
        const aampList = JSON.parse(fs.readFileSync(aampPath, 'utf8')) as string[];

        this.extensionToFormat.clear();
        this.baseAampExtensions.clear();
        this.bridgeHandlers.clear();

        for (const [ext, handler] of Object.entries(core)) {
            this.registerFormat(
                {
                    extensions: [ext],
                    handler,
                    language: LANGUAGE_BY_HANDLER[handler] ?? undefined,
                    editable: true,
                },
                'builtin',
            );
        }

        for (const ext of aampList) {
            this.baseAampExtensions.add(normalizeExtension(ext));
        }

        this.initialized = true;
    }

    registerFormat(registration: FormatRegistration, source: FormatSource = 'api'): void {
        const editable = registration.editable ?? true;
        for (const ext of registration.extensions) {
            const normalized = normalizeExtension(ext);
            if (!normalized) {
                continue;
            }
            if (registration.handler === 'aamp') {
                this.baseAampExtensions.add(normalized);
                continue;
            }
            this.extensionToFormat.set(normalized, {
                handler: registration.handler,
                language: registration.language ?? LANGUAGE_BY_HANDLER[registration.handler],
                editable,
                source,
            });
        }
    }

    registerAampExtensions(extensions: string[], source: FormatSource = 'api'): void {
        for (const ext of extensions) {
            this.baseAampExtensions.add(normalizeExtension(ext));
        }
    }

    registerBridgeHandler(registration: BridgeHandlerRegistration, source: FormatSource = 'api'): void {
        this.bridgeHandlers.set(registration.kind, {
            ...registration,
            readFunction: registration.readFunction ?? 'read_content',
            writeFunction: registration.writeFunction ?? 'write_content',
        });
    }

    private getExtraAampFromSettings(): Set<string> {
        return new Set(
            vscode.workspace
                .getConfiguration('TKVSC')
                .get<string[]>('extraAampExtensions', [])
                .map((ext) => normalizeExtension(ext)),
        );
    }

    getAampExtensions(): Set<string> {
        return new Set([...this.baseAampExtensions, ...this.getExtraAampFromSettings()]);
    }

    isAampExtension(filePath: string): boolean {
        const ext = fileExtensionFromPath(filePath);
        return ext !== '' && this.getAampExtensions().has(ext);
    }

    getHandlerType(filePath: string): string | undefined {
        if (this.isAampExtension(filePath)) {
            return 'aamp';
        }
        const ext = fileExtensionFromPath(filePath);
        return ext ? this.extensionToFormat.get(ext)?.handler : undefined;
    }

    getLanguageId(filePath: string): string | undefined {
        if (this.isAampExtension(filePath)) {
            return 'yaml';
        }
        const ext = fileExtensionFromPath(filePath);
        return ext ? this.extensionToFormat.get(ext)?.language : undefined;
    }

    isEditable(filePath: string): boolean {
        if (this.isAampExtension(filePath)) {
            return true;
        }
        const ext = fileExtensionFromPath(filePath);
        const def = ext ? this.extensionToFormat.get(ext) : undefined;
        return def?.editable ?? false;
    }

    isCoreExtension(filePath: string): boolean {
        const handler = this.getHandlerType(filePath);
        return handler !== undefined && handler !== 'aamp';
    }

    getCoreExtensionMap(): Record<string, string> {
        const map: Record<string, string> = {};
        for (const [ext, def] of this.extensionToFormat.entries()) {
            map[ext] = def.handler;
        }
        return map;
    }

    isBuiltinHandler(kind: string): boolean {
        return BUILTIN_HANDLERS.has(kind);
    }

    getBridgeHandler(kind: string): BridgeHandlerRegistration | undefined {
        return this.bridgeHandlers.get(kind);
    }

    buildHandlerManifest(): HandlerManifestJson {
        const extensionToHandler: Record<string, string> = {};
        for (const [ext, def] of this.extensionToFormat.entries()) {
            extensionToHandler[ext] = def.handler;
        }

        const handlers: HandlerManifestJson['handlers'] = {};
        for (const [kind, reg] of this.bridgeHandlers.entries()) {
            if (this.isBuiltinHandler(kind)) {
                continue;
            }
            handlers[kind] = {
                modulePath: reg.modulePath,
                readFunction: reg.readFunction ?? 'read_content',
                writeFunction: reg.writeFunction ?? 'write_content',
            };
        }

        return {
            version: 1,
            extensionToHandler,
            aampExtensions: [...this.getAampExtensions()].sort(),
            handlers,
        };
    }

    ensureInitialized(): void {
        if (!this.initialized) {
            throw new Error('FormatRegistry not initialized');
        }
    }
}

const registry = new FormatRegistry();

export function initFormatRegistry(extensionPath: string): void {
    registry.initBuiltin(extensionPath);
}

export function getFormatRegistry(): FormatRegistry {
    return registry;
}
