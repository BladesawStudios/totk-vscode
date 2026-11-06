import * as vscode from 'vscode';
import { stripCompressedSuffix } from './gameProfile';

const FONT_FILE_PATTERN = /\.(?:bfotf|bfttf|ttf|otf)$/i;

export function isFontFilePath(filePath: string): boolean {
    return FONT_FILE_PATTERN.test(stripCompressedSuffix(filePath.replace(/\\/g, '/')));
}

export function isTotkEncryptedFontTarget(filePath: string): boolean {
    return /\.(?:bfotf|bfttf)$/i.test(stripCompressedSuffix(filePath.replace(/\\/g, '/')));
}

export const FONT_IMPORT_FILTERS: Record<string, string[]> = {
    Fonts: ['ttf', 'otf', 'bfotf', 'bfttf'],
};
