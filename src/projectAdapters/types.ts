import type * as vscode from 'vscode';

/** Selected mod option within a project (TKMM: group + option folder). */
export interface ProjectOptionRef {
    group: string;
    option: string;
}

export type ProjectOptionPickResult = ProjectOptionRef | 'BASE_PROJECT' | 'BACK';

export interface ProjectAdapterContextValues {
    optionsRoot: string;
    optionGroup: string;
    option: string;
    optionActive: string;
}

/**
 * Pluggable mod-project layout (TKMM today; BCML / loose folders via addons later).
 *
 * @see docs/addon-development.md
 */
export interface ProjectAdapter {
    readonly id: string;
    readonly displayName: string;
    /** Tree `contextValue` strings for options hierarchy menus. */
    readonly contextValues: ProjectAdapterContextValues;
    /** Folder name that holds option groups (TKMM: `options`). */
    readonly optionsDirName: string;
    /** Marker file at project root (TKMM: `.tkproj`). */
    readonly projectMarkerFile?: string;

    isProjectRoot(projectRootPath: string): boolean | Promise<boolean>;

    supportsOptionsTree(projectRootPath: string): boolean | Promise<boolean>;

    /** Map a projects-tree item to the owning project root path. */
    resolveProjectRootFromTreeItem(
        contextValue: string | undefined,
        resourceFsPath: string,
    ): string | undefined;

    /** Project root for option-tree navigation from a tree element. */
    resolveOptionsProjectRoot(
        contextValue: string | undefined,
        resourceFsPath: string,
        workspaceRootPath: string,
        isArchiveRoot: boolean,
    ): string;

    getActiveOption(
        context: vscode.ExtensionContext,
        projectRoot: string,
    ): ProjectOptionRef | undefined;

    setActiveOption(
        context: vscode.ExtensionContext,
        projectRoot: string,
        group?: string,
        option?: string,
    ): Promise<void>;

    listOptionGroups(projectRoot: string): Promise<string[]>;

    listOptions(projectRoot: string, group: string): Promise<string[]>;

    createOptionGroup(projectRoot: string, groupName: string): Promise<void>;

    createOption(projectRoot: string, groupName: string, optionName: string): Promise<void>;

    askForOption(projectRoot: string): Promise<ProjectOptionPickResult | undefined>;

    /** Isolated mod roots for canonical import (project root + option folders). */
    listModRoots(projectRoot: string): string[];

    /** Import project folder paths from an external tool (e.g. TKMM recent.json). */
    importProjects?(): Promise<string[]>;

    /** User-facing label for import source (settings / error messages). */
    getImportSourceLabel?(): string;

    /** Create default files/folders for a new project at `projectFolderUri`. */
    scaffoldNewProject?(projectFolderUri: vscode.Uri): Promise<void>;
}
