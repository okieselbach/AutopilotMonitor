import React from "react";

export interface InlineMarkdownOptions {
  /**
   * Render `[label](https://…)` as links opening in a new tab. Off by default: rule
   * explanations never carried links, and their callers must stay pixel-identical.
   * Only http(s) targets become anchors; anything else renders as its label.
   */
  links?: boolean;
  linkClassName?: string;
  /** Render single-asterisk `*emphasis*` as <em>. Off by default for the same reason. */
  emphasis?: boolean;
}

const BOLD_OR_CODE = "\\*\\*[^*]+\\*\\*|`[^`]+`";
const LINK = "\\[[^\\]]+\\]\\([^)\\s]+\\)";
const EMPHASIS = "(?<!\\*)\\*[^*\\s][^*]*\\*(?!\\*)";

function tokenPattern(options: InlineMarkdownOptions): RegExp {
  const alternatives = [BOLD_OR_CODE];
  if (options.links) alternatives.push(LINK);
  if (options.emphasis) alternatives.push(EMPHASIS);
  return new RegExp(`(${alternatives.join("|")})`, "g");
}

/**
 * Converts basic inline markdown (**bold** and `code`, optionally [label](url) and
 * *emphasis*) to React elements. Returns an array of React nodes suitable for use as JSX children.
 */
export function formatInlineMarkdown(text: string, options: InlineMarkdownOptions = {}): React.ReactNode[] {
  // Split on the token patterns, keeping the delimiters as capture groups
  const parts = text.split(tokenPattern(options));

  return parts.map((part, i) => {
    if (part.startsWith("**") && part.endsWith("**")) {
      return (
        <strong key={i} className="font-semibold">
          {part.slice(2, -2)}
        </strong>
      );
    }
    if (part.startsWith("`") && part.endsWith("`")) {
      return (
        <code
          key={i}
          className="bg-gray-100 text-gray-800 px-1 py-0.5 rounded text-xs font-mono"
        >
          {part.slice(1, -1)}
        </code>
      );
    }
    if (options.links && part.startsWith("[")) {
      const m = /^\[([^\]]+)\]\(([^)\s]+)\)$/.exec(part);
      if (m) {
        const [, label, href] = m;
        if (!/^https?:\/\//i.test(href)) return label;
        return (
          <a key={i} href={href} target="_blank" rel="noopener noreferrer" className={options.linkClassName}>
            {label}
          </a>
        );
      }
    }
    if (options.emphasis && part.length > 2 && part.startsWith("*") && part.endsWith("*")) {
      return <em key={i}>{part.slice(1, -1)}</em>;
    }
    return part;
  });
}
