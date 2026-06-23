import type * as vscode from 'vscode';
import { getBuiltinTkmmAdapter } from './tkmmAdapter';
import type { ProjectAdapter } from './types';

class ProjectAdapterRegistry {
    private readonly adapters = new Map<string, ProjectAdapter>();
    private initialized = false;

    initBuiltin(): void {
        this.adapters.clear();
        this.register(getBuiltinTkmmAdapter());
        this.initialized = true;
    }

    register(adapter: ProjectAdapter): void {
        this.adapters.set(adapter.id, adapter);
    }

    get(id: string): ProjectAdapter | undefined {
        return this.adapters.get(id);
    }

    getAll(): ProjectAdapter[] {
        return [...this.adapters.values()];
    }

    getDefault(): ProjectAdapter {
        return getBuiltinTkmmAdapter();
    }

    async detectForRoot(projectRootPath: string): Promise<ProjectAdapter> {
        for (const adapter of this.adapters.values()) {
            if (await adapter.isProjectRoot(projectRootPath)) {
                return adapter;
            }
        }
        return this.getDefault();
    }

    detectForRootSync(projectRootPath: string): ProjectAdapter {
        for (const adapter of this.adapters.values()) {
            const result = adapter.isProjectRoot(projectRootPath);
            if (typeof result === 'boolean' && result) {
                return adapter;
            }
        }
        return this.getDefault();
    }

    async importAllProjects(): Promise<Array<{ adapter: ProjectAdapter; paths: string[] }>> {
        const results: Array<{ adapter: ProjectAdapter; paths: string[] }> = [];
        for (const adapter of this.adapters.values()) {
            if (!adapter.importProjects) {
                continue;
            }
            const paths = await adapter.importProjects();
            if (paths.length > 0) {
                results.push({ adapter, paths });
            }
        }
        return results;
    }

    resolveProjectRootFromTreeItem(
        contextValue: string | undefined,
        resourceFsPath: string,
    ): string | undefined {
        for (const adapter of this.adapters.values()) {
            const resolved = adapter.resolveProjectRootFromTreeItem(contextValue, resourceFsPath);
            if (resolved) {
                return resolved;
            }
        }
        return undefined;
    }

    ensureInitialized(): void {
        if (!this.initialized) {
            throw new Error('ProjectAdapterRegistry not initialized');
        }
    }
}

const registry = new ProjectAdapterRegistry();

export function isAdapterOptionsContextValue(contextValue: string | undefined): boolean {
    if (!contextValue) {
        return false;
    }
    for (const adapter of registry.getAll()) {
        const values = adapter.contextValues;
        if (
            contextValue === values.optionsRoot
            || contextValue === values.optionGroup
            || contextValue === values.option
            || contextValue === values.optionActive
        ) {
            return true;
        }
    }
    return false;
}

export function isAdapterOptionFolderContextValue(contextValue: string | undefined): boolean {
    if (!contextValue) {
        return false;
    }
    for (const adapter of registry.getAll()) {
        const values = adapter.contextValues;
        if (contextValue === values.optionGroup || contextValue === values.option) {
            return true;
        }
    }
    return false;
}

export function initProjectAdapterRegistry(): void {
    registry.initBuiltin();
}

export function registerProjectAdapter(adapter: ProjectAdapter): void {
    registry.register(adapter);
}

export function getProjectAdapter(id: string): ProjectAdapter | undefined {
    return registry.get(id);
}

export function getProjectAdapters(): ProjectAdapter[] {
    return registry.getAll();
}

export function detectProjectAdapter(projectRootPath: string): ProjectAdapter {
    return registry.detectForRootSync(projectRootPath);
}

export async function detectProjectAdapterAsync(
    projectRootPath: string,
): Promise<ProjectAdapter> {
    return registry.detectForRoot(projectRootPath);
}

export async function importProjectsFromAdapters(): Promise<
    Array<{ adapter: ProjectAdapter; paths: string[] }>
> {
    return registry.importAllProjects();
}

export function resolveProjectRootFromTreeItem(
    contextValue: string | undefined,
    resourceFsPath: string,
): string | undefined {
    return registry.resolveProjectRootFromTreeItem(contextValue, resourceFsPath);
}

export function getActiveProjectOption(
    context: vscode.ExtensionContext,
    projectRoot: string,
): ReturnType<ProjectAdapter['getActiveOption']> {
    const adapter = detectProjectAdapter(projectRoot);
    return adapter.getActiveOption(context, projectRoot);
}

export async function askForProjectOption(
    projectRoot: string,
): Promise<ReturnType<ProjectAdapter['askForOption']>> {
    const adapter = await detectProjectAdapterAsync(projectRoot);
    return adapter.askForOption(projectRoot);
}

export function listProjectModRoots(projectRoot: string): string[] {
    const adapter = detectProjectAdapter(projectRoot);
    return adapter.listModRoots(projectRoot);
}
