import { describe, it, expect } from "vitest";
import type { DiagnosisCulprit, DiagnosisHeadline } from "@/lib/generated/wire-types.generated";
import { verdictText } from "../verdictText";

const headline = (over: Partial<DiagnosisHeadline>): DiagnosisHeadline => Object.assign({ outcome: "failed", cause: "unknown", verdictPath: "agent:failed" }, over);

const appFailure = headline({
  cause: "esp_app_failure",
  failedStep: { category: "AccountSetup", subcategory: "Apps", errorCode: "0x80d03805", at: "2026-10-13T14:44:24Z" },
  culprits: [{ kind: "app", evidence: "install_failed", name: "Contoso Notes", targeted: "User", errorCode: "0x80d03805", installStarts: 4 }],
  espConfig: { continueAnyway: false, timeoutMinutes: 40 },
});

describe("verdictText", () => {
  it("names the failed step, the app and why the ESP stopped", () => {
    expect(verdictText(appFailure)).toEqual({
      tone: "red",
      title: "Enrollment failed in Account setup › Apps",
      heading: "Why it failed",
      summary: "Contoso Notes could not be installed, so the ESP ended the enrollment.",
      details: [
        "The app is required for the user.",
        "It failed with 0x80d03805 after 4 install starts.",
        "This ESP does not let users continue after an install error.",
      ],
    });
  });

  it("counts several failed apps and tells a detection failure from an install error", () => {
    const two = verdictText(
      headline({
        cause: "esp_app_failure",
        culprits: [
          { kind: "app", evidence: "install_failed", name: "Contoso Notes", detectionResult: "NotDetected" },
          { kind: "app", evidence: "presumed_failed", name: "Contoso VPN" },
        ],
      })
    );
    expect(two.summary).toBe("2 required apps failed, so the ESP ended the enrollment.");
    expect(two.details).toContain("It finished installing, but its detection rule did not find the app.");
  });

  it("says an app never started when the ESP timed out on it", () => {
    const v = verdictText(headline({ cause: "esp_app_timeout", culprits: [{ kind: "app", evidence: "never_started", name: "Contoso Agent" }], espConfig: { timeoutMinutes: 60 } }));
    expect(v.summary).toBe("The ESP stopped while Contoso Agent had not started installing.");
    expect(v.details).toEqual(["The ESP time limit is 60 minutes."]);
  });

  it.each([
    [{ outcome: "incomplete", cause: "agent_gave_up", verdictPath: "maxlife:r6" }, "slate", "Enrollment incomplete", "The agent reached its maximum lifetime before the enrollment finished."],
    [{ outcome: "incomplete", cause: "agent_gave_up", verdictPath: "stopwait:r5_incomplete" }, "slate", "Enrollment incomplete", "The agent stopped waiting for the user to sign in."],
    [{ outcome: "incomplete", cause: "sweep_timeout", verdictPath: "sweep:r6" }, "slate", "Enrollment incomplete", "No completion arrived before the timeout, so the platform closed the session."],
    [{ outcome: "stalled", cause: "agent_silent", verdictPath: "sweep:stalled" }, "orange", "Enrollment stalled", "The agent stopped sending data."],
    [{ outcome: "awaiting_user", cause: "awaiting_user", verdictPath: "ingest:wg_awaiting" }, "sky", "Waiting for the user", "Pre-provisioning finished; the device is waiting for its user."],
    [{ outcome: "awaiting_user", cause: "awaiting_user", verdictPath: "sweep:r5_awaiting" }, "sky", "Waiting for the user", "Device setup finished; the enrollment is waiting for the user."],
    [{ outcome: "succeeded", cause: "reconciled", verdictPath: "sweep:r4" }, "green", "Enrollment succeeded", "The platform declared this session complete without a completion report."],
    [{ outcome: "succeeded", cause: "soft_failure", verdictPath: "agent:complete_soft" }, "amber", "Enrollment succeeded after an ESP error", "The ESP failed, the user continued anyway, and setup completed."],
    [{ outcome: "failed", cause: "rule_ko", verdictPath: "rule:ANALYZE-APP-013", ruleId: "ANALYZE-APP-013" }, "red", "Enrollment failed", "The analyze rule ANALYZE-APP-013 marked this session as failed."],
    [{ outcome: "failed", cause: "admin_marked", verdictPath: "manual:failed" }, "red", "Enrollment failed", "An administrator marked this session as failed."],
    [{ outcome: "incomplete", cause: "superseded", verdictPath: "register:superseded" }, "slate", "Enrollment incomplete", "A newer enrollment of this device replaced this session."],
    [{ outcome: "in_progress", cause: "none", verdictPath: "register:new" }, "blue", "Enrollment in progress", "The enrollment is still running."],
  ] as const)("%j", (over, tone, title, summary) => {
    const v = verdictText(headline(over));
    expect(v.tone).toBe(tone);
    expect(v.title).toBe(title);
    expect(v.summary).toBe(summary);
  });

  it("says what happened to the app of a soft failure", () => {
    const soft = (culprit: DiagnosisCulprit) => verdictText(headline({ outcome: "succeeded", cause: "soft_failure", culprits: [culprit] })).details;
    expect(soft({ kind: "app", evidence: "install_failed", name: "Contoso Notes", errorCode: "0x80070652" })).toEqual(["Contoso Notes failed with 0x80070652."]);
    expect(soft({ kind: "app", evidence: "not_completed", name: "Contoso Notes" })).toEqual(["Contoso Notes had not finished when the ESP failed."]);
  });

  it("falls back to the reason text when the cause is unknown", () => {
    expect(verdictText(headline({ reasonText: "Session timed out after 5 hours" })).summary).toBe("Session timed out after 5 hours");
    expect(verdictText(headline({})).summary).toBe("The outcome could not be attributed.");
  });

  it("explains an agent-level failure by its reason", () => {
    const v = verdictText(headline({ cause: "agent_failure", failedStep: { reason: "oobe_update_not_finished", at: "2026-10-13T14:00:00Z" } }));
    expect(v.summary).toBe("The Windows update at the end of OOBE did not finish.");
  });
});
