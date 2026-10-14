import { Fragment } from "react";
import type { RuleBlock, RuleInline } from "@/lib/ruleText";

/**
 * Renders a rule text parsed by `lib/ruleText` (paragraphs, lists, bold, emphasis, code). Values
 * arrive as text nodes, so nothing a device reported is ever interpreted as markup.
 */
export function RuleText({ blocks, className }: { blocks: RuleBlock[]; className?: string }) {
  if (blocks.length === 0) return null;
  return (
    <div className={className ?? "space-y-2 text-sm leading-relaxed text-gray-700"}>
      {blocks.map((block, i) => {
        if (block.kind === "paragraph") {
          return (
            <p key={i}>
              {block.lines.map((line, j) => (
                <Fragment key={j}>
                  {j > 0 && <br />}
                  <RuleInlines inlines={line} />
                </Fragment>
              ))}
            </p>
          );
        }
        const items = block.items.map((item, j) => (
          <li key={j}>
            <RuleInlines inlines={item} />
          </li>
        ));
        return block.ordered ? (
          <ol key={i} className="list-decimal space-y-1 pl-5">
            {items}
          </ol>
        ) : (
          <ul key={i} className="list-disc space-y-1 pl-5">
            {items}
          </ul>
        );
      })}
    </div>
  );
}

export function RuleInlines({ inlines }: { inlines: RuleInline[] }) {
  return (
    <>
      {inlines.map((node, i) => (
        <RuleInlineNode key={i} node={node} />
      ))}
    </>
  );
}

function RuleInlineNode({ node }: { node: RuleInline }) {
  switch (node.kind) {
    case "text":
      return <>{node.text}</>;
    case "strong":
      return (
        <strong className="font-semibold text-gray-900">
          <RuleInlines inlines={node.children} />
        </strong>
      );
    case "em":
      return (
        <em>
          <RuleInlines inlines={node.children} />
        </em>
      );
    case "code":
      return (
        <code className="rounded bg-gray-100 px-1 py-0.5 font-mono text-xs text-gray-800 break-words">
          <RuleInlines inlines={node.children} />
        </code>
      );
    case "token":
      return (
        <code className="rounded border border-dashed border-gray-300 px-1 font-mono text-xs text-gray-500" title="Filled in from each session">
          {node.token}
        </code>
      );
    case "value":
      return node.recorded ? (
        <>{node.text}</>
      ) : (
        <span className="text-gray-400" title="Not recorded for this session">
          {node.text}
        </span>
      );
  }
}
