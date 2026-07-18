import { ImageResponse } from "next/og";

export const alt = "AutopilotMonitor – Real-Time Windows Enrollment Monitoring";
export const size = { width: 1200, height: 630 };
export const contentType = "image/png";

export default async function Image() {
  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          display: "flex",
          flexDirection: "column",
          justifyContent: "center",
          alignItems: "center",
          background: "linear-gradient(135deg, #4a5de8 0%, #3644b5 50%, #2a3490 100%)",
          fontFamily: "Inter, sans-serif",
        }}
      >
        {/* Chart icon */}
        <div style={{ display: "flex", alignItems: "flex-end", gap: "16px", marginBottom: "40px" }}>
          <div
            style={{
              width: "40px",
              height: "100px",
              backgroundColor: "rgba(255,255,255,0.85)",
              borderRadius: "8px",
            }}
          />
          <div
            style={{
              width: "40px",
              height: "140px",
              backgroundColor: "rgba(255,255,255,0.9)",
              borderRadius: "8px",
            }}
          />
          <div
            style={{
              width: "40px",
              height: "180px",
              backgroundColor: "white",
              borderRadius: "8px",
            }}
          />
        </div>

        {/* Title */}
        <div
          style={{
            fontSize: "64px",
            fontWeight: 700,
            color: "white",
            letterSpacing: "-1px",
            marginBottom: "16px",
          }}
        >
          AutopilotMonitor
        </div>

        {/* Tagline */}
        <div
          style={{
            fontSize: "28px",
            fontWeight: 400,
            color: "rgba(255,255,255,0.85)",
            marginBottom: "48px",
          }}
        >
          Real-Time Windows Enrollment Monitoring
        </div>

        {/* URL */}
        <div
          style={{
            fontSize: "20px",
            fontWeight: 500,
            color: "rgba(255,255,255,0.6)",
            letterSpacing: "1px",
          }}
        >
          www.autopilotmonitor.com
        </div>
      </div>
    ),
    { ...size },
  );
}
