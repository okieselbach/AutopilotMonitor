import { PageSkeleton } from "@/components/skeletons/PageSkeleton";

export default function Loading() {
  return <PageSkeleton cards={3} rows={10} />;
}
