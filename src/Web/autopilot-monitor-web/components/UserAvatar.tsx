"use client";

import { useUserPhoto } from "@/hooks/useUserPhoto";

function initialsOf(displayName?: string, upn?: string): string {
  if (displayName) {
    const names = displayName.split(' ');
    if (names.length >= 2) {
      return `${names[0].charAt(0)}${names[names.length - 1].charAt(0)}`.toUpperCase();
    }
    return displayName.charAt(0).toUpperCase();
  }
  return upn?.charAt(0).toUpperCase() || 'U';
}

const SIZE_CLASSES = {
  sm: "w-7 h-7",
  md: "w-8 h-8 flex-shrink-0",
} as const;

interface UserAvatarProps {
  displayName?: string;
  upn?: string;
  size: keyof typeof SIZE_CLASSES;
}

/** The user's Entra ID profile photo in the avatar circle; initials until (or unless) one loads. */
export function UserAvatar({ displayName, upn, size }: UserAvatarProps) {
  const photo = useUserPhoto();

  if (photo) {
    return (
      // data: URL from Microsoft Graph — nothing for next/image to optimize
      // eslint-disable-next-line @next/next/no-img-element
      <img src={photo} alt="" className={`${SIZE_CLASSES[size]} rounded-full object-cover`} />
    );
  }

  return (
    <div className={`${SIZE_CLASSES[size]} rounded-full bg-green-600 flex items-center justify-center text-white font-semibold text-xs`}>
      {initialsOf(displayName, upn)}
    </div>
  );
}
