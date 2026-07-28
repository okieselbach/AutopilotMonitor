import { PageSkeleton } from "@/components/skeletons/PageSkeleton";

export default function Loading() {
  return <PageSkeleton cards={4} rows={6} />;
}
