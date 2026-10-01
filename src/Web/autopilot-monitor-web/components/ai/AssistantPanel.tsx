import {
  FILE_ACCESS_SKINS,
  type Block,
  type Question,
  type Skin,
  type Span,
  type Tone,
  type ToolCall,
} from "./questions";

/**
 * One answer from the /ai gallery, drawn as a chat app, VS Code, a terminal agent or a self-hosted
 * front end. Same content in every skin; only chrome and type change. Colors are lp-* tokens or
 * arbitrary values: the app-wide `.dark` overrides would otherwise flip palette classes.
 */

const TONE_LIGHT: Record<Tone, string> = {
  ok: "text-[var(--lp-accent-ink)]",
  warn: "text-[var(--lp-warn-ink)]",
  high: "text-[var(--lp-danger)]",
  dim: "text-[var(--lp-ink-soft)]",
};
const TONE_DARK: Record<Tone, string> = {
  ok: "text-[var(--lp-term-ok)]",
  warn: "text-[var(--lp-term-warn)]",
  high: "text-[var(--lp-term-high)]",
  dim: "text-[var(--lp-term-faint)]",
};

interface SkinStyle {
  dark: boolean;
  frame: string;
  head: string;
  body: string;
  user: string;
  tools: string;
  tool: string;
  who: string | null;
  input: string;
  code: string;
  list: string;
  th: string;
  td: string;
  ticket: string;
  ticketText: string;
  note: string;
}

