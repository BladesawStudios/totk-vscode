/**
 * Line scanner that mirrors `syntaxes/xlink.tmLanguage.json`, for the
 * viewport-only semantic highlighting in `xlnkSemanticTokens.ts`. It has no
 * `vscode` dependency so it can be exercised outside the editor.
 *
 * Like TextMate, each step takes the rule whose match starts earliest, with
 * ties going to the rule listed first. Keep the rules in grammar order.
 */

/** Token type names, also the semantic token legend (index = position). */
export const XLNK_TOKEN_TYPES = [
    'xlnkComment',
    'xlnkSection',
    'xlnkEntryName',
    'xlnkHash',
    'xlnkValueVar',
    'xlnkOperator',
    'xlnkWildcard',
    'xlnkProperty',
    'xlnkString',
    'xlnkEscape',
    'xlnkContainer',
    'xlnkSpecial',
    'xlnkScope',
    'xlnkConstant',
    'xlnkNumber',
] as const;

export type XlnkTokenType = (typeof XLNK_TOKEN_TYPES)[number];

export interface XlnkToken {
    start: number;
    length: number;
    type: XlnkTokenType;
}

interface Rule {
    regex: RegExp;
    /** Token type per capture group; index 0 is the whole match. */
    captures: (XlnkTokenType | undefined)[];
}

const rule = (source: string, ...captures: (XlnkTokenType | undefined)[]): Rule => ({
    regex: new RegExp(source, 'gd'),
    captures,
});

const RULES: Rule[] = [
    // comment
    rule('#.*$', 'xlnkComment'),
    // section
    rule(
        '\\b(?:Metadata|ParamDefines|SystemUserParams|CustomUserParams|SystemAssetParams|CustomAssetParams|TriggerParams|Users|UserParams|LocalProperties|ActionSlots|Properties|AlwaysTriggers|AssetCallTables|Cases|Children|Points|OverwriteParams)\\b(?=\\s*\\{)',
        'xlnkSection',
    ),
    // condition
    rule('<value>', 'xlnkValueVar'),
    rule('(?<=\\s)(?:==|!=|<=|>=|<|>)(?=\\s)', 'xlnkOperator'),
    rule('=>', 'xlnkOperator'),
    rule('\\(\\s*_\\s*\\)', 'xlnkWildcard'),
    // entry: Name[0xdeadbeef]
    rule(
        '("[^"]*"|[^\\s"=!<>(){}\\[\\]@,#:]+)(\\[)(0x[0-9a-fA-F]+)(\\])',
        undefined,
        'xlnkEntryName',
        'xlnkHash',
        'xlnkNumber',
        'xlnkHash',
    ),
    // assignment (only ever matches at column 0, as `^` does in TextMate)
    rule('^\\s*(@?[A-Za-z_][A-Za-z0-9_]*)\\s*(=)', undefined, 'xlnkProperty', 'xlnkOperator'),
    // value: strings are handled separately (see STRING_START)
    rule('"', undefined),
    rule('\\b(?:Asset|Blend|BlendBy|Sequence|Grid|Jump|Random|RandomNoRepeat|Switch)\\b', 'xlnkContainer'),
    rule(
        '\\b(?:CURVE|RANDOM|ARRANGE|Standard|Constant|Update|NoUpdate|Linear|InflectedPolynomial|IncreasingPolynomial|DecreasingPolynomial|FrameWindow|Always|OnLeave|Previous|ForceContinue|ELink|SLink|None|PriorityThenOldest|PriorityThenNewest|OldestThenPriority|NewestThenPriority|SpatialPriorityThenOldest|SpatialPriorityThenNewest|OldestThenSpatialPriority|NewestThenSpatialPriority)\\b',
        'xlnkSpecial',
    ),
    rule('\\b(Local|Global|ActionSlot)(::)', undefined, 'xlnkScope'),
    rule('\\b(?:true|false|null)\\b', 'xlnkConstant'),
    rule('\\b0x[0-9a-fA-F]+\\b', 'xlnkNumber'),
    rule('\\b0b[01]+\\b', 'xlnkNumber'),
    rule('(?<![\\w.])[-+]?[0-9]+\\.[0-9]*(?:e[-+]?[0-9]+)?\\b', 'xlnkNumber'),
    rule('(?<![\\w.])[-+]?[0-9]+\\b', 'xlnkNumber'),
];

const STRING_START = RULES.findIndex((r) => r.regex.source === '"');
const ESCAPE = /\\(?:u\{[0-9a-fA-F]+\}|.)/y;

/** Tokenizes one line; tokens come back sorted and non-overlapping. */
export function tokenizeXlnkLine(line: string): XlnkToken[] {
    const tokens: XlnkToken[] = [];
    const push = (start: number, end: number, type: XlnkTokenType) => {
        if (end > start) {
            tokens.push({ start, length: end - start, type });
        }
    };

    // Next match per rule, cached until the scan position passes it.
    const next: (RegExpExecArray | null | undefined)[] = new Array(RULES.length);
    let pos = 0;

    while (pos < line.length) {
        let best = -1;
        let bestMatch: RegExpExecArray | null = null;
        for (let i = 0; i < RULES.length; i++) {
            let match = next[i];
            if (match === undefined || (match !== null && match.index < pos)) {
                const regex = RULES[i].regex;
                regex.lastIndex = pos;
                match = regex.exec(line);
                next[i] = match;
            }
            if (match && (bestMatch === null || match.index < bestMatch.index)) {
                best = i;
                bestMatch = match;
            }
        }
        if (!bestMatch) {
            break;
        }

        if (best === STRING_START) {
            pos = scanString(line, bestMatch.index, push);
            continue;
        }

        const captures = RULES[best].captures;
        const indices = bestMatch.indices!;
        for (let group = 0; group < captures.length; group++) {
            const type = captures[group];
            const span = indices[group];
            if (type && span) {
                push(span[0], span[1], type);
            }
        }
        const end = bestMatch.index + bestMatch[0].length;
        // Guard against zero-length matches stalling the loop.
        pos = end > pos ? end : pos + 1;
    }

    return tokens;
}

/** Emits a string (split around escapes) starting at the opening quote; returns the end. */
function scanString(
    line: string,
    start: number,
    push: (start: number, end: number, type: XlnkTokenType) => void,
): number {
    let segmentStart = start;
    let pos = start + 1;
    while (pos < line.length) {
        const ch = line.charCodeAt(pos);
        if (ch === 0x22 /* " */) {
            push(segmentStart, pos + 1, 'xlnkString');
            return pos + 1;
        }
        if (ch === 0x5c /* \ */) {
            ESCAPE.lastIndex = pos;
            const escape = ESCAPE.exec(line);
            if (escape) {
                push(segmentStart, pos, 'xlnkString');
                push(pos, pos + escape[0].length, 'xlnkEscape');
                pos += escape[0].length;
                segmentStart = pos;
                continue;
            }
        }
        pos++;
    }
    // Unterminated: TextMate would carry on to the next line, but XLNK
    // output never splits a string, so stop at the end of this one.
    push(segmentStart, line.length, 'xlnkString');
    return line.length;
}
