/**
 * Content of the /ai question gallery: topics, example questions, the MCP tools each one calls and
 * the answer, as typed blocks. Data instead of JSX, so every string keeps its own spaces (Turbopack
 * trims JSX text at inline-element boundaries) and the tool names can be checked against the MCP
 * server (components/ai/__tests__/questions.test.ts). All numbers and names are sample data.
 */

export type Skin = "chat" | "vscode" | "terminal" | "selfhosted";
export type Tone = "ok" | "warn" | "high" | "dim";
export type TopicId = "troubleshoot" | "fleet" | "apps" | "security" | "reporting";

/** Plain text, or a text run with emphasis. Strings carry their own leading/trailing spaces. */
export type Span = string | { text: string; tone?: Tone; bold?: boolean; code?: boolean };

export interface TableRow {
  cells: Span[];
  /** Bar drawn in an extra last column, width in px. */
  bar?: { width: number; tone?: "warn" | "dim" };
  /** One cell spanning the whole table (a "… and N more" line). */
  fullWidth?: boolean;
}

export type Block =
  | { kind: "p"; spans: Span[]; tone?: Tone }
  | { kind: "list"; items: Span[][] }
  | { kind: "table"; head?: string[]; numeric?: number[]; rows: TableRow[] }
  | { kind: "ticket"; title: string; fields: { label: string; text: string }[] }
  | { kind: "note"; text: string }
  /** The answer differs between clients that can read local files and those that cannot. */
  | { kind: "byFileAccess"; withFiles: Block[]; withoutFiles: Block[] };

/** An MCP tool call, or (`local`) a command a file-capable client runs on its own machine. */
export interface ToolCall {
  name: string;
  args?: string;
  local?: true;
}

export interface Question {
  id: string;
  topic: TopicId;
  text: string;
  tools: ToolCall[];
  answer: Block[];
}

export const TOPICS: { id: TopicId; label: string }[] = [
  { id: "troubleshoot", label: "Troubleshoot" },
  { id: "fleet", label: "Fleet" },
  { id: "apps", label: "Apps" },
  { id: "security", label: "Security" },
  { id: "reporting", label: "Reporting" },
];

export const SKINS: { id: Skin; label: string; note: string }[] = [
  { id: "chat", label: "Chat app", note: "Claude on the web, desktop or mobile, or ChatGPT." },
  { id: "vscode", label: "VS Code", note: "VS Code with GitHub Copilot in agent mode, next to your scripts." },
  { id: "terminal", label: "Terminal", note: "Claude Code, Codex or Gemini CLI. They can also download the diagnostics package." },
  { id: "selfhosted", label: "Self-hosted", note: "A chat front end your organization hosts, with the model you choose." },
];

/** Clients with file access can download the diagnostics package and search it locally. */
export const FILE_ACCESS_SKINS: ReadonlySet<Skin> = new Set<Skin>(["vscode", "terminal"]);

const dim = (text: string): Span => ({ text, tone: "dim" });
const code = (text: string): Span => ({ text, code: true });
const label = (text: string, tone: Tone): Span => ({ text, tone, bold: true });