const STYLES: Record<Skin, SkinStyle> = {
  chat: {
    dark: false,
    frame: "grid-cols-1 border-[var(--lp-line)] bg-[var(--lp-surface)]",
    head: "border-b border-[var(--lp-line-soft)] bg-[var(--lp-surface-2)]",
    body: "gap-3.5 text-[14.5px] leading-[1.6] text-[var(--lp-ink)]",
    user: "self-end max-w-[82%] rounded-[18px] rounded-br-md bg-[var(--lp-surface-2)] px-3.5 py-2.5",
    tools: "flex flex-wrap gap-1.5",
    tool: "inline-flex items-center gap-1.5 rounded-full bg-[var(--lp-accent-soft)] px-2.5 py-1 font-mono text-[11.5px] text-[var(--lp-accent-ink)]",
    who: "Assistant",
    input: "rounded-[14px] border border-[var(--lp-line)] bg-[var(--lp-surface)] px-3.5 py-2.5 text-sm text-[var(--lp-ink-faint)]",
    code: "rounded-[5px] bg-[var(--lp-surface-2)] px-[5px] py-px font-mono text-[0.88em]",
    list: "list-disc pl-5",
    th: "border-b border-[var(--lp-line-soft)] py-1.5 pr-4 text-[11.5px] font-semibold text-[var(--lp-ink-faint)]",
    td: "border-b border-[var(--lp-line-soft)] py-1.5 pr-4",
    ticket: "rounded-xl border border-[var(--lp-line)] bg-[var(--lp-bg)] px-3.5 py-3 text-[13.5px]",
    ticketText: "text-[var(--lp-ink-soft)]",
    note: "inline-flex items-center rounded-full border border-[var(--lp-line)] px-2.5 py-1 font-sans text-xs text-[var(--lp-ink-soft)]",
  },
  vscode: {
    dark: true,
    frame: "grid-cols-[44px_minmax(0,1fr)] border-[var(--lp-term-line)] bg-[var(--lp-term-bg)]",
    head: "border-b border-[var(--lp-term-line)] text-[var(--lp-term-faint)]",
    body: "gap-3.5 text-[13.5px] leading-[1.6] text-[var(--lp-term-ink)]",
    user: "rounded-lg border border-[var(--lp-term-line)] bg-[rgba(255,255,255,0.04)] px-3.5 py-2.5",
    tools: "flex flex-col gap-[3px]",
    tool: "text-xs text-[var(--lp-term-faint)]",
    who: "Copilot",
    input: "rounded-lg border border-[var(--lp-term-line)] bg-[rgba(255,255,255,0.04)] px-3.5 py-2.5 text-sm text-[var(--lp-term-faint)]",
    code: "rounded-[5px] bg-[rgba(255,255,255,0.07)] px-[5px] py-px font-mono text-[0.88em]",
    list: "list-disc pl-5",
    th: "border-b border-[var(--lp-term-line)] py-1.5 pr-4 text-[11.5px] font-semibold text-[var(--lp-term-faint)]",
    td: "border-b border-[var(--lp-term-line)] py-1.5 pr-4",
    ticket: "rounded-xl border border-[var(--lp-term-line)] bg-[rgba(255,255,255,0.03)] px-3.5 py-3 text-[13.5px]",
    ticketText: "text-[var(--lp-term-faint)]",
    note: "inline-flex items-center rounded-full border border-[var(--lp-term-line)] px-2.5 py-1 font-sans text-xs text-[var(--lp-term-faint)]",
  },
  terminal: {
    dark: true,
    frame: "grid-cols-1 border-[var(--lp-term-line)] bg-[var(--lp-term-bg)]",
    head: "border-b border-[var(--lp-term-line)]",
    body: "gap-2.5 font-mono text-[12.5px] leading-[1.7] text-[var(--lp-term-ink)]",
    user: "",
    tools: "flex flex-col pl-4",
    tool: "break-all text-[var(--lp-term-faint)]",
    who: null,
    input: "font-mono text-[12.5px] text-[var(--lp-term-faint)]",
    code: "",
    list: "list-none",
    th: "pr-[18px] font-normal text-[var(--lp-term-faint)]",
    td: "pr-[18px]",
    ticket: "rounded-xl border border-[var(--lp-term-line)] bg-[rgba(255,255,255,0.03)] px-3.5 py-3",
    ticketText: "text-[var(--lp-term-faint)]",
    note: "inline-flex items-center rounded-full border border-[var(--lp-term-line)] px-2.5 py-1 font-sans text-xs text-[var(--lp-term-faint)]",
  },
  selfhosted: {
    dark: false,
    frame: "grid-cols-1 border-[var(--lp-line)] bg-[var(--lp-surface)]",
    head: "border-b border-[var(--lp-line-soft)] bg-[var(--lp-surface)]",
    body: "gap-3.5 text-[14.5px] leading-[1.6] text-[var(--lp-ink)]",
    user: "self-end max-w-[82%] rounded-[18px] rounded-br-md bg-[var(--lp-accent-soft)] px-3.5 py-2.5 shadow-[inset_0_0_0_1px_var(--lp-accent-line)]",
    tools: "flex flex-wrap gap-1.5",
    tool: "inline-flex items-center gap-1.5 rounded-full bg-[var(--lp-surface-2)] px-2.5 py-1 font-mono text-[11.5px] text-[var(--lp-ink-soft)]",
    who: "Assistant · your model",
    input: "rounded-[14px] border border-[var(--lp-line)] bg-[var(--lp-surface)] px-3.5 py-2.5 text-sm text-[var(--lp-ink-faint)]",
    code: "rounded-[5px] bg-[var(--lp-surface-2)] px-[5px] py-px font-mono text-[0.88em]",
    list: "list-disc pl-5",
    th: "border-b border-[var(--lp-line-soft)] py-1.5 pr-4 text-[11.5px] font-semibold text-[var(--lp-ink-faint)]",
    td: "border-b border-[var(--lp-line-soft)] py-1.5 pr-4",
    ticket: "rounded-xl border border-[var(--lp-line)] bg-[var(--lp-bg)] px-3.5 py-3 text-[13.5px]",
    ticketText: "text-[var(--lp-ink-soft)]",
    note: "inline-flex items-center rounded-full border border-[var(--lp-line)] px-2.5 py-1 font-sans text-xs text-[var(--lp-ink-soft)]",
  },
};

const BAR_LIGHT = { base: "bg-[var(--lp-accent)]", warn: "bg-[var(--lp-warn)]", dim: "bg-[var(--lp-line)]" };
const BAR_DARK = { base: "bg-[var(--lp-accent)]", warn: "bg-[var(--lp-term-warn)]", dim: "bg-[var(--lp-term-line)]" };

function toneClass(tone: Tone, skin: Skin): string {
  return (STYLES[skin].dark ? TONE_DARK : TONE_LIGHT)[tone];
}

function SpanView({ span, skin }: { span: Span; skin: Skin }) {
  if (typeof span === "string") return <>{span}</>;
  const content = span.code ? <code className={STYLES[skin].code}>{span.text}</code> : span.text;
  const classes = [span.tone ? toneClass(span.tone, skin) : "", span.bold ? "font-semibold" : ""].filter(Boolean).join(" ");
  return classes ? <span className={classes}>{content}</span> : <>{content}</>;
}

function Spans({ spans, skin }: { spans: Span[]; skin: Skin }) {
  return (
    <>
      {spans.map((span, i) => (
        <SpanView key={i} span={span} skin={skin} />
      ))}
    </>
  );
}

