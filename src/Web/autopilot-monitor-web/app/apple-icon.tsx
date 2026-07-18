import { ImageResponse } from "next/og";

export const size = { width: 180, height: 180 };
export const contentType = "image/png";

export default async function Icon() {
  return new ImageResponse(
    (
      <div
        style={{
          width: "100%",
          height: "100%",
          display: "flex",
          alignItems: "flex-end",
          justifyContent: "center",
          background: "linear-gradient(135deg, #4a5de8 0%, #3644b5 100%)",
          borderRadius: "40px",
          padding: "30px 30px 36px 30px",
          gap: "14px",
        }}
      >
        <div
          style={{
            width: "28px",
            height: "60px",
            backgroundColor: "rgba(255,255,255,0.85)",
            borderRadius: "6px",
          }}
        />
        <div
          style={{
            width: "28px",
            height: "80px",
            backgroundColor: "rgba(255,255,255,0.9)",
            borderRadius: "6px",
          }}
        />
        <div
          style={{
            width: "28px",
            height: "100px",
            backgroundColor: "white",
            borderRadius: "6px",
          }}
        />
      </div>
    ),
    { ...size },
  );
}