export const QUESTIONS: Question[] = [
  {
    id: "t1",
    topic: "troubleshoot",
    text: "CONTOSO-3812 failed enrollment this morning. What happened?",
    tools: [
      { name: "search_sessions", args: '{ deviceName: "CONTOSO-3812" }' },
      { name: "get_session_summary", args: '{ sessionId: "e6a1…f43b" }' },
    ],
    answer: [
      { kind: "p", spans: ["Two attempts today. The first failed, the retry succeeded."] },
      {
        kind: "list",
        items: [
          [label("✗ Failed", "high"), " 11:05–11:17 · ESP error ", code("0x8007EA61"), " · 7 of 8 apps"],
          [label("✓ Succeeded", "ok"), " 12:05–12:20 · 8 of 8 apps"],
        ],
      },
      {
        kind: "p",
        spans: [
          label("Cause:", "warn"),
          " Contoso CRM exited with ",
          code("60001"),
          ". The Win32 app has no mapping for that code, so the ESP stopped. ",
          code("0x8007EA61"),
          " is 60001 as an HRESULT.",
        ],
      },
      { kind: "p", tone: "dim", spans: ["60001 is PSADT's generic code for an unhandled error."] },
      { kind: "p", spans: [label("Fix:", "ok"), " map 60001 to retry, or handle the error inside the package."] },
    ],
  },
  {
    id: "t2",
    topic: "troubleshoot",
    text: "What does 0x87D1041C mean, and where do we see it?",
    tools: [
      { name: "lookup_error_code", args: '{ code: "0x87D1041C" }' },
      { name: "search_events", args: '{ query: "app not detected after install" }' },
    ],
    answer: [
      { kind: "p", spans: [code("0x87D1041C"), " means the app was not detected after the installer reported success."] },
      {
        kind: "p",
        tone: "dim",
        spans: ["The usual cause is a detection rule that checks a file, version or registry value the installer does not write."],
      },
      { kind: "p", spans: [label("Seen in 14 sessions", "warn"), " in the last 30 days, all with Contoso VPN 7.1."] },
      { kind: "p", spans: [label("Next step:", "ok"), " compare the detection rule with what version 7.1 installs."] },
    ],
  },
  {
    id: "t3",
    topic: "troubleshoot",
    text: "Get the diagnostics package of that session and check the IME log.",
    tools: [
      { name: "get_session_diagnostics", args: '{ sessionId: "e6a1…f43b" }' },
      { name: "Bash", args: 'unzip -q diag.zip && grep -n "Contoso CRM" AppWorkload.log', local: true },
    ],
    answer: [
      {
        kind: "byFileAccess",
        withoutFiles: [
          { kind: "p", spans: ["Here is a download link for the diagnostics package. It stays valid for about 10 minutes."] },
          {
            kind: "p",
            tone: "dim",
            spans: [
              "I can't open ZIP files in this chat. A client with file access, such as Claude Code or VS Code, unpacks the package and searches the IME logs for you.",
            ],
          },
        ],
        withFiles: [
          { kind: "p", spans: ["Downloaded the package and searched AppWorkload.log:"] },
          {
            kind: "table",
            rows: [
              { cells: [dim("11:13:26"), "Contoso CRM: install started, attempt 1"] },
              { cells: [dim("11:14:58"), { text: "Installer exited with 60001 after 92 s", tone: "high" }] },
              { cells: [dim("11:14:58"), "No mapping for 60001, install reported as failed"] },
              { cells: [dim("12:09:41"), { text: "Attempt 2: installed in 75 s", tone: "ok" }] },
            ],
          },
          { kind: "p", spans: [label("Result:", "ok"), " the package works on a second run. The failure is transient."] },
        ],
      },
    ],
  },
  {
    id: "f1",
    topic: "fleet",
    text: "Where does our enrollment time go, and what would save the most?",
    tools: [{ name: "get_time_attribution" }],
    answer: [
      { kind: "p", spans: ["Median user-driven enrollment over the last 30 days: ", { text: "38 min", bold: true }, "."] },
      {
        kind: "table",
        head: ["Phase", "Median"],
        numeric: [1],
        rows: [
          { cells: ["Device preparation", "4 min"], bar: { width: 25 } },
          { cells: [{ text: "Device setup apps", tone: "warn" }, { text: "19 min", tone: "warn" }], bar: { width: 120, tone: "warn" } },
          { cells: ["Windows Hello", "2 min"], bar: { width: 13 } },
          { cells: ["Account setup", "9 min"], bar: { width: 57 } },
          { cells: ["Desktop handoff", "1 min"], bar: { width: 6 } },
          { cells: [dim("Not attributed"), dim("3 min")], bar: { width: 19, tone: "dim" } },
        ],
      },
      {
        kind: "p",
        spans: [
          label("Biggest lever:", "warn"),
          " Contoso CAD Suite blocks device setup for 7 min. Installing it after setup would save up to 7 min per device.",
        ],
      },
    ],
  },
  {
    id: "f2",
    topic: "fleet",
    text: "How many devices enrolled right the first time last month?",
    tools: [
      { name: "get_device_history", args: "{ days: 30 }" },
      { name: "search_sessions", args: '{ fields: "serialNumber,model,status" }' },
    ],
    answer: [
      { kind: "p", spans: [label("First-time-right: 412 of 448 devices (92%).", "ok"), " 36 devices needed two or more attempts."] },
      { kind: "p", spans: [label("21 of the 36 are the same model:", "warn"), " Contoso Book 14 G3."] },
      { kind: "p", spans: ["Check the driver and firmware apps for that model first."] },
    ],
  },
  {
    id: "f3",
    topic: "fleet",
    text: "Are enrollments at the Munich office slower than elsewhere?",
    tools: [{ name: "get_geographic_metrics", args: '{ groupBy: "city" }' }],
    answer: [
      {
        kind: "table",
        head: ["Location", "Sessions", "Median", "Success"],
        numeric: [1, 2, 3],
        rows: [
          { cells: [{ text: "Munich", tone: "warn" }, "64", { text: "52 min", tone: "warn" }, "97%"] },
          { cells: ["All sites", "487", "38 min", "95%"] },
        ],
      },
      {
        kind: "p",
        spans: [
          "The devices in Munich don't fail, they are slow. API latency matches the other sites, so the distance to the backend is not the cause.",
        ],
      },
      { kind: "p", spans: [label("Next step:", "ok"), " check the proxy and Delivery Optimization at that site."] },
    ],
  },
  {
    id: "p1",
    topic: "apps",
    text: "Which app breaks our enrollments most often?",
    tools: [{ name: "get_app_install_metrics", args: "{ days: 30 }" }],
    answer: [
      { kind: "p", spans: ["Top failing apps in the last 30 days:"] },
      {
        kind: "table",
        head: ["App", "Failures", "Rate", "Most common code"],
        numeric: [1, 2],
        rows: [
          { cells: [{ text: "Contoso VPN 7.1", tone: "high" }, { text: "14", tone: "high" }, "6.2%", code("0x87D1041C")] },
          { cells: ["Contoso CRM 4.2", "5", "2.4%", code("exit 60001")] },
          { cells: ["Printer Pack 2.0", "3", "0.7%", code("0x80070643")] },
        ],
      },
      { kind: "p", spans: [label("Takeaway:", "ok"), " Contoso VPN alone causes more than half of all app failures."] },
    ],
  },
  {
    id: "p2",
    topic: "apps",
    text: "Which apps keep device setup waiting the longest?",
    tools: [{ name: "get_time_attribution" }],
    answer: [
      { kind: "p", spans: ["Apps that block device setup, by median active install time:"] },
      {
        kind: "table",
        head: ["App", "Install time"],
        numeric: [1],
        rows: [
          { cells: [{ text: "Contoso CAD Suite", tone: "warn" }, { text: "7 min 10 s", tone: "warn" }], bar: { width: 120, tone: "warn" } },
          { cells: ["Microsoft 365 Apps", "4 min 40 s"], bar: { width: 78 } },
          { cells: ["Contoso VPN 7.1", "1 min 50 s"], bar: { width: 31 } },
        ],
      },
      { kind: "p", spans: [label("Biggest saving:", "ok"), " taking CAD Suite off the blocking list saves up to 7 min per device."] },
    ],
  },
  {
    id: "p3",
    topic: "apps",
    text: "How much install traffic came from peers or Connected Cache?",
    tools: [{ name: "get_app_install_metrics", args: "{ days: 30 }" }],
    answer: [
      { kind: "p", spans: ["1.8 TB were downloaded for app installs in the last 30 days."] },
      {
        kind: "table",
        head: ["Source", "Share"],
        numeric: [1],
        rows: [
          { cells: [{ text: "Peers and Microsoft Connected Cache", tone: "ok" }, { text: "62%", tone: "ok" }], bar: { width: 120 } },
          { cells: ["Internet", "38%"], bar: { width: 74, tone: "dim" } },
        ],
      },
      { kind: "p", spans: ["That kept about 1.1 TB off your internet line."] },
    ],
  },
  {
    id: "s1",
    topic: "security",
    text: "How exposed are the devices we enrolled this month?",
    tools: [{ name: "get_vulnerability_summary", args: "{ days: 30 }" }],
    answer: [
      { kind: "p", spans: ["17 distinct CVEs across 236 devices."] },
      { kind: "p", spans: [label("Act now:", "high"), " 2 CVEs are on CISA's Known Exploited list."] },
      {
        kind: "table",
        head: ["Software", "Priority", "CVSS", "Devices"],
        numeric: [2, 3],
        rows: [
          { cells: ["Contoso PDF Reader 11.2", { text: "KEV", tone: "high" }, "7.8", "188"] },
          { cells: ["Contoso Archiver 3.1", { text: "KEV", tone: "high" }, "8.8", "9"] },
        ],
      },
      { kind: "p", spans: [label("Attend:", "warn"), " 4 CVEs with a high exploit probability (EPSS). ", dim("Track: 11.")] },
      { kind: "p", spans: [label("Start with PDF Reader:", "ok"), " one update covers 188 devices."] },
      { kind: "note", text: "Uses the optional software inventory with CVE matching." },
    ],
  },
  {
    id: "s2",
    topic: "security",
    text: "Which devices still get the vulnerable PDF Reader?",
    tools: [{ name: "search_sessions_by_cve", args: '{ cveId: "CVE-2026-…" }' }],
    answer: [
      { kind: "p", spans: ["188 sessions in the last 30 days. ", label("All of them report version 11.2, none has 11.3 yet.", "warn")] },
      {
        kind: "table",
        head: ["Device", "Model", "Enrolled"],
        rows: [
          { cells: ["CONTOSO-4471", "Contoso Book 14 G3", "today 09:12"] },
          { cells: ["CONTOSO-4466", "Contoso Book 14 G3", "today 08:47"] },
          { cells: ["CONTOSO-4459", "Contoso Desk 7", "yesterday 16:05"] },
          { cells: [dim("… and 185 more")], fullWidth: true },
        ],
      },
      { kind: "p", tone: "dim", spans: ["Want the full list as a CSV for the patch team?"] },
    ],
  },
  {
    id: "s3",
    topic: "security",
    text: "Did any device enroll with an old TPM version?",
    tools: [{ name: "search_sessions", args: '{ deviceProperties: { "tpm_status.specVersion": "1.2" } }' }],
    answer: [{ kind: "p", spans: [label("No.", "ok"), " All 448 devices from the last 30 days report TPM 2.0."] }],
  },
  {
    id: "r1",
    topic: "reporting",
    text: "Write this week's enrollment report for the change advisory board.",
    tools: [
      { name: "get_metrics", args: "{ days: 7 }" },
      { name: "get_device_history", args: "{ days: 7 }" },
      { name: "get_app_install_metrics", args: "{ days: 7 }" },
    ],
    answer: [
      { kind: "p", spans: [{ text: "Enrollment report, week 40", bold: true }] },
      {
        kind: "list",
        items: [
          ["118 enrollments, 95% succeeded (last week 91%)"],
          ["First-time-right: 93%"],
          ["Median duration 37 min, 4 min faster than last week"],
          [{ text: "Open issue: Contoso VPN 7.1 detection rule, 4 failures", tone: "warn" }],
        ],
      },
      { kind: "p", tone: "dim", spans: ["Want it as a table or as an email draft?"] },
    ],
  },
  {
    id: "r2",
    topic: "reporting",
    text: "Draft a ticket for the packaging team about the VPN failures.",
    tools: [
      { name: "search_events", args: '{ query: "Contoso VPN not detected" }' },
      { name: "get_session_summary", args: "… × 3" },
    ],
    answer: [
      { kind: "p", spans: ["Here is a draft:"] },
      {
        kind: "ticket",
        title: "Contoso VPN 7.1 not detected after install (0x87D1041C)",
        fields: [
          { label: "Impact", text: "14 failed installs in 30 days, all during device setup." },
          { label: "Evidence", text: "3 example sessions with timeline, linked below." },
          { label: "Likely cause", text: "the detection rule does not match version 7.1." },
          { label: "Ask", text: "align the detection rule with 7.1 and test it on one device." },
        ],
      },
    ],
  },
  {
    id: "r3",
    topic: "reporting",
    text: "Summarize last month for my manager in five sentences.",
    tools: [
      { name: "get_metrics", args: "{ days: 30 }" },
      { name: "get_time_attribution" },
    ],
    answer: [
      {
        kind: "p",
        spans: [
          "In September we enrolled 448 devices, and 95% succeeded. 92% of them worked on the first attempt. A typical enrollment took 38 minutes. Most failures came from one VPN package, which packaging is fixing. Moving one large app out of device setup could save up to 7 minutes per device.",
        ],
      },
    ],
  },
];