function TableView({ block, skin }: { block: Extract<Block, { kind: "table" }>; skin: Skin }) {
  const style = STYLES[skin];
  const numeric = new Set(block.numeric ?? []);
  const hasBar = block.rows.some(row => row.bar);
  const columns = (block.head?.length ?? Math.max(...block.rows.map(row => row.cells.length))) + (hasBar ? 1 : 0);
  const bars = style.dark ? BAR_DARK : BAR_LIGHT;
  const align = (i: number) => (numeric.has(i) ? "text-right tabular-nums" : "text-left");
  return (
    <div className="overflow-x-auto">
      <table className="border-collapse [&_tbody_tr:last-child>td]:border-b-0">
        {block.head && (
          <thead>
            <tr>
              {block.head.map((heading, i) => (
                <th key={heading} className={`${style.th} ${align(i)}`}>
                  {heading}
                </th>
              ))}
              {hasBar && <th className={style.th} />}
            </tr>
          </thead>
        )}
        <tbody>
          {block.rows.map((row, ri) => (
            <tr key={ri}>
              {row.fullWidth ? (
                <td colSpan={columns} className={style.td}>
                  <Spans spans={row.cells} skin={skin} />
                </td>
              ) : (
                <>
                  {row.cells.map((cell, ci) => (
                    <td key={ci} className={`${style.td} whitespace-nowrap ${align(ci)}`}>
                      <SpanView span={cell} skin={skin} />
                    </td>
                  ))}
                  {hasBar && (
                    <td className={style.td}>
                      {row.bar && (
                        <span
                          className={`inline-block h-2 rounded align-middle ${bars[row.bar.tone ?? "base"]}`}
                          style={{ width: row.bar.width }}
                        />
                      )}
                    </td>
                  )}
                </>
              )}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

function BlockView({ block, skin }: { block: Block; skin: Skin }) {
  const style = STYLES[skin];
  switch (block.kind) {
    case "p":
      return (
        <p className={block.tone ? toneClass(block.tone, skin) : undefined}>
          <Spans spans={block.spans} skin={skin} />
        </p>
      );
    case "list":
      return (
        <ul className={style.list}>
          {block.items.map((item, i) => (
            <li key={i}>
              {skin === "terminal" ? "- " : null}
              <Spans spans={item} skin={skin} />
            </li>
          ))}
        </ul>
      );
    case "table":
      return <TableView block={block} skin={skin} />;
    case "ticket":
      return (
        <div className={style.ticket}>
          <p className="font-bold">{block.title}</p>
          {block.fields.map(field => (
            <p key={field.label} className={`mt-1.5 ${style.ticketText}`}>
              <b className="font-semibold">{`${field.label}:`}</b>
              {` ${field.text}`}
            </p>
          ))}
        </div>
      );
    case "note":
      return (
        <p>
          <span className={style.note}>{block.text}</span>
        </p>
      );
    case "byFileAccess":
      return <BlockList blocks={FILE_ACCESS_SKINS.has(skin) ? block.withFiles : block.withoutFiles} skin={skin} />;
  }
}

function BlockList({ blocks, skin }: { blocks: Block[]; skin: Skin }) {
  return (
    <div className="flex flex-col gap-2">
      {blocks.map((block, i) => (
        <BlockView key={i} block={block} skin={skin} />
      ))}
    </div>
  );
}

function ToolLine({ call, skin }: { call: ToolCall; skin: Skin }) {
  const style = STYLES[skin];
  if (skin === "terminal") {
    return (
      <span className={style.tool}>
        <span className="text-[var(--lp-accent)]">{"✓ "}</span>
        {`${call.name}(${call.args ?? ""})`}
      </span>
    );
  }
  if (skin === "vscode") {
    return (
      <span className={style.tool}>
        <span className="text-[var(--lp-term-ok)]">{"✓ "}</span>
        {call.local ? "Ran terminal command: " : "Ran "}
        <code className="font-mono">{call.local ? call.args : call.name}</code>
        {call.local ? null : " · Autopilot Monitor (MCP Server)"}
      </span>
    );
  }
  return (
    <span className={style.tool}>
      <span aria-hidden="true">✓</span>
      {call.name}
    </span>
  );
}

function PanelHead({ skin }: { skin: Skin }) {
  const row = "flex min-h-[46px] flex-wrap items-center gap-x-2.5 gap-y-1.5 px-3.5 py-2.5 text-[13px]";
  switch (skin) {
    case "chat":
      return (
        <div className={row}>
          <span className="font-semibold text-[var(--lp-ink)]">Enrollment questions</span>
          <span className="ml-auto inline-flex items-center gap-1.5 rounded-full bg-[var(--lp-accent-soft)] px-2 py-0.5 text-xs font-semibold text-[var(--lp-accent-ink)]">
            <span className="h-1.5 w-1.5 rounded-full bg-[var(--lp-accent)]" />
            Autopilot Monitor
          </span>
        </div>
      );
    case "vscode":
      return (
        <div className={row}>
          <span className="text-[11px] font-semibold uppercase tracking-[0.14em]">Chat</span>
          <span className="ml-auto text-xs">Agent · Autopilot Monitor (MCP)</span>
        </div>
      );
    case "terminal":
      return (
        <div className={row}>
          <span className="inline-flex gap-1.5" aria-hidden="true">
            <span className="h-2.5 w-2.5 rounded-full bg-[#f16057]" />
            <span className="h-2.5 w-2.5 rounded-full bg-[#f5bd4f]" />
            <span className="h-2.5 w-2.5 rounded-full bg-[#57c454]" />
          </span>
          <span className="ml-1 min-w-0 truncate font-mono text-[11px] text-[var(--lp-term-faint)]">
            terminal — AI agent · autopilot-monitor MCP connected
          </span>
        </div>
      );
    case "selfhosted":
      return (
        <div className={row}>
          <span className="min-w-0 truncate rounded-md bg-[var(--lp-surface-2)] px-2.5 py-1 font-mono text-xs text-[var(--lp-ink-soft)]">
            chat.contoso.internal
          </span>
          <span className="rounded-full bg-[var(--lp-surface-2)] px-2 py-0.5 text-xs font-semibold text-[var(--lp-ink-soft)]">
            Model: your choice
          </span>
          <span className="ml-auto rounded-full bg-[var(--lp-accent-soft)] px-2 py-0.5 text-xs font-semibold text-[var(--lp-accent-ink)]">
            Runs on your server
          </span>
        </div>
      );
  }
}

function InputLine({ skin }: { skin: Skin }) {
  const style = STYLES[skin];
  if (skin === "terminal") {
    return (
      <div className={style.input}>
        <span className="text-[var(--lp-accent)]">{"❯ "}</span>
        <span className="lp-cursor" />
      </div>
    );
  }
  return <div className={style.input}>{skin === "selfhosted" ? "Message your assistant…" : "Ask a follow-up…"}</div>;
}

export function AssistantPanel({ skin, question }: { skin: Skin; question: Question }) {
  const style = STYLES[skin];
  const calls = question.tools.filter(call => !call.local || FILE_ACCESS_SKINS.has(skin));
  return (
    <div
      className={`grid overflow-hidden rounded-[18px] border shadow-[0_30px_60px_-30px_rgba(24,24,27,0.28)] transition-colors ${style.frame}`}
    >
      {skin === "vscode" && (
        <div aria-hidden="true" className="border-r border-[var(--lp-term-line)] bg-[var(--lp-term-deep)] py-3">
          {[0, 1, 2, 3].map(i => (
            <span
              key={i}
              className={`mx-auto mb-4 block h-5 w-5 rounded-[5px] border-2 ${
                i === 2 ? "border-[var(--lp-term-ink)]" : "border-[var(--lp-term-faint)] opacity-55"
              }`}
            />
          ))}
        </div>
      )}
      <div className="flex min-w-0 flex-col">
        <div className={style.head}>
          <PanelHead skin={skin} />
        </div>
        <div key={question.id} className={`lp-event-in flex flex-1 flex-col px-[18px] pb-1.5 pt-[18px] lg:min-h-[440px] ${style.body}`}>
          {skin === "terminal" ? (
            <div>
              <span className="text-[var(--lp-accent)]">{"❯ "}</span>
              {question.text}
            </div>
          ) : (
            <div className={style.user}>{question.text}</div>
          )}
          <div className={style.tools}>
            {calls.map(call => (
              <ToolLine key={`${call.name}-${call.args ?? ""}`} call={call} skin={skin} />
            ))}
          </div>
          <div className={skin === "chat" || skin === "selfhosted" ? "max-w-[94%]" : undefined}>
            {style.who && (
              <div className={`mb-1 text-xs font-semibold ${style.dark ? "text-[var(--lp-term-faint)]" : "text-[var(--lp-ink-faint)]"}`}>
                {style.who}
              </div>
            )}
            <BlockList blocks={question.answer} skin={skin} />
          </div>
        </div>
        <div className="px-3.5 pb-3.5 pt-2.5">
          <InputLine skin={skin} />
        </div>
      </div>
    </div>
  );
}
