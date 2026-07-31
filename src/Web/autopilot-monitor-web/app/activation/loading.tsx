import { PageSkeleton } from "@/components/skeletons/PageSkeleton";

export default function Loading() {
  return <PageSkeleton cards={0} rows={6} />;
}
